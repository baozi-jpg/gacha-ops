using System.Net.Http.Headers;
using System.Text.Json;

namespace GachaOps.Core.Services;

public interface IGitHubReleaseClient
{
    Task<string> GetLatestVersionAsync(
        string repository,
        CancellationToken cancellationToken = default);
}

public sealed class GitHubReleaseClient : IGitHubReleaseClient
{
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _client;

    public GitHubReleaseClient(HttpClient? client = null)
    {
        _client = client ?? SharedClient;
    }

    public async Task<string> GetLatestVersionAsync(
        string repository,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository) || repository.Count(character => character == '/') != 1)
        {
            throw new ArgumentException("GitHub 仓库名称无效。", nameof(repository));
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{repository}/releases/latest");
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("tag_name", out var tagNode)
            || string.IsNullOrWhiteSpace(tagNode.GetString()))
        {
            throw new InvalidDataException("官方版本接口未返回 tag_name。");
        }

        return tagNode.GetString()!;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GachaOps", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
}
