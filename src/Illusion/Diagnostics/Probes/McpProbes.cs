using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Illusion.Assets;
using Illusion.Formats.Hashing;
using Illusion.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// End-to-end check of the embedded MCP server: it starts, a real MCP client discovers and calls the
/// tool over streamable HTTP, a second server on the same port reports the clash instead of throwing,
/// and stopping actually closes the door.
/// </summary>
internal static class McpProbes
{
    /// <summary>
    /// The tool surface this build promises, listed out so the probe fails on a tool that silently
    /// stopped being registered. Kept in the order the tool classes declare them.
    /// </summary>
    private static readonly string[] ExpectedTools =
    {
        "ping",
        // SdsTools
        "list_sds_files", "open_sds_file", "get_sds_header", "list_resources", "get_resource_info",
        "search_resources", "extract_resource", "close_sds_file", "get_sds_stats",
        // UtilityTools
        "hash_fnv32", "hash_fnv64", "hash_batch", "convert_number", "list_game_files",
        "get_configured_games",
        // TableTools
        "list_tables", "dump_rows", "lookup_by_row",
        // StreamMapTools
        "parse_stream_map",
    };

    /// <summary>Records one assertion. A delegate rather than an <c>Action</c> so the optional
    /// <paramref name="detail"/> survives being passed between the probe's steps.</summary>
    private delegate void CheckFn(string name, bool ok, string detail = "");

    /// <summary>Records a step that could not run at all — no game install, nothing to read. A SKIP
    /// is neither a pass nor a failure: the machine simply could not answer the question.</summary>
    private delegate void NoteFn(string message);

    internal static void RunMcpProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_mcp.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        void Skip(string message) => sb.AppendLine("[SKIP] " + message);

        try
        {
            // Probes run inside App.OnStartup — on the UI thread, and before the dispatcher loop has
            // started. Anything that awaited back onto that context would wait forever, since nothing
            // will ever pump it. Driving the scenario from the thread pool sidesteps that entirely.
            Task.Run(() => RunScenarioAsync(Check, Skip)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            Finish(sb, outFile, "MCP", pass, fail);
        }
    }

