using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Page;
using RainBot.Services.Preprocessing;
using RainBot.Services.QQ;
using Xunit;

namespace RainBot.Tests;

public class GitHubReaderTests
{
    private const string Repo = "https://github.com/octocat/hello-world";
    private const string RepoJson = """{"full_name":"octocat/Hello-World","private":false,"description":"测试公开仓库","language":"C#","stargazers_count":12,"forks_count":3,"open_issues_count":2,"default_branch":"main","license":{"spdx_id":"MIT"},"archived":false}""";
    private const string IssueJson = """{"number":7,"title":"修复消息解析","state":"open","user":{"login":"alice"},"body":"真实主帖正文","comments":2}""";

    [Theory]
    [InlineData("https://github.com/Octocat/Hello-World.git?tab=readme-ov-file#readme", "repository", "/repos/octocat/hello-world")]
    [InlineData("http://www.github.com/octocat/hello-world/issues/007#issuecomment-123", "issue", "/repos/octocat/hello-world/issues/7")]
    [InlineData("https://github.com/octocat/hello-world/pull/7", "pull", "/repos/octocat/hello-world/pulls/7")]
    [InlineData("https://github.com/octocat/hello-world/commit/abcdef123", "commit", "/repos/octocat/hello-world/commits/abcdef123")]
    [InlineData("https://github.com/octocat/hello-world/releases", "release", "/repos/octocat/hello-world/releases/latest")]
    [InlineData("https://github.com/octocat/hello-world/releases/tag/v1.0", "release", "/repos/octocat/hello-world/releases/tags/v1.0")]
    [InlineData("https://github.com/octocat/hello-world/blob/main/src/app.cs", "file", "/repos/octocat/hello-world/contents/src/app.cs")]
    public void 规范化链接并映射固定API(string url, string kind, string apiPath)
    {
        var link = GitHubLink.Parse(url);
        Assert.NotNull(link);
        Assert.Equal(kind, link.Kind);
        Assert.Equal("api.github.com", link.ApiUrl.Host);
        Assert.Equal(apiPath, link.ApiUrl.AbsolutePath);
        Assert.Equal("", link.Url.Fragment);
        Assert.Equal("", link.Url.Query);
        Assert.Equal("https", link.Url.Scheme);
    }

    [Theory]
    [InlineData("https://github.com.evil.example/octocat/hello-world")]
    [InlineData("https://evilgithub.com/octocat/hello-world")]
    [InlineData("https://api.github.com/repos/octocat/hello-world")]
    [InlineData("https://github.com:8080/octocat/hello-world")]
    [InlineData("https://user:password@github.com/octocat/hello-world")]
    [InlineData("https://github.com/octocat")]
    [InlineData("https://github.com/octocat/hello-world/issues/new")]
    [InlineData("https://github.com/octocat/hello-world/issues/0")]
    [InlineData("https://github.com/octocat/hello-world/discussions/1")]
    [InlineData("https://github.com/octocat/hello-world/actions/runs/1")]
    [InlineData("https://github.com/octocat/hello-world/blob/main")]
    [InlineData("https://github.com/octocat/hello-world/releases/download/v1/archive.zip")]
    public void 无效及不支持的链接不触发读取(string url) => Assert.Null(GitHubLink.Parse(url));

    [Fact]
    public void 文本提取排除卡片预览去重并去除分享参数()
    {
        Assert.Equal(new[] { Repo, Repo + "/pull/7" }, GitHubLinkPreprocessor.Extract($"看看 [{Repo}?tab=readme#readme]({Repo})，{Repo}/pull/7。"));
        Assert.Empty(GitHubLinkPreprocessor.Extract("[卡片消息] preview: " + Repo));
        var file = GitHubLink.Parse(Repo + "/blob/feature%2Ffix/src/a%20b.cs");
        Assert.NotNull(file);
        Assert.Equal("feature/fix", file.Reference);
        Assert.Contains("ref=feature%2Ffix", file.ApiUrl.Query);
        Assert.Contains("a%20b.cs", file.ApiUrl.AbsolutePath);
    }

