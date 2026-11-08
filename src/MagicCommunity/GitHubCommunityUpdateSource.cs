using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MagicCommunity;

// Public release metadata only. No account credentials, background polling,
// installer downloads, or executable content from release notes.
internal sealed class GitHubCommunityUpdateSource : ICommunityUpdateSource
{
    internal const string Repository = "littletaro97-arch/honor-magic-visual-community";
    internal static readonly Uri ReleasesPage = new("https://github.com/" + Repository + "/releases");
    internal static readonly Uri ManifestUrl = new("https://raw.githubusercontent.com/" + Repository + "/main/updates/update.json");
    private const int PageSize = 100, PageLimit = 5, ResponseLimit = 2 * 1024 * 1024;
    private static readonly HttpClient shared = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly HttpClient http;
    private readonly GitHubReleaseVersion current;
    internal GitHubCommunityUpdateSource(HttpClient? http = null, string? version = null)
    {
        this.http = http ?? shared;
        current = GitHubReleaseVersion.Parse(version ?? AppController.Version)
            ?? throw new InvalidDataException("当前版本号无效，无法比较更新。");
    }
    public async Task<CommunityUpdateResult> Check(CancellationToken cancellation)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        total.CancelAfter(TimeSpan.FromSeconds(12));
        using var api = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        api.CancelAfter(TimeSpan.FromSeconds(7));
        try { return await CheckApi(api.Token).ConfigureAwait(false); }
        catch (Exception error) when (Retryable(error) && !cancellation.IsCancellationRequested)
        {
            try { return await CheckManifest(total.Token).ConfigureAwait(false); }
            catch (Exception fallback) when (Retryable(fallback) && !cancellation.IsCancellationRequested)
            {
                throw new HttpRequestException("暂时无法获取更新信息，请稍后重试或打开 GitHub 发布页。", new AggregateException(error, fallback));
            }
        }
    }
    private static bool Retryable(Exception error) => error is HttpRequestException or OperationCanceledException or TimeoutException or JsonException or InvalidDataException;
    internal async Task<CommunityUpdateResult> CheckApi(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var token = deadline.Token;
        CommunityRelease? best = null;
        GitHubReleaseVersion? bestVersion = null;
        try
        {
            for (int page = 1; ; page++)
            {
                if (page > PageLimit) throw new InvalidDataException("发布记录过多，请打开 GitHub 发布页查看。");
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{Repository}/releases?per_page={PageSize}&page={page}");
                request.Headers.UserAgent.ParseAdd("MagicCommunity/" + AppController.Version);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                    throw new HttpRequestException("GitHub 暂时限制了检查请求，请稍后重试。");
                response.EnsureSuccessStatusCode();
                await response.Content.LoadIntoBufferAsync(ResponseLimit, token).ConfigureAwait(false);
                using var data = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 24 });
                var releases = data.RootElement;
                if (releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() > PageSize)
                    throw new InvalidDataException("GitHub 返回的发布列表无效。");
                Select(releases, ref best, ref bestVersion);
                if (releases.GetArrayLength() < PageSize) break;
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("连接 GitHub 超时，请检查网络后重试。");
        }
        cancellation.ThrowIfCancellationRequested();
        return Result(best, bestVersion);
    }
    private async Task<CommunityUpdateResult> CheckManifest(CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
        request.Headers.UserAgent.ParseAdd("MagicCommunity/" + AppController.Version);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.CacheControl = new() { NoCache = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(512 * 1024, cancellation).ConfigureAwait(false);
        using var data = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 16 });
        var root = data.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 1 || Text(root, "repository") != Repository ||
            !root.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() > PageSize)
            throw new InvalidDataException("更新清单格式或仓库身份无效。");
        foreach (var item in releases.EnumerateArray())
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
                !item.TryGetProperty("prerelease", out var preview) || preview.ValueKind is not (JsonValueKind.False or JsonValueKind.True) ||
                GitHubReleaseVersion.Parse(Text(item, "tag_name")) is null ||
                !DateTimeOffset.TryParse(Text(item, "published_at"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                throw new InvalidDataException("更新清单包含未发布或无效版本。");
        CommunityRelease? best = null;GitHubReleaseVersion? bestVersion = null;
        Select(releases, ref best, ref bestVersion);
        cancellation.ThrowIfCancellationRequested();
        return Result(best, bestVersion);
    }
    private void Select(JsonElement releases, ref CommunityRelease? best, ref GitHubReleaseVersion? bestVersion)
    {
        foreach (var item in releases.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("GitHub 返回的发布记录无效。");
            if (Flag(item, "draft")) continue;
            string tag = Text(item, "tag_name");var candidate = GitHubReleaseVersion.Parse(tag);
            if (candidate is null || !current.Prerelease && (candidate.Prerelease || Flag(item, "prerelease"))) continue;
            if (bestVersion is not null && candidate.CompareTo(bestVersion) <= 0) continue;
            string notes = Text(item, "body");if (notes.Length > 65536) throw new InvalidDataException("发布说明过长，请打开 GitHub 发布页查看。");
            bestVersion = candidate;
            best = new(tag, notes, new(ReleasesPage.AbsoluteUri + "/tag/" + Uri.EscapeDataString(tag)));
            CommunityUpdateWindow.ValidateRelease(best);
        }
    }
    private CommunityUpdateResult Result(CommunityRelease? best, GitHubReleaseVersion? bestVersion)
    {
        if (best is null) return new(CommunityUpdateState.NoRelease, Detail: "GitHub 发布仓库已接入，暂无适用于当前通道的已发布版本。");
        return bestVersion!.CompareTo(current) > 0
            ? new(CommunityUpdateState.Available, best, "更新来源：GitHub Releases。请在发布页查看安装包与更新说明。")
            : new(CommunityUpdateState.Latest, Detail: "当前版本不低于 GitHub 已发布的最新适用版本。");
    }
    private static bool Flag(JsonElement value, string key) => value.TryGetProperty(key, out var flag) && flag.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
}