    private static async Task RunScenarioAsync(CheckFn check, NoteFn skip)
    {
        // Port 0: the OS picks a free one, so the probe can run while the application itself is open
        // on the default port — and two probes can run at once.
        //
        // The seams are registered exactly as App does it, minus the UI marshal (there is no
        // dispatcher in a headless run). Without them a tool taking IGameEnvironment cannot be
        // constructed and the SDK answers with prose instead of the tool's JSON — so registering
        // here is not scaffolding, it is what makes the DI half of the wiring get tested at all.
        var host = new McpServerHost(new McpHostOptions
        {
            Port = 0,
            ConfigureServices = services => services.AddSingleton<IGameEnvironment, AppGameEnvironment>(),
        });
        await using (host.ConfigureAwait(false))
        {
            await host.StartAsync().ConfigureAwait(false);

            McpServerState state = host.State;
            check("server reaches Running", state.Status == McpServerStatus.Running, state.Error ?? "");
            check("address is a loopback MCP endpoint",
                state.Address is not null
                && state.Address.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)
                && state.Address.EndsWith(McpHostOptions.Path, StringComparison.Ordinal),
                state.Address ?? "<null>");

            if (state.Address is null)
            {
                return;
            }

            await ExerciseClientAsync(state.Address, check, skip).ConfigureAwait(false);
            await CheckForeignHostRejectedAsync(state.Address, check).ConfigureAwait(false);
            await CheckPortClashAsync(new Uri(state.Address).Port, check).ConfigureAwait(false);
            await CheckStopWinsAsync(check).ConfigureAwait(false);

            // Both halves of this pair matter. Asserting only that a stopped server goes quiet proves
            // nothing on its own — an unanswerable request looks identical to a closed port — so the
            // same request has to be shown working first.
            check("the endpoint answers while running",
                await RespondsAsync(state.Address).ConfigureAwait(false));

            await host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            check("server reports Stopped", host.State.Status == McpServerStatus.Stopped,
                host.State.Status.ToString());
            check("the endpoint stops answering once stopped",
                !await RespondsAsync(state.Address).ConfigureAwait(false));
        }
    }

    /// <summary>Talks to the server exactly as a real client does: discover the tools, then call one.</summary>
    private static async Task ExerciseClientAsync(string address, CheckFn check, NoteFn skip)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(address),
            // Pinned rather than auto-detected: the point is to prove the modern transport works,
            // not to let the client quietly fall back to legacy SSE.
            TransportMode = HttpTransportMode.StreamableHttp,
        });

        await using McpClient client = await McpClient.CreateAsync(transport).ConfigureAwait(false);

        IList<McpClientTool> tools = await client.ListToolsAsync().ConfigureAwait(false);
        McpClientTool? ping = tools.FirstOrDefault(t => t.Name == "ping");
        check("client discovers the ping tool", ping is not null,
            string.Join(", ", tools.Select(t => t.Name)));

        // Every tool, not just ping. A description is what the model reads to decide whether to call
        // the thing at all, so one missing is a tool the model will never reach for — and the SDK is
        // perfectly happy to serve it, which is why this is asserted rather than assumed.
        string[] undescribed = tools
            .Where(t => string.IsNullOrWhiteSpace(t.Description))
            .Select(t => t.Name)
            .ToArray();
        check("every tool carries a description for the model to read",
            undescribed.Length == 0, string.Join(", ", undescribed));

        // Names are the tools' public contract: a client's saved prompts and a user's muscle memory
        // both address them by name, so a rename is a breaking change that should show up here.
        // Duplicates are the other failure this catches — two [McpServerTool] methods claiming one
        // name resolve silently to whichever the SDK enumerated last.
        string[] duplicates = tools
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        check("no two tools claim the same name", duplicates.Length == 0, string.Join(", ", duplicates));

        string[] missing = ExpectedTools.Where(name => tools.All(t => t.Name != name)).ToArray();
        check($"all {ExpectedTools.Length} expected tools are served", missing.Length == 0,
            missing.Length == 0 ? $"{tools.Count} served" : "missing: " + string.Join(", ", missing));

        CallToolResult result = await client.CallToolAsync("ping").ConfigureAwait(false);
        string text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        check("calling ping succeeds", result.IsError != true);
        check("ping answers with its version banner", text.Contains("pong", StringComparison.Ordinal), text);

        await ExerciseToolsAsync(client, check, skip).ConfigureAwait(false);
    }

    /// <summary>
    /// Calls the browsing tools for real. Discovery proves only that a method was registered — not
    /// that it opens an archive, pages an array, or explains a bad argument instead of throwing.
    /// The file-reading half SKIPs when no game is configured: the machine has nothing to read.
    /// </summary>
    private static async Task ExerciseToolsAsync(McpClient client, CheckFn check, NoteFn skip)
    {
        // The pure tools first — they need nothing off disk, so they run on every machine.
        JsonElement hash = await CallAsync(client, "hash_fnv64",
            new Dictionary<string, object?> { ["input"] = "sds/city/eastside.sds" }).ConfigureAwait(false);
        check("hash_fnv64 reproduces the format layer's own hash",
            hash.GetProperty("success").GetBoolean()
            && hash.GetProperty("fnv64").GetUInt64() == Fnv64.Hash("sds/city/eastside.sds"),
            hash.GetProperty("hex").GetString() ?? "");

        JsonElement number = await CallAsync(client, "convert_number",
            new Dictionary<string, object?> { ["input"] = "0xDEADBEEF" }).ConfigureAwait(false);
        check("convert_number reads hex and reports both signednesses",
            number.GetProperty("unsigned32").GetUInt32() == 0xDEADBEEF
            && number.GetProperty("signed32").GetInt32() == unchecked((int)0xDEADBEEF));

        JsonElement batch = await CallAsync(client, "hash_batch",
            new Dictionary<string, object?> { ["inputs"] = "alpha\nbeta, gamma" }).ConfigureAwait(false);
        check("hash_batch splits on newlines and commas alike",
            batch.GetProperty("count").GetInt32() == 3,
            batch.GetProperty("count").GetInt32().ToString(CultureInfo.InvariantCulture));

        // Handed nonsense, a tool must ANSWER rather than throw. An exception would reach the client
        // as the SDK's generic "an error occurred" and the caller would learn nothing about what it
        // got wrong — which is the whole reason ToolResult.Invalid exists.
        JsonElement noSource = await CallAsync(client, "list_tables",
            new Dictionary<string, object?>()).ConfigureAwait(false);
        check("a tool given no source argument explains itself instead of throwing",
            !noSource.GetProperty("success").GetBoolean()
            && (noSource.GetProperty("error").GetString() ?? "").Contains("exactly one", StringComparison.Ordinal),
            noSource.GetProperty("error").GetString() ?? "");

        JsonElement games = await CallAsync(client, "get_configured_games", null).ConfigureAwait(false);
        check("get_configured_games reaches the application through its DI seam",
            games.GetProperty("success").GetBoolean(),
            games.GetProperty("configuredPath").GetString() ?? "<not set>");

        if (!ProbeAssert.InitEnv(out string? envError))
        {
            skip("no game install configured — the file-reading tools were not exercised (" + envError + ")");
            return;
        }

        string sdsRoot = Path.Combine(MafiaEnvironment.PcFolder, "sds");
        string tablesSds = Path.Combine(sdsRoot, "tables", "tables.sds");

        JsonElement listed = await CallAsync(client, "list_sds_files",
            new Dictionary<string, object?> { ["directoryPath"] = sdsRoot, ["limit"] = 5 }).ConfigureAwait(false);
        check("list_sds_files finds the install's archives and pages them",
            listed.GetProperty("total").GetInt32() > 5
            && listed.GetProperty("returned").GetInt32() == 5,
            listed.GetProperty("total").GetInt32().ToString(CultureInfo.InvariantCulture) + " found");

        if (!File.Exists(tablesSds))
        {
            skip("tables.sds not present in this install — the archive tools were not exercised");
            return;
        }

        JsonElement stats = await CallAsync(client, "get_sds_stats",
            new Dictionary<string, object?> { ["filePath"] = tablesSds }).ConfigureAwait(false);
        check("get_sds_stats breaks an archive down by resource type",
            stats.GetProperty("resourceCount").GetInt32() > 0
            && stats.GetProperty("types").GetArrayLength() > 0
            && stats.GetProperty("decompressedBytes").GetInt64() > 0,
            stats.GetProperty("resourceCount").GetInt32().ToString(CultureInfo.InvariantCulture) + " resources");

        JsonElement opened = await CallAsync(client, "open_sds_file",
            new Dictionary<string, object?> { ["filePath"] = tablesSds, ["limit"] = 3 }).ConfigureAwait(false);
        check("open_sds_file reports a version-19 PC archive and honours the page limit",
            opened.GetProperty("version").GetUInt32() == 19
            && opened.GetProperty("platform").GetString() == "PC"
            && opened.GetProperty("resources").GetArrayLength() <= 3,
            "v" + opened.GetProperty("version").GetUInt32().ToString(CultureInfo.InvariantCulture));

        // Truncation is the one behaviour of extract_resource a caller MUST be able to trust: a
        // silently short payload decoded as if it were whole is a bug that surfaces far from here.
        JsonElement extracted = await CallAsync(client, "extract_resource",
            new Dictionary<string, object?>
            {
                ["filePath"] = tablesSds,
                ["resourceIndex"] = 0,
                ["maxBytes"] = 16,
            }).ConfigureAwait(false);
        check("extract_resource truncates to maxBytes and says that it did",
            extracted.GetProperty("returnedSize").GetInt32() == 16
            && extracted.GetProperty("truncated").GetBoolean()
            && Convert.FromBase64String(extracted.GetProperty("base64Data").GetString()!).Length == 16);

        JsonElement outOfRange = await CallAsync(client, "get_resource_info",
            new Dictionary<string, object?> { ["filePath"] = tablesSds, ["resourceIndex"] = 999999 }).ConfigureAwait(false);
        check("an out-of-range resource index is refused with the real count",
            !outOfRange.GetProperty("success").GetBoolean()
            && (outOfRange.GetProperty("error").GetString() ?? "").Contains("out of range", StringComparison.Ordinal),
            outOfRange.GetProperty("error").GetString() ?? "");

        JsonElement tables = await CallAsync(client, "list_tables",
            new Dictionary<string, object?> { ["sdsPath"] = tablesSds }).ConfigureAwait(false);
        bool anyTables = tables.GetProperty("success").GetBoolean() && tables.GetProperty("count").GetInt32() > 0;
        check("list_tables enumerates the Table resources of tables.sds", anyTables,
            anyTables
                ? tables.GetProperty("count").GetInt32().ToString(CultureInfo.InvariantCulture) + " tables"
                : "none");

        if (anyTables)
        {
            JsonElement first = tables.GetProperty("tables")[0];
            string tableName = first.GetProperty("name").GetString()!;
            int columnCount = first.GetProperty("columnCount").GetInt32();

            JsonElement rows = await CallAsync(client, "dump_rows",
                new Dictionary<string, object?>
                {
                    ["sdsPath"] = tablesSds,
                    ["tableName"] = tableName,
                    ["limit"] = 3,
                }).ConfigureAwait(false);
            // Cells are positional against the reported columns — a row of a different width would
            // put every lookup off by one, so the widths are compared rather than assumed.
            bool widthsAgree = rows.GetProperty("rows").EnumerateArray()
                .All(r => r.GetProperty("cells").GetArrayLength() == columnCount);
            check("dump_rows returns rows whose cell count matches the column list",
                rows.GetProperty("success").GetBoolean() && widthsAgree
                && rows.GetProperty("returned").GetInt32() <= 3,
                tableName + " x" + columnCount.ToString(CultureInfo.InvariantCulture));

            JsonElement row = await CallAsync(client, "lookup_by_row",
                new Dictionary<string, object?>
                {
                    ["sdsPath"] = tablesSds,
                    ["tableName"] = tableName,
                    ["rowIndex"] = 0,
                }).ConfigureAwait(false);
            check("lookup_by_row pairs every cell with its column hash and type",
                row.GetProperty("success").GetBoolean()
                && row.GetProperty("cells").GetArrayLength() == columnCount);
        }

        string? streamMap = MafiaEnvironment.StreamMapPath;
        if (streamMap is null || !File.Exists(streamMap))
        {
            skip("no StreamMapa.bin in this install — parse_stream_map was not exercised");
        }
        else
        {
            JsonElement summary = await CallAsync(client, "parse_stream_map",
                new Dictionary<string, object?> { ["filePath"] = streamMap }).ConfigureAwait(false);
            check("parse_stream_map summarizes the groups, lines and loaders",
                summary.GetProperty("success").GetBoolean()
                && summary.GetProperty("loaderCount").GetInt32() > 0
                && summary.GetProperty("lineCount").GetInt32() > 0
                && summary.GetProperty("groups").GetArrayLength() > 0,
                summary.GetProperty("loaderCount").GetInt32().ToString(CultureInfo.InvariantCulture) + " loaders");

            JsonElement loaders = await CallAsync(client, "parse_stream_map",
                new Dictionary<string, object?>
                {
                    ["filePath"] = streamMap,
                    ["section"] = "loaders",
                    ["limit"] = 4,
                }).ConfigureAwait(false);
            check("parse_stream_map pages the loaders section and names each asset",
                loaders.GetProperty("returned").GetInt32() == 4
                && loaders.GetProperty("loaders")[0].GetProperty("path").GetString()!.Length > 0,
                loaders.GetProperty("loaders")[0].GetProperty("path").GetString() ?? "");
        }

        JsonElement closed = await CallAsync(client, "close_sds_file",
            new Dictionary<string, object?> { ["filePath"] = tablesSds }).ConfigureAwait(false);
        check("close_sds_file drops the archive the earlier tools cached",
            closed.GetProperty("success").GetBoolean() && closed.GetProperty("closed").GetBoolean());
    }

    /// <summary>Calls one tool and parses its JSON answer. The element is cloned so it outlives the
    /// document it was parsed from.</summary>
    private static async Task<JsonElement> CallAsync(
        McpClient client, string tool, IReadOnlyDictionary<string, object?>? arguments)
    {
        CallToolResult result = await client.CallToolAsync(tool, arguments).ConfigureAwait(false);
        string text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            // A tool that threw rather than returning reaches the client as the SDK's own prose
            // ("An error occurred invoking 'x'."), not as JSON — most often because a DI parameter
            // could not be resolved. The bare reader exception names neither the tool nor what came
            // back, and both are the whole diagnosis.
            throw new InvalidOperationException($"'{tool}' did not answer with JSON: {text}", ex);
        }
    }

    /// <summary>
    /// Guards the server's one real defence against a web page the user happens to be visiting.
    /// An attacker who points their own domain at 127.0.0.1 reaches this port with a request the
    /// browser considers same-origin; only the host allow-list turns it away. That the allow-list is
    /// in force depends on framework wiring that reading <c>Build()</c> alone will not reveal — an
    /// SDK bump, or a switch from the literal loopback address to a host name, could undo it in
    /// silence. Hence an assertion rather than trust.
    /// </summary>
    private static async Task CheckForeignHostRejectedAsync(string address, CheckFn check)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address))
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Host = "evil.example.com";
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using HttpResponseMessage response = await http.SendAsync(request).ConfigureAwait(false);
            check("a request under a foreign host name is refused",
                response.StatusCode == HttpStatusCode.BadRequest, response.StatusCode.ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            check("a request under a foreign host name is refused", false, ex.Message);
        }
    }

    /// <summary>
    /// Closing the application moments after launching it queues a stop while the start may not have
    /// begun; whichever thread reaches the gate first wins, so a stop must be final even when it
    /// arrives first. Ordering the calls this way asserts that guarantee without racing for it —
    /// otherwise the start would go on to bind a port while the process is already shutting down.
    /// </summary>
    private static async Task CheckStopWinsAsync(CheckFn check)
    {
        var host = new McpServerHost(new McpHostOptions { Port = 0 });
        await using (host.ConfigureAwait(false))
        {
            await host.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await host.StartAsync().ConfigureAwait(false);
            check("a start that lands after a stop leaves the server down",
                host.State.Status == McpServerStatus.Stopped, host.State.Status.ToString());
        }
    }

    /// <summary>A second server on a taken port must report the clash, not throw or hang.</summary>
    private static async Task CheckPortClashAsync(int busyPort, CheckFn check)
    {
        var second = new McpServerHost(new McpHostOptions { Port = busyPort });
        await using (second.ConfigureAwait(false))
        {
            await second.StartAsync().ConfigureAwait(false);
            McpServerState state = second.State;
            check("a second server on a busy port fails instead of throwing",
                state.Status == McpServerStatus.Failed, state.Status.ToString());
            check("the clash is explained in plain English",
                state.Error is not null && state.Error.Contains("already in use", StringComparison.Ordinal),
                state.Error ?? "<null>");
        }
    }

    /// <summary>
    /// A hand-rolled MCP request, deliberately not going through the SDK client so it can be aimed at
    /// a server that may already be gone. The Accept header carries both media types because the
    /// transport rejects anything else with "406 Not Acceptable" — and a 406 from a live server is
    /// indistinguishable here from a refused connection, which would make the caller's assertions
    /// pass no matter what the server did.
    /// </summary>
    private static async Task<bool> RespondsAsync(string address)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address))
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using HttpResponseMessage response = await http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static void Finish(StringBuilder sb, string outFile, string name, int pass, int fail)
    {
        sb.Insert(0, $"{name} PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }
}
