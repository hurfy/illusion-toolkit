using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Illusion.Mcp.Tools;

/// <summary>
/// How every tool answers. A tool returns one JSON string; the SDK wraps it in a text content block
/// and the model reads it. Every payload carries a <c>success</c> flag first, so a client can branch
/// on it without knowing the shape of the rest.
/// <para>
/// <b>Failures carry real detail.</b> The MCP SDK would otherwise reduce an exception to
/// "An error occurred invoking 'X'.", which makes a format bug ("this archive's Collisions payload
/// throws six frames inside the section reader") undiagnosable from the client — and diagnosing
/// exactly that is what these tools are for. The full type chain and the first stack frames are
/// therefore serialized into the response. That is safe only because the endpoint is unreachable
/// from off this machine (see <see cref="McpServerHost"/>); it would be an information leak on any
/// server that were not loopback-bound.
/// </para>
/// One caveat the caller should know: <see cref="Exception.Message"/> comes from the framework or
/// the OS and is localized, so a failure on a non-English Windows can answer in that language. The
/// exception <i>type</i> names beside it are identifiers and always readable.
/// </summary>
internal static class ToolResult
{
    private const int MaxChainDepth = 6;
    private const int MaxStackFrames = 10;

    private static readonly JsonSerializerOptions Options = new()
    {
        // Null members are noise in a response the model has to read; leaving them out keeps the
        // optional halves of the "filePath or base64Data" tools from padding every answer.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes a successful payload. Callers put <c>success = true</c> in it themselves,
    /// so the flag reads in source right beside the data it describes.</summary>
    public static string Json(object payload) => JsonSerializer.Serialize(payload, Options);

    /// <summary>The standard failure payload for a catch block.</summary>
    public static string Fail(Exception ex) =>
        Json(new { success = false, error = ex.Message, errorDetail = Detail(ex) });

    /// <summary>
    /// A failure the tool detected itself — a bad argument combination, a missing file — rather than
    /// one thrown from underneath. No stack: there is nothing to diagnose, the caller simply asked
    /// for something that cannot be served.
    /// </summary>
    public static string Invalid(string message) => Json(new { success = false, error = message });

    /// <summary>Unwraps <see cref="TargetInvocationException"/> so a reflection-invoked failure points
    /// at the real source, then walks the inner-exception chain.</summary>
    private static object Detail(Exception ex)
    {
        while (ex is TargetInvocationException { InnerException: { } inner })
        {
            ex = inner;
        }

        var chain = new List<object>(MaxChainDepth);
        for (Exception? e = ex; e is not null && chain.Count < MaxChainDepth; e = e.InnerException)
        {
            chain.Add(new { type = e.GetType().FullName, message = e.Message, stack = Frames(e.StackTrace) });
        }
        return new { chain };
    }

    private static string[] Frames(string? stack)
    {
        if (string.IsNullOrEmpty(stack))
        {
            return [];
        }

        string[] lines = stack.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        int take = Math.Min(lines.Length, MaxStackFrames);
        var trimmed = new string[take];
        for (int i = 0; i < take; i++)
        {
            trimmed[i] = lines[i].Trim();
        }
        return trimmed;
    }
}