// SemVer precedence includes numeric prerelease identifiers (preview.10 >
// preview.9). Build metadata does not affect precedence; invalid tags are skipped.
internal sealed class GitHubReleaseVersion : IComparable<GitHubReleaseVersion>
{
    private readonly string[] core, pre;
    internal bool Prerelease => pre.Length > 0;
    private GitHubReleaseVersion(string[] core, string[] pre) { this.core = core; this.pre = pre; }
    private static bool Numeric(string value) => value.Length > 0 && value.All(c => c is >= '0' and <= '9');
    private static bool Identifier(string value) => value.Length > 0 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '-');
    internal static GitHubReleaseVersion? Parse(string text)
    {
        if (text.Length is 0 or > 80) return null;
        if (text[0] is 'v' or 'V') text = text[1..];
        var build = text.Split('+');
        if (build.Length > 2 || build.Length == 2 && !build[1].Split('.').All(Identifier)) return null;
        int hyphen = build[0].IndexOf('-');
        var core = (hyphen < 0 ? build[0] : build[0][..hyphen]).Split('.');
        var pre = hyphen < 0 ? [] : build[0][(hyphen + 1)..].Split('.');
        if (core.Length != 3 || core.Any(p => !Numeric(p) || p.Length > 1 && p[0] == '0') ||
            pre.Any(p => !Identifier(p) || Numeric(p) && p.Length > 1 && p[0] == '0')) return null;
        return new(core, pre);
    }
    private static int Number(string a, string b) => a.Length == b.Length ? string.CompareOrdinal(a, b) : a.Length.CompareTo(b.Length);
    public int CompareTo(GitHubReleaseVersion? other)
    {
        if (other is null) return 1;
        for (int i = 0; i < core.Length; i++) { int order = Number(core[i], other.core[i]); if (order != 0) return order; }
        if (!Prerelease || !other.Prerelease) return Prerelease == other.Prerelease ? 0 : Prerelease ? -1 : 1;
        for (int i = 0; i < Math.Min(pre.Length, other.pre.Length); i++)
        {
            bool a = Numeric(pre[i]), b = Numeric(other.pre[i]);
            int order = a && b ? Number(pre[i], other.pre[i]) : a != b ? a ? -1 : 1 : string.CompareOrdinal(pre[i], other.pre[i]);
            if (order != 0) return order;
        }
        return pre.Length.CompareTo(other.pre.Length);
    }
}
