using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ShiroBot.Plugin.GithubView.Service;

/// <summary>
/// GitHub REST API 通用客户端:统一 UA / Accept / token,提供 JSON 与二进制请求。
/// </summary>
internal sealed class GitHubApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClient _imageHttp;
    private volatile string _token = "";

    public GitHubApiClient()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShiroBot-GithubView", "1.1"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        _imageHttp = new HttpClient();
        _imageHttp.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShiroBot-GithubView", "1.1"));
        _imageHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
    }

    public void SetToken(string? token) => _token = token?.Trim() ?? "";

    public async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        var token = _token;
        if (token.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new GitHubNotFoundException(path);
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.FirstOrDefault() == "0")
        {
            throw new GitHubRateLimitException();
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 通过 per_page=1 + Link header rel="last" 的 page 号统计集合总数。
    /// </summary>
    public async Task<int> GetCollectionCountAsync(string path, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        var token = _token;
        if (token.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Headers.TryGetValues("Link", out var values))
        {
            var count = TryReadLastPage(values);
            if (count is not null)
            {
                return count.Value;
            }
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.GetArrayLength()
            : 0;
    }

    private static int? TryReadLastPage(IEnumerable<string> links)
    {
        foreach (var part in string.Join(',', links).Split(','))
        {
            if (!part.Contains("rel=\"last\"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 用 LastIndexOf:URL 里 per_page= 也含 "page=",分页参数 page= 在其后
            var pageIndex = part.LastIndexOf("page=", StringComparison.OrdinalIgnoreCase);
            if (pageIndex < 0)
            {
                continue;
            }

            pageIndex += "page=".Length;
            var endIndex = pageIndex;
            while (endIndex < part.Length && char.IsDigit(part[endIndex]))
            {
                endIndex++;
            }

            if (int.TryParse(part[pageIndex..endIndex], out var page))
            {
                return page;
            }
        }

        return null;
    }

    public async Task<byte[]?> GetImageAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        try
        {
            return await _imageHttp.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _imageHttp.Dispose();
    }
}

internal sealed class GitHubNotFoundException(string path)
    : Exception($"GitHub 资源不存在: {path}");

internal sealed class GitHubRateLimitException()
    : Exception("GitHub API 限流,请稍后再试(可在配置中设置 Token 提升配额)");

internal static class JsonExtensions
{
    public static string? GetStringOrNull(this JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    public static string? GetStringOrNull(this JsonElement element, string objectName, string propertyName)
        => element.TryGetProperty(objectName, out var obj) && obj.ValueKind == JsonValueKind.Object
            ? obj.GetStringOrNull(propertyName)
            : null;

    public static int GetIntOrZero(this JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;

    public static bool GetBoolOrFalse(this JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True;

    public static DateTimeOffset? GetDateOrNull(this JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
           && property.TryGetDateTimeOffset(out var value)
            ? value
            : null;
}
