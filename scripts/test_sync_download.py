import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile


spec = importlib.util.spec_from_file_location(
    "sync_download", Path(__file__).with_name("sync-download.py"))
sync = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sync)


class DownloadSyncTests(unittest.TestCase):
    def setUp(self):
        self.area = tempfile.TemporaryDirectory(prefix="gachaops-sync-test-")
        self.addCleanup(self.area.cleanup)
        self.directory = Path(self.area.name)
        self.package = self.directory / "GachaOps-v1.2.2-win-x64.zip"
        with zipfile.ZipFile(self.package, "w") as archive:
            for name in ("GachaOps.exe", "GachaOps.dll", "GachaOps.deps.json",
                         "GachaOps.runtimeconfig.json"):
                archive.writestr("GachaOps-win-x64/" + name, "fixture")
        self.payload = self.package.read_bytes()
        self.release = {"tag": "v1.2.2", "name": self.package.name,
                        "size": len(self.payload),
                        "sha256": hashlib.sha256(self.payload).hexdigest()}

    def api_release(self):
        return {"tag_name": self.release["tag"], "draft": False,
                "prerelease": False, "assets": [
                    {"name": self.release["name"], "state": "uploaded",
                     "size": self.release["size"],
                     "digest": "sha256:" + self.release["sha256"]}]}

    def test_selects_exact_stable_package_and_rejects_missing_digest(self):
        api = self.api_release()
        with patch.object(sync, "command", return_value=json.dumps(api)):
            self.assertEqual(sync.latest_release("owner/repo"), self.release)
        api["assets"][0]["digest"] = None
        with patch.object(sync, "command", return_value=json.dumps(api)):
            with self.assertRaisesRegex(ValueError, "SHA-256"):
                sync.latest_release("owner/repo")

    def test_command_reads_utf8_output_on_windows(self):
        output = sync.command(sys.executable, "-c",
                              "import sys; sys.stdout.buffer.write('中文'.encode('utf-8'))")
        self.assertEqual(output, "中文")

    def test_rejects_prerelease_and_unsafe_tag(self):
        for override in ({"prerelease": True}, {"tag_name": "../../other"}):
            api = self.api_release() | override
            with patch.object(sync, "command", return_value=json.dumps(api)):
                with self.assertRaises(ValueError):
                    sync.latest_release("owner/repo")

    def test_validates_zip_and_detects_same_size_corruption(self):
        sync.verify_file(self.package, self.release, check_zip=True)
        broken = bytearray(self.payload)
        broken[-1] ^= 1
        self.package.write_bytes(broken)
        with self.assertRaisesRegex(ValueError, "SHA-256"):
            sync.verify_file(self.package, self.release)

    def test_rejects_exe_only_package(self):
        with zipfile.ZipFile(self.package, "w") as archive:
            archive.writestr("GachaOps.exe", "fixture")
        payload = self.package.read_bytes()
        release = self.release | {"size": len(payload),
                                  "sha256": hashlib.sha256(payload).hexdigest()}
        with self.assertRaisesRegex(ValueError, "missing required"):
            sync.verify_file(self.package, release, check_zip=True)

    def run_mirror(self, *, corrupt_upload=False, new_release=False,
                   corrupt_public=False, cache_control="no-store"):
        calls = []

        def fake_command(*args):
            calls.append(args)
            if args[0] == "gh":
                api = self.api_release()
                if new_release:
                    api["assets"][0]["digest"] = "sha256:" + "0" * 64
                return json.dumps(api)
            if args[1:3] == ("s3", "cp") and args[3].startswith("s3://"):
                content = b"bad" if corrupt_upload else self.payload
                Path(args[4]).write_bytes(content)
            return "{}"

        response = io.BytesIO(b"bad" if corrupt_public else self.payload)
        response.headers = {"Cache-Control": cache_control}
        with patch.object(sync, "command", side_effect=fake_command), \
                patch.object(sync.urllib.request, "urlopen", return_value=response), \
                contextlib.redirect_stdout(io.StringIO()):
            try:
                sync.mirror("owner/repo", self.release, self.package, self.directory,
                            "0" * 32, "test-bucket", "https://download.example.com")
            finally:
                self.calls = calls

    def assert_not_promoted(self):
        self.assertFalse(any(call[1:3] == ("s3api", "copy-object")
                             for call in self.calls))

    def test_upload_readback_failure_keeps_latest_untouched(self):
        with self.assertRaisesRegex(ValueError, "size"):
            self.run_mirror(corrupt_upload=True)
        self.assert_not_promoted()

    def test_release_change_keeps_latest_untouched(self):
        with self.assertRaisesRegex(ValueError, "changed"):
            self.run_mirror(new_release=True)
        self.assert_not_promoted()

    def test_success_promotes_verified_package_and_checks_public_download(self):
        self.run_mirror()
        promotion = next(call for call in self.calls if call[1:3] == ("s3api", "copy-object"))
        self.assertEqual(promotion[promotion.index("--key") + 1], sync.LATEST_KEY)
        self.assertEqual(promotion[promotion.index("--cache-control") + 1], "no-store")
        self.assertEqual((self.directory / "public.zip").read_bytes(), self.payload)

    def test_public_corruption_or_cached_response_is_reported_as_failure(self):
        for options in ({"corrupt_public": True}, {"cache_control": "max-age=3600"}):
            with self.subTest(options=options), self.assertRaises(ValueError):
                self.run_mirror(**options)


if __name__ == "__main__":
    unittest.main()
