using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// Reading Mafia II <c>.eff</c> (Effects) resources.
/// <para>
/// <b>Partial by design, and the tools say so in their answers.</b> Illusion's format layer types
/// the <c>.eff</c> container header and carries the reflected particle/FX property tree after it as
/// an opaque capsule — enough for the file to round-trip byte-exact, which is what the editor needs,
/// but not a decode. So these tools identify and measure an effects resource; they do not list its
/// generations, operators, parameters, frames or sounds.
/// </para>
/// Every response carries <c>treeDecoded: false</c> rather than leaving a caller to infer the limit
/// from a suspiciously short answer.
/// </summary>
[McpServerToolType]
public sealed class EffectsTools
{
    [McpServerTool(Name = "parse_effects_file")]
    [Description("Parse a Mafia II .eff (Effects) file and report its container header. NOTE: this toolkit does not decode the effects property tree — the response says so with treeDecoded:false. Use it to identify and size an effects resource, not to read the effects inside it.")]
    public static string ParseEffectsFile(
        [Description("Path to a .eff file.")] string filePath)
    {
        return Run(filePath, null);
    }

    [McpServerTool(Name = "parse_effects_from_bytes")]
    [Description("Parse .eff (Effects) content from base64 bytes, e.g. the output of extract_resource on an 'Effects' resource. Reports the container header only — see parse_effects_file for the same caveat.")]
    public static string ParseEffectsFromBytes(
        [Description("Base64 of an Effects payload.")] string base64Data)
    {
        return Run(null, base64Data);
    }

    private static string Run(string? filePath, string? base64Data)
    {
        try
        {
            if (!DecodeTools.TryPayload(filePath, base64Data, allowNeither: false, out byte[]? payload, out string complaint))
            {
                return ToolResult.Invalid(complaint);
            }

            return ToolResult.Json(Decoders.Effects(payload!));
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }
}
