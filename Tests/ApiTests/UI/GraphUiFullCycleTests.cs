using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Ui;

[RelevantTestClass]
public sealed class GraphUiFullCycleTests
{
    [TestMethod]
    public async Task EdgeEndpointCollapse_FollowsLazyLoadedCycleAndCollapsesSelectedSide()
    {
        await using var server = await ApiServer.StartAsync();
        await SeedCycleGraph(server.BaseUri);

        await using var browser = await BrowserSession.LaunchAsync();
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
        });

        await page.GotoAsync($"{server.BaseUri}?path=one&renderer=svg");
        await ExpectVisibleNodes(page, "one");

        await ClickEdgeControl(page, "one", "two", "load-neighbor");
        await ExpectVisibleNodes(page, "one", "two");

        await ClickEdgeControl(page, "two", "five", "load-neighbor");
        await ExpectVisibleNodes(page, "five", "one", "two");

        await ClickEdgeControl(page, "five", "three", "load-neighbor");
        await ExpectVisibleNodes(page, "five", "one", "three", "two");

        await ClickEdgeControl(page, "two", "five", "collapse-edge");
        await ExpectVisibleNodes(page, "five", "one", "three", "two");
        await ExpectEdgeControl(page, "two", "five", "expand-edge");

        await ClickEdgeControl(page, "three", "four", "load-neighbor");
        await ExpectVisibleNodes(page, "five", "four", "one", "three", "two");

        await ClickEdgeControl(page, "three", "four", "collapse-edge");
        await ExpectVisibleNodes(page, "five", "one", "three", "two");

        await ClickEdgeControl(page, "two", "one", "collapse-edge");
        await ExpectVisibleNodes(page, "two");
    }

    [TestMethod]
    public async Task EdgeEndpointCollapse_KeepsVisibleNodeForEveryInitialRootComponent()
    {
        await using var server = await ApiServer.StartAsync();
        await SeedCrossRootGraph(server.BaseUri);

        await using var browser = await BrowserSession.LaunchAsync();
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
        });

        await page.GotoAsync($"{server.BaseUri}?renderer=svg");
        await ExpectVisibleNodes(page, "a", "b");

        await ClickEdgeControl(page, "a", "a/x", "load-neighbor");
        await ExpectVisibleNodes(page, "a", "b", "x");

        await ClickEdgeControl(page, "a", "a/x", "collapse-edge");
        await ExpectVisibleNodes(page, "a", "b");
    }

    static async Task SeedCycleGraph(Uri baseUri)
    {
        using var client = new HttpClient { BaseAddress = baseUri };
        foreach (var node in new[] { "one", "two", "three", "four", "five" })
            await CreateNode(client, node);

        await Connect(client, "one", "two");
        await Connect(client, "one", "three");
        await Connect(client, "two", "five");
        await Connect(client, "five", "three");
        await Connect(client, "three", "four");
    }

    static async Task SeedCrossRootGraph(Uri baseUri)
    {
        using var client = new HttpClient { BaseAddress = baseUri };
        await CreateNode(client, "a");
        await CreateNode(client, "b");
        await CreateNode(client, "x", "a");
        await Connect(client, "a", "a/x");
        await Connect(client, "a/x", "b");
    }

    static async Task CreateNode(HttpClient client, string localId, params string[] parentPath)
    {
        using var response = await client.PostAsJsonAsync("/api/graph/nodes", new
        {
            localId,
            parentPath = parentPath.Length == 0 ? null : parentPath
        });
        await AssertSuccess(response);
    }

    static async Task Connect(HttpClient client, string left, string right)
    {
        using var response = await client.PostAsJsonAsync("/api/graph/connections", new
        {
            node1InternalId = SplitId(left),
            node2InternalId = SplitId(right)
        });
        await AssertSuccess(response);
    }

    static string[] SplitId(string value) =>
        value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static async Task AssertSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync();
        Assert.Fail($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    static async Task ClickEdgeControl(IPage page, string anchorName, string otherName, string action)
    {
        var locator = page.Locator(EdgeControlSelector(anchorName, otherName, action)).First;
        await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });
        await locator.DispatchEventAsync("click");
    }

    static async Task ExpectEdgeControl(IPage page, string anchorName, string otherName, string action)
    {
        await page.Locator(EdgeControlSelector(anchorName, otherName, action)).First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });
    }

    static string EdgeControlSelector(string anchorName, string otherName, string action) =>
        $".graph-edge-control[data-anchor-name='{CssAttribute(anchorName)}'][data-other-name='{CssAttribute(otherName)}'][data-edge-action='{CssAttribute(action)}']";

    static string CssAttribute(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    static async Task ExpectVisibleNodes(IPage page, params string[] expected)
    {
        var ordered = expected.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        await page.WaitForFunctionAsync(
            """
            expected => JSON.stringify([...document.querySelectorAll(".graph-node-label")]
              .map(element => element.textContent.trim())
              .filter(Boolean)
              .sort()) === JSON.stringify(expected)
            """,
            ordered,
            new PageWaitForFunctionOptions { Timeout = 5000 });
    }

    sealed class BrowserSession : IAsyncDisposable
    {
        readonly IPlaywright playwright;

        BrowserSession(IPlaywright playwright, IBrowser browser)
        {
            this.playwright = playwright;
            Browser = browser;
        }

        IBrowser Browser { get; }

        public static async Task<BrowserSession> LaunchAsync()
        {
            var playwright = await Playwright.CreateAsync();
            foreach (var channel in new[] { "msedge", "chrome" })
            {
                try
                {
                    var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Channel = channel,
                        Headless = true
                    });
                    return new BrowserSession(playwright, browser);
                }
                catch (PlaywrightException)
                {
                }
            }

            try
            {
                var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
                return new BrowserSession(playwright, browser);
            }
            catch (PlaywrightException ex)
            {
                playwright.Dispose();
                Assert.Inconclusive("Playwright не смог запустить Edge, Chrome или bundled Chromium: " + ex.Message);
                throw;
            }
        }

        public Task<IPage> NewPageAsync(BrowserNewPageOptions options) =>
            Browser.NewPageAsync(options);

        public async ValueTask DisposeAsync()
        {
            await Browser.DisposeAsync();
            playwright.Dispose();
        }
    }

    sealed class ApiServer : IAsyncDisposable
    {
        readonly Process process;
        readonly string storagePath;
        readonly StringBuilder output;

        ApiServer(Process process, Uri baseUri, string storagePath, StringBuilder output)
        {
            this.process = process;
            BaseUri = baseUri;
            this.storagePath = storagePath;
            this.output = output;
        }

        public Uri BaseUri { get; }

        public static async Task<ApiServer> StartAsync()
        {
            var repoRoot = RepoRoot();
            var port = FreeTcpPort();
            var storagePath = Path.Combine(Path.GetTempPath(), "GraphDataUiTests", Guid.NewGuid().ToString("N"));
            var baseUri = new Uri($"http://127.0.0.1:{port}/");
            var output = new StringBuilder();
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{Path.Combine(repoRoot, "Api", "0_Api.csproj")}\" --no-build --no-launch-profile --urls {baseUri}",
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            startInfo.Environment["GraphStorage__RootPath"] = storagePath;
            startInfo.Environment["GraphStorage__MetadataFileName"] = "node.json";
            startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => AppendOutput(output, args.Data);
            process.ErrorDataReceived += (_, args) => AppendOutput(output, args.Data);
            if (!process.Start())
                throw new InvalidOperationException("Не удалось запустить API.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var server = new ApiServer(process, baseUri, storagePath, output);
            try
            {
                await server.WaitUntilReady();
                return server;
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        async Task WaitUntilReady()
        {
            using var client = new HttpClient { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (process.HasExited)
                    Assert.Fail($"API завершился до старта с кодом {process.ExitCode}:{Environment.NewLine}{output}");

                try
                {
                    using var response = await client.GetAsync("/api/ui/settings");
                    if (response.StatusCode == HttpStatusCode.OK)
                        return;
                }
                catch (HttpRequestException)
                {
                }
                catch (TaskCanceledException)
                {
                }

                await Task.Delay(250);
            }

            Assert.Fail($"API не стартовал за 45 секунд:{Environment.NewLine}{output}");
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process.Dispose();
            DeleteStorageDirectory();
        }

        void DeleteStorageDirectory()
        {
            if (!Directory.Exists(storagePath))
                return;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(storagePath, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(150);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(150);
                }
            }
        }
    }

    static void AppendOutput(StringBuilder output, string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
            output.AppendLine(line);
    }

    static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    static string RepoRoot([CallerFilePath] string sourcePath = "")
    {
        foreach (var candidate in new[] {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(sourcePath)
        })
        {
            var directory = new DirectoryInfo(candidate!);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GraphData.slnx")))
                directory = directory.Parent;

            if (directory is not null)
                return directory.FullName;
        }

        Assert.Fail("Could not locate GraphData.slnx from test output directory, current directory, or source path.");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }
}
