using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Ui;

[RelevantTestClass]
public sealed class TypedGraphUiFullCycleTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NativeGraph_ExistingEditorCreatesEditsFindsAndDeletesAfterServerRestart()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "GraphDataTypedUiTests", Guid.NewGuid().ToString("N"));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchBrowser(playwright);
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
        });
        var browserErrors = new List<string>();
        var dialogTasks = new List<Task>();
        page.PageError += (_, error) => browserErrors.Add(error);
        page.Dialog += (_, dialog) => dialogTasks.Add(dialog.AcceptAsync());
        try
        {
            await using (var server = await ApiServer.StartAsync(storagePath))
            {
                using var client = new HttpClient { BaseAddress = server.BaseUri };
                await SeedTypes(client);
                await page.GotoAsync($"{server.BaseUri}?renderer=svg");
                await page.Locator("#create-node-type option[value='NodeTypes/City']").WaitForAsync(
                    new LocatorWaitForOptions { State = WaitForSelectorState.Attached });

                await CreateNode(page, "origin", "NodeTypes/City");
                await CreateNode(page, "destination", "NodeTypes/City");
                await CreateNode(page, "scratch", "");
                Assert.AreEqual(0, (await ReadElement(client, "scratch")).GetProperty("typeIds").GetArrayLength());

                await OpenDetails(page);
                await page.Locator("#selection-list .attribute-key").First.FillAsync("note");
                await page.Locator("#selection-list .attribute-value").First.FillAsync("Черновик знания");
                await SetAttribute(page, "name", "scratch");
                await page.Locator("#selection-list").GetByRole(AriaRole.Button, new() { Name = "Сохранить", Exact = true }).ClickAsync();
                await ExpectStatus(page, "Сохранён узел «scratch»");
                Assert.AreEqual("Черновик знания", (await ReadElement(client, "scratch")).GetProperty("attributes").GetProperty("note").GetString());

                await page.SelectOptionAsync("#assign-node-type", "NodeTypes/City");
                await page.ClickAsync("#assign-node-type-button");
                await ExpectStatus(page, "Типы сохранены");
                CollectionAssert.Contains((await ReadElement(client, "scratch")).GetProperty("typeIds").EnumerateArray().Select(value => value.GetString()).ToArray(), "NodeTypes/City");

                await OpenNode(page, server.BaseUri, "origin");
                await page.SelectOptionAsync("#connect-edge-type", "NodeTypes/Flight");
                await page.FillAsync("#connect-members [data-member='Origin']", "origin");
                await page.FillAsync("#connect-members [data-member='Destination']", "origin");
                await page.Locator("#connect-members").GetByLabel("Атрибут: code", new() { Exact = true }).FillAsync("F-001");
                await page.FillAsync("#connect-edge-name", "flight");
                await page.ClickAsync("#connect-form button[type='submit']");
                await ExpectStatus(page, "Создана связь «flight»");
                await page.Locator(".graph-node-label[title='flight']").WaitForAsync();
                await page.WaitForFunctionAsync("document.querySelectorAll('.graph-svg-edge').length === 2");
                var renderedRelation = await page.Locator(".graph-node-label[title='flight']").InnerTextAsync();
                StringAssert.Contains(renderedRelation, "Origin");
                StringAssert.Contains(renderedRelation, "Destination");

                await OpenDetails(page);
                await page.Locator("#selection-list [data-member='Destination']").FillAsync("destination");
                await page.Locator("#selection-list").GetByRole(AriaRole.Button, new() { Name = "Сохранить отношение", Exact = true }).ClickAsync();
                await ExpectStatus(page, "Отношение сохранено");
                var flight = await ReadElement(client, "flight");
                Assert.AreEqual("flight", flight.GetProperty("id").GetString());
                Assert.AreEqual("origin", flight.GetProperty("members").GetProperty("Origin")[0].GetString());
                Assert.AreEqual("destination", flight.GetProperty("members").GetProperty("Destination")[0].GetString());
                Assert.AreEqual("F-001", flight.GetProperty("attributes").GetProperty("code").GetString());
                StringAssert.Contains(await page.Locator(".graph-node-label[title='flight']").InnerTextAsync(), "destination");

                // Change schema, roles and attributes together without replacing the relation identity.
                await page.Locator("#selection-list select").SelectOptionAsync("NodeTypes/Journey");
                await page.Locator("#selection-list [data-member='Start']").FillAsync("origin");
                await page.Locator("#selection-list [data-member='Finish']").FillAsync("destination");
                await SetAttribute(page, "booking", "BK-001");
                var replacement = await page.RunAndWaitForResponseAsync(
                    () => page.Locator("#selection-list").GetByRole(AriaRole.Button, new() { Name = "Сохранить отношение", Exact = true }).ClickAsync(),
                    response => response.Request.Method == "PUT" && response.Url.Contains("/api/typed-graph/relations?id=flight", StringComparison.Ordinal));
                Assert.IsTrue(replacement.Ok, await replacement.TextAsync());
                await page.WaitForFunctionAsync("document.querySelector('.graph-node-label[title=\"flight\"]').textContent.includes('Journey')");
                var journey = await ReadElement(client, "flight");
                Assert.AreEqual("flight", journey.GetProperty("id").GetString());
                CollectionAssert.AreEqual(new[] { "NodeTypes/Journey" }, journey.GetProperty("typeIds").EnumerateArray().Select(value => value.GetString()).ToArray());
                Assert.AreEqual("origin", journey.GetProperty("members").GetProperty("Start")[0].GetString());
                Assert.AreEqual("destination", journey.GetProperty("members").GetProperty("Finish")[0].GetString());
                Assert.IsFalse(journey.GetProperty("members").TryGetProperty("Origin", out _));
                Assert.IsFalse(journey.GetProperty("members").TryGetProperty("Destination", out _));
                Assert.AreEqual("BK-001", journey.GetProperty("attributes").GetProperty("booking").GetString());
                var visibleRoleLabels = await page.Locator(".graph-edge-label").AllTextContentsAsync();
                Assert.IsTrue(visibleRoleLabels.Any(label => label.Contains("Start", StringComparison.Ordinal)));
                Assert.IsTrue(visibleRoleLabels.Any(label => label.Contains("Finish", StringComparison.Ordinal)));
                Assert.IsFalse(visibleRoleLabels.Any(label => label.Contains("Origin", StringComparison.Ordinal)
                    || label.Contains("Destination", StringComparison.Ordinal)));

                foreach (var member in new[] { "Start", "Finish" })
                {
                    var memberHeight = await page.Locator($"#selection-list [data-member='{member}']")
                        .EvaluateAsync<double>("element => element.getBoundingClientRect().height");
                    Assert.IsTrue(memberHeight is > 0 and < 100, $"Single-participant field '{member}' should stay compact; actual height: {memberHeight}px.");
                }
                await page.ClickAsync("#fit-button");
                await page.WaitForFunctionAsync("""
                    () => {
                      const graph = document.querySelector('#graph').getBoundingClientRect();
                      const nodes = [...document.querySelectorAll('.graph-svg-node')];
                      return nodes.length === 4 && nodes.every(node => {
                        const rect = node.getBoundingClientRect();
                        return rect.left >= graph.left && rect.right <= graph.right
                          && rect.top >= graph.top && rect.bottom <= graph.bottom;
                      });
                    }
                    """);

                var screenshot = Path.Combine(RepoRoot(), "TestResults", "typed-graph-ui-cycle", "typed-graph-editor-cycle.png");
                Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshot, FullPage = true });
                TestContext.AddResultFile(screenshot);

                await page.ClickAsync("[data-tab='search']");
                await page.FillAsync("#search-query-json", """
                    {"return":["n"],"where":{"kind":"text","node":{"kind":"var","name":"n"},"value":"flight"},"limit":20}
                    """);
                await page.ClickAsync("#search-submit-button");
                await ExpectStatus(page, "Найдено решений: 1");
                StringAssert.Contains(await page.Locator("#search-results").InnerTextAsync(), "flight");
            }

            // Restart the actual API process against the same native records.
            await using (var reopened = await ApiServer.StartAsync(storagePath))
            {
                using var client = new HttpClient { BaseAddress = reopened.BaseUri };
                await OpenNode(page, reopened.BaseUri, "flight");
                await OpenDetails(page);
                Assert.AreEqual("destination", await page.Locator("#selection-list [data-member='Finish']").InputValueAsync());
                Assert.AreEqual("NodeTypes/Journey", await page.Locator("#selection-list select").InputValueAsync());
                Assert.AreEqual("BK-001", (await ReadElement(client, "flight")).GetProperty("attributes").GetProperty("booking").GetString());
                Assert.AreEqual("Черновик знания", (await ReadElement(client, "scratch")).GetProperty("attributes").GetProperty("note").GetString());

                await page.ClickAsync("#delete-selected-nodes-button");
                await ExpectStatus(page, "Выбранные элементы удалены");
                using var deletedRelation = await client.GetAsync("/api/typed-graph/elements?id=flight");
                Assert.AreEqual(HttpStatusCode.NotFound, deletedRelation.StatusCode);

                await OpenNode(page, reopened.BaseUri, "scratch");
                await page.ClickAsync("#delete-selected-nodes-button");
                await ExpectStatus(page, "Выбранные элементы удалены");
                using var deletedInstance = await client.GetAsync("/api/typed-graph/elements?id=scratch");
                Assert.AreEqual(HttpStatusCode.NotFound, deletedInstance.StatusCode);
                await Task.WhenAll(dialogTasks);
                Assert.AreEqual(0, browserErrors.Count, string.Join(Environment.NewLine, browserErrors));
                Assert.IsFalse(await page.Locator("#server-error-overlay").IsVisibleAsync());
            }
        }
        finally
        {
            DeleteTestStorage(storagePath);
        }
    }

    static async Task CreateNode(IPage page, string id, string typeId)
    {
        if (await page.Locator("#clear-selection-button").IsVisibleAsync())
            await page.ClickAsync("#clear-selection-button");
        await page.FillAsync("#create-node-name", id);
        await page.SelectOptionAsync("#create-node-type", typeId);
        if (typeId == "NodeTypes/City")
            await page.Locator("#create-node-fields").GetByLabel("Атрибут: name", new() { Exact = true }).FillAsync(id);
        await page.ClickAsync("#create-node-form button[type='submit']");
        await ExpectStatus(page, $"Создан узел «{id}»");
        await page.Locator($".graph-node-label[title='{id}']").WaitForAsync();
    }

    static async Task OpenNode(IPage page, Uri baseUri, string id)
    {
        await page.GotoAsync($"{baseUri}?path={Uri.EscapeDataString(id)}&renderer=svg");
        await page.Locator($".graph-node-label[title='{id}']").WaitForAsync();
        await page.WaitForFunctionAsync("!document.querySelector('#delete-selected-nodes-button').disabled");
    }

    static async Task OpenDetails(IPage page)
    {
        if (await page.Locator("#selection-list .selection-details").CountAsync() == 0)
            await page.Locator("#selection-list .selection-row-header").First.ClickAsync();
        await page.Locator("#selection-list .selection-details").WaitForAsync();
    }

    static async Task SetAttribute(IPage page, string key, string value)
    {
        var rows = page.Locator("#selection-list .attribute-row");
        for (var index = 0; index < await rows.CountAsync(); index++)
        {
            var row = rows.Nth(index);
            if (await row.Locator(".attribute-key").InputValueAsync() != key) continue;
            await row.Locator(".attribute-value").FillAsync(value);
            return;
        }
        await page.Locator("#selection-list").GetByRole(AriaRole.Button, new() { Name = "Добавить", Exact = true }).ClickAsync();
        await rows.Last.Locator(".attribute-key").FillAsync(key);
        await rows.Last.Locator(".attribute-value").FillAsync(value);
    }

    static Task ExpectStatus(IPage page, string text) => page.WaitForFunctionAsync(
        "expected => document.querySelector('#status').textContent === expected", text,
        new PageWaitForFunctionOptions { Timeout = 10000 });

    static async Task<JsonElement> ReadElement(HttpClient client, string id)
    {
        using var response = await client.GetAsync("/api/typed-graph/elements?id=" + Uri.EscapeDataString(id));
        await AssertSuccess(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    static async Task SeedTypes(HttpClient client)
    {
        using var city = await client.PostAsJsonAsync("/api/typed-graph/types", new
        {
            id = "NodeTypes/City", kind = "Instance", members = Array.Empty<object>(),
            attributes = new[] { new { name = "name", kind = "String", required = true } }
        });
        await AssertSuccess(city);
        using var flight = await client.PostAsJsonAsync("/api/typed-graph/types", new
        {
            id = "NodeTypes/Flight", kind = "Relation",
            attributes = new[] { new { name = "code", kind = "String", required = true } },
            members = new[] {
                new { name = "Origin", typeId = "NodeTypes/City", min = 1, max = 1 },
                new { name = "Destination", typeId = "NodeTypes/City", min = 1, max = 1 }
            }
        });
        await AssertSuccess(flight);
        using var journey = await client.PostAsJsonAsync("/api/typed-graph/types", new
        {
            id = "NodeTypes/Journey", kind = "Relation",
            attributes = new[] { new { name = "booking", kind = "String", required = true } },
            members = new[] {
                new { name = "Start", typeId = "NodeTypes/City", min = 1, max = 1 },
                new { name = "Finish", typeId = "NodeTypes/City", min = 1, max = 1 }
            }
        });
        await AssertSuccess(journey);
    }

    static async Task AssertSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            Assert.Fail($"{(int)response.StatusCode} {response.ReasonPhrase}: {await response.Content.ReadAsStringAsync()}");
    }

    static async Task<IBrowser> LaunchBrowser(IPlaywright playwright)
    {
        foreach (var channel in new[] { "msedge", "chrome" })
        {
            try { return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Channel = channel, Headless = true }); }
            catch (PlaywrightException) { }
        }
        try { return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); }
        catch (PlaywrightException error)
        {
            Assert.Inconclusive("Playwright не смог запустить браузер: " + error.Message);
            throw;
        }
    }

    sealed class ApiServer : IAsyncDisposable
    {
        readonly Process process;
        readonly StringBuilder output;

        ApiServer(Process process, Uri baseUri, StringBuilder output)
        {
            this.process = process;
            this.output = output;
            BaseUri = baseUri;
        }

        public Uri BaseUri { get; }

        public static async Task<ApiServer> StartAsync(string storagePath)
        {
            var repoRoot = RepoRoot();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var baseUri = new Uri($"http://127.0.0.1:{port}/");
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{Path.Combine(repoRoot, "Api", "0_Api.csproj")}\" --no-build --no-launch-profile --urls {baseUri}",
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            startInfo.Environment["GraphStorage__Mode"] = "semantic";
            startInfo.Environment["GraphStorage__RootPath"] = "";
            startInfo.Environment["SemanticGraphStorage__RootPath"] = storagePath;
            startInfo.Environment["Logging__EventLog__LogLevel__Default"] = "None";
            startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            var output = new StringBuilder();
            var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, args) => { lock (output) output.AppendLine(args.Data); };
            process.ErrorDataReceived += (_, args) => { lock (output) output.AppendLine(args.Data); };
            if (!process.Start()) throw new InvalidOperationException("Не удалось запустить API.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var server = new ApiServer(process, baseUri, output);
            try { await server.WaitUntilReady(); return server; }
            catch { await server.DisposeAsync(); throw; }
        }

        async Task WaitUntilReady()
        {
            using var client = new HttpClient { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (process.HasExited) Assert.Fail($"API завершился с кодом {process.ExitCode}: {output}");
                try
                {
                    using var response = await client.GetAsync("/api/typed-graph/types");
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(250);
            }
            Assert.Fail($"API не стартовал за 45 секунд: {output}");
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process.Dispose();
        }
    }

    static void DeleteTestStorage(string storagePath)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GraphDataTypedUiTests")) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(storagePath);
        if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to remove storage outside the test directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }

    static string RepoRoot([CallerFilePath] string sourcePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (!File.Exists(Path.Combine(directory.FullName, "GraphData.slnx")))
            directory = directory.Parent ?? throw new InvalidOperationException("GraphData.slnx not found.");
        return directory.FullName;
    }
}