    [Fact]
    public async Task 仓库读取元信息和README并缓存规范化链接()
    {
        using var provider = TestHost.Build().Provider;
        int calls = 0;
        using var pages = Pages(provider, request =>
        {
            calls++;
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.Accept).MediaType);
            Assert.Equal("2026-03-10", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
            Assert.Null(request.Headers.Authorization);
            return Json(request.RequestUri.AbsolutePath.EndsWith("/readme") ? FileJson("# 唯一README正文") : RepoJson);
        });
        var result = await pages.ReadAsync(Repo);
        Assert.Equal("ok", result.Status);
        Assert.Equal("github", result.Reader);
        Assert.Contains("测试公开仓库", result.Summary);
        Assert.Contains("MIT", result.Summary);
        Assert.Contains("唯一README正文", result.Summary);
        await pages.ReadAsync(Repo + "#readme");
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task README缺失保留已获取仓库信息()
    {
        using var provider = TestHost.Build().Provider;
        using var pages = Pages(provider, request => request.RequestUri!.AbsolutePath.EndsWith("/readme")
            ? new(HttpStatusCode.NotFound) : Json(RepoJson));
        var result = await pages.ReadAsync(Repo);
        Assert.Equal("ok", result.Status);
        Assert.Contains("测试公开仓库", result.Summary);
        Assert.Contains("README 未取得", result.Summary);
    }

    [Fact]
    public void PR读取正文和状态且不会声明读过代码差异()
    {
        var result = new GitHubReader().Read(new Uri(Repo + "/pull/7"),
            """{"title":"修复问题","number":7,"state":"closed","merged":true,"draft":false,"user":{"login":"alice"},"body":"PR 真实说明","head":{"ref":"fix"},"base":{"ref":"main"},"changed_files":2,"additions":5,"deletions":3}""");
        Assert.Equal("ok", result.Status);
        Assert.Contains("PR 真实说明", result.Summary);
        Assert.Contains("已合并：True", result.Summary);
        Assert.Contains("fix → main", result.Summary);
        Assert.Contains("不含评论、评审或完整代码差异", result.Summary);
    }

    [Fact]
    public void Commit和Release读取说明()
    {
        var reader = new GitHubReader();
        var commit = reader.Read(new Uri(Repo + "/commit/abcdef123"),
            """{"sha":"abcdef123","commit":{"message":"修复取消逻辑"},"author":{"login":"bob"},"files":[{"filename":"app.cs","patch":"不注入完整diff"}]}""");
        Assert.Equal("ok", commit.Status);
        Assert.Contains("修复取消逻辑", commit.Summary);
        Assert.Contains("app.cs", commit.Summary);
        Assert.DoesNotContain("不注入完整diff", commit.Summary);
        var release = reader.Read(new Uri(Repo + "/releases/tag/v1"),
            """{"tag_name":"v1","name":"首个版本","body":"发布真实说明","prerelease":false}""");
        Assert.Equal("ok", release.Status);
        Assert.Contains("发布真实说明", release.Summary);
    }

