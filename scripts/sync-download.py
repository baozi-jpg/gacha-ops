"""Mirror the latest stable GitHub release to R2 using gh and AWS CLI."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import urllib.error
import urllib.request
import zipfile


LATEST_KEY = "latest/GachaOps-win-x64.zip"


def command(*args):
    result = subprocess.run(args, capture_output=True, text=True,
                            encoding="utf-8", errors="replace", timeout=600)
    if result.returncode:
        # Do not echo CLI output: a command may include credential diagnostics.
        raise RuntimeError(f"{args[0]} {args[1]} failed (exit {result.returncode}).")
    return result.stdout


def latest_release(repo):
    release = json.loads(command("gh", "api", f"repos/{repo}/releases/latest"))
    tag = release.get("tag_name", "")
    if release.get("draft") or release.get("prerelease"):
        raise ValueError("Only stable, published releases may update the download.")
    if not re.fullmatch(r"v\d+\.\d+\.\d+", tag):
        raise ValueError("Expected a stable vMAJOR.MINOR.PATCH release tag.")
    name = f"GachaOps-{tag}-win-x64.zip"
    assets = [a for a in release.get("assets", []) if a.get("name") == name]
    if len(assets) != 1 or assets[0].get("state") != "uploaded":
        raise ValueError("The release must contain exactly one uploaded Windows ZIP.")
    asset = assets[0]
    digest = asset.get("digest") or ""
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest) or asset.get("size", 0) <= 0:
        raise ValueError("The release asset must include a size and SHA-256 digest.")
    return {"tag": tag, "name": name, "size": asset["size"], "sha256": digest[7:]}


def verify_file(path, release, check_zip=False):
    if path.stat().st_size != release["size"]:
        raise ValueError("Downloaded package size does not match the release.")
    with path.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if digest != release["sha256"]:
        raise ValueError("Downloaded package SHA-256 does not match the release.")
    if check_zip:
        with zipfile.ZipFile(path) as archive:
            if archive.testzip() is not None:
                raise ValueError("ZIP integrity check failed.")
            names = {Path(name).name for name in archive.namelist()}
            required = {"GachaOps.exe", "GachaOps.dll", "GachaOps.deps.json",
                        "GachaOps.runtimeconfig.json"}
            if not required <= names:
                raise ValueError("The ZIP is missing required GachaOps package files.")


def download_release(repo, release, directory):
    command("gh", "release", "download", release["tag"], "--repo", repo,
            "--pattern", release["name"], "--dir", str(directory))
    package = directory / release["name"]
    verify_file(package, release, check_zip=True)
    return package


def mirror(repo, release, package, directory, account, bucket, public_base):
    endpoint = f"https://{account}.r2.cloudflarestorage.com"

    def aws(*args):
        return command("aws", *args, "--endpoint-url", endpoint, "--region", "auto")

    key = f"releases/{release['tag']}/{release['name']}"
    metadata = {"sha256": release["sha256"], "version": release["tag"]}
    disposition = f'attachment; filename="{release["name"]}"'
    # Mutable latest has no cache. Version packages also avoid stale content
    # when a release maintainer replaces an attachment under the same tag.
    aws("s3", "cp", str(package), f"s3://{bucket}/{key}", "--only-show-errors",
        "--no-progress", "--content-type", "application/zip",
        "--content-disposition", disposition, "--cache-control", "no-store",
        "--metadata", json.dumps(metadata))
    uploaded = directory / "uploaded.zip"
    aws("s3", "cp", f"s3://{bucket}/{key}", str(uploaded),
        "--only-show-errors", "--no-progress")
    verify_file(uploaded, release)
    # Resolve latest again just before promotion so a newer publication cannot
    # be replaced with the older package fetched at the start of this run.
    if latest_release(repo) != release:
        raise ValueError("Latest release changed during synchronization; rerun the workflow.")
    aws("s3api", "copy-object", "--bucket", bucket, "--key", LATEST_KEY,
        "--copy-source", f"{bucket}/{key}", "--metadata-directive", "REPLACE",
        "--metadata", json.dumps(metadata), "--content-type", "application/zip",
        "--content-disposition", disposition, "--cache-control", "no-store")
    promoted = directory / "latest.zip"
    aws("s3", "cp", f"s3://{bucket}/{LATEST_KEY}", str(promoted),
        "--only-show-errors", "--no-progress")
    verify_file(promoted, release)
    url = f"{public_base.rstrip('/')}/{LATEST_KEY}"
    request = urllib.request.Request(url, headers={
        "Cache-Control": "no-cache",
        "User-Agent": "GachaOps-Download-Sync/1.0 (+https://gachaops.ma-kabaka.uk)",
    })
    with urllib.request.urlopen(request, timeout=120) as response:
        if "no-store" not in response.headers.get("Cache-Control", "").lower():
            raise ValueError("Public download must return Cache-Control: no-store.")
        with (directory / "public.zip").open("wb") as stream:
            while chunk := response.read(1024 * 1024):
                stream.write(chunk)
    verify_file(directory / "public.zip", release)
    print(f"Synchronized {release['tag']}: {url}")
    print(f"SHA-256: {release['sha256']}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verify-only", action="store_true",
                        help="Download and validate the release without accessing R2.")
    args = parser.parse_args()
    repo = os.environ.get("GITHUB_REPOSITORY", "")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repo):
        raise ValueError("GITHUB_REPOSITORY must be owner/repository.")
    if not args.verify_only:
        account = os.environ.get("R2_ACCOUNT_ID", "")
        bucket = os.environ.get("R2_BUCKET", "")
        public_base = os.environ.get("R2_PUBLIC_BASE_URL", "")
        if not re.fullmatch(r"[0-9a-f]{32}", account):
            raise ValueError("R2_ACCOUNT_ID must be a Cloudflare account ID.")
        if not re.fullmatch(r"[a-z0-9][a-z0-9-]{1,61}[a-z0-9]", bucket):
            raise ValueError("R2_BUCKET must be a valid bucket name.")
        if not re.fullmatch(r"https://[a-z0-9.-]+", public_base):
            raise ValueError("R2_PUBLIC_BASE_URL must be an HTTPS origin without a path.")
        for name in ("AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY"):
            if not os.environ.get(name):
                raise ValueError(f"Required secret {name} is not configured.")
    release = latest_release(repo)
    with tempfile.TemporaryDirectory(prefix="gachaops-download-") as temporary:
        directory = Path(temporary)
        package = download_release(repo, release, directory)
        if args.verify_only:
            print(f"Verified {release['name']}: {release['size']} bytes")
            print(f"SHA-256: {release['sha256']}")
        else:
            mirror(repo, release, package, directory, account, bucket, public_base)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError, OSError, subprocess.TimeoutExpired,
            urllib.error.URLError, zipfile.BadZipFile) as error:
        raise SystemExit(f"Download synchronization failed: {error}") from None
