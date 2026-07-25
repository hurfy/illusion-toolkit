using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Illusion.Mcp;
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
    /// <summary>Records one assertion. A delegate rather than an <c>Action</c> so the optional
    /// <paramref name="detail"/> survives being passed between the probe's steps.</summary>
    private delegate void CheckFn(string name, bool ok, string detail = "");

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

        try
        {
            // Probes run inside App.OnStartup — on the UI thread, and before the dispatcher loop has
            // started. Anything that awaited back onto that context would wait forever, since nothing
            // will ever pump it. Driving the scenario from the thread pool sidesteps that entirely.
            Task.Run(() => RunScenarioAsync(Check)).GetAwaiter().GetResult();
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

    private static async Task RunScenarioAsync(CheckFn check)
    {
        // Port 0: the OS picks a free one, so the probe can run while the application itself is open
        // on the default port — and two probes can run at once.
        var host = new McpServerHost(new McpHostOptions { Port = 0 });
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

            await ExerciseClientAsync(state.Address, check).ConfigureAwait(false);
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
    private static async Task ExerciseClientAsync(string address, CheckFn check)
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
        check("the tool carries a description for the model to read",
            !string.IsNullOrWhiteSpace(ping?.Description));

        CallToolResult result = await client.CallToolAsync("ping").ConfigureAwait(false);
        string text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        check("calling ping succeeds", result.IsError != true);
        check("ping answers with its version banner", text.Contains("pong", StringComparison.Ordinal), text);
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