    [Fact]
    public async Task 文件只读取UTF8文本且不请求下载地址()
    {
        using var provider = TestHost.Build().Provider;
        using var pages = Pages(provider, request =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("?ref=main", request.RequestUri.Query);
            return Json(FileJson("public class Test {}"));
        });
        var result = await pages.ReadAsync(Repo + "/blob/main/README.md");
        Assert.Equal("ok", result.Status);
        Assert.Contains("public class Test {}", result.Summary);
        Assert.Contains("未执行代码", result.Summary);
        Assert.Equal("unavailable", new GitHubReader().Read(new Uri(Repo + "/blob/main/a.bin"), FileJson("\0binary")).Status);
        Assert.Equal("unavailable", new GitHubReader().Read(new Uri(Repo + "/blob/main/a.bin"), """{"path":"a.bin","type":"file","encoding":"none","content":"","size":2000000}""").Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"full_name\":\"secret/repo\",\"private\":true}")]
    public void 无资源信息不能编造仓库资料(string json) =>
        Assert.Equal("unavailable", new GitHubReader().Read(new Uri(Repo), json).Status);

    [Fact]
    public async Task 限流失败与正常链接隔离并缓存失败()
    {
        using var provider = TestHost.Build().Provider;
        int calls = 0;
        using var pages = Pages(provider, request =>
        {
            calls++;
            return request.RequestUri!.AbsolutePath.EndsWith("/1") ? new(HttpStatusCode.Forbidden) : Json(IssueJson);
        });
        var processor = new GitHubLinkPreprocessor(pages, provider.GetRequiredService<RuntimeConfig>());
        var message = TestHelpers.Msg("group", "alice", $"{Repo}/issues/1 {Repo}/issues/7");
        var result = await processor.ProcessAsync(Context(message), default);
        Assert.Equal("unavailable", result[0].Status);
        Assert.Contains("限流", result[0].Error);
        Assert.Equal("ok", result[1].Status);
        Assert.Contains("真实主帖正文", result[1].Text);
        await processor.ProcessAsync(Context(message), default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task API跳转到非GitHub地址时拒绝且不发起后续请求()
    {
        using var provider = TestHost.Build().Provider;
        int calls = 0;
        using var pages = Pages(provider, _ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://example.com/fake-api");
            return response;
        });
        var result = await pages.ReadAsync(Repo + "/issues/7");
        Assert.Equal("unavailable", result.Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 开关优先级数量和字符预算即时生效()
    {
        using var provider = TestHost.Build().Provider;
        var config = provider.GetRequiredService<RuntimeConfig>();
        var requested = new List<string>();
        using var pages = Pages(provider, request =>
        {
            requested.Add(request.RequestUri!.AbsolutePath);
            return Json(IssueJson.Replace("真实主帖正文", new string('文', 6000)));
        });
        var processor = new GitHubLinkPreprocessor(pages, config);
        var current = TestHelpers.Msg("group", "alice", Repo + "/issues/7");
        var other = TestHelpers.Msg("group", "bob", Repo + "/issues/9");
        var context = new MessagePreprocessContext(current, [other, current], Repo + "/issues/8", [], [], DateTimeOffset.UtcNow);
        config.Config.GitHub.MaxLinksPerTurn = 2;
        config.Config.GitHub.MaxChars = 200;
        var results = await processor.ProcessAsync(context, default);
        Assert.Equal(2, results.Count);
        Assert.EndsWith("/7", requested[0]);
        Assert.EndsWith("/8", requested[1]);
        Assert.All(results, result => Assert.True(result.Text.Length <= 200));
        Assert.All(results, result => Assert.Contains("内容已截断", result.Text));
        config.Config.GitHub.Enabled = false;
        Assert.Empty(await processor.ProcessAsync(context, default));
        Assert.Equal(2, requested.Count);
    }

    [Fact]
    public async Task 超时取消网络读取且外部取消传播()
    {
        using var provider = TestHost.Build().Provider;
        var config = provider.GetRequiredService<RuntimeConfig>();
        config.Config.GitHub.BudgetSeconds = 1;
        using var handler = new SlowHandler();
        using var pages = new PageService(new PageReaderHttp(new SlowFactory(handler)),
            new SiteReaderRegistry([new GitHubReader()], new OpenGraphReader()), config, NullLogger<PageService>.Instance);
        var processor = new GitHubLinkPreprocessor(pages, config);
        var context = Context(TestHelpers.Msg("group", "alice", Repo + "/issues/7"));
        Assert.Equal("timeout", Assert.Single(await processor.ProcessAsync(context, default)).Status);
        Assert.True(handler.Cancelled);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessAsync(context, cancellation.Token));
    }

    [Fact]
    public async Task 正常处理流程自动注入GitHub文本仅到本轮F块()
    {
        string modelBody = "";
        await using var provider = await TestHost.BuildReadyAsync(request =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Json(IssueJson);
            if (request.RequestUri.AbsolutePath.Contains("chat/completions"))
                modelBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(TestHelpers.LlmTextResponse("收到"));
        });
        var config = provider.GetRequiredService<RuntimeConfig>();
        foreach (var property in typeof(FunConfig).GetProperties().Where(property => property.Name.StartsWith("Enable")))
            property.SetValue(config.Config.Fun, false);
        await provider.GetRequiredService<MessageProcessor>().ProcessAsync(TestHelpers.Msg("group", "alice", Repo + "/issues/7", isAt: true), default);
        using var body = JsonDocument.Parse(modelBody);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Contains("真实主帖正文", messages[3].GetProperty("content").GetString());
        Assert.DoesNotContain("真实主帖正文", messages[2].GetProperty("content").GetString());
        Assert.DoesNotContain(provider.GetRequiredService<HistoryStore>().GetRecent("group", 10000, out _),
            entry => entry.Content.Contains("真实主帖正文"));
        Assert.Contains(await config.ListAllAsync(), entry => entry.Key == "GitHub.Enabled");
        Assert.Null(await config.SetAsync("GitHub.Enabled", "false"));
        Assert.False(config.Config.GitHub.Enabled);
        Assert.Null(await config.ResetAsync("GitHub.Enabled"));
        Assert.True(config.Config.GitHub.Enabled);
    }

    private static MessagePreprocessContext Context(IncomingMessage message) => new(message, [message], "", [], [], DateTimeOffset.UtcNow);
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static string FileJson(string text) => JsonSerializer.Serialize(new
    { type = "file", path = "README.md", encoding = "base64", size = Encoding.UTF8.GetByteCount(text), content = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) });
    private static PageService Pages(ServiceProvider provider, Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new PageReaderHttp(new FakeHttpClientFactory(responder)), new SiteReaderRegistry([new GitHubReader()], new OpenGraphReader()),
            provider.GetRequiredService<RuntimeConfig>(), NullLogger<PageService>.Instance);
    private sealed class SlowFactory(SlowHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }
    private sealed class SlowHandler : HttpMessageHandler
    {
        public bool Cancelled { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return Json(IssueJson);
        }
    }
}
