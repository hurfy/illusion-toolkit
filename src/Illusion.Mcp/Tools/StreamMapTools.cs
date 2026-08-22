using System.ComponentModel;
using Illusion.Formats.StreamMap;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// Reading <c>StreamMapa.bin</c> — the table that decides which SDS archives the game loads, and
/// when.
/// <para>
/// The file is three related arrays. <b>Groups</b> name the categories a line belongs to.
/// <b>Lines</b> are the named streaming states the game's Lua switches between ("the player is in
/// this district", "this mission is running"). <b>Loaders</b> are the individual assets, each with
/// the line range <c>[Start, End]</c> it is active for — so working out what a district actually
/// loads means finding its line's id and then every loader whose range covers it.
/// </para>
/// This is the file a modder re-points to make the game load their archive instead of a stock one,
/// which makes reading it the first step of nearly every substantial mod.
/// </summary>
[McpServerToolType]
public sealed class StreamMapTools
{
    [McpServerTool(Name = "parse_stream_map")]
    [Description("Decode a StreamMapa.bin. section=summary returns the counts and the full group list; section=lines or section=loaders returns that array, paginated. Pass either filePath or base64Data. Find the game's own file with get_configured_games.")]
    public static string ParseStreamMap(
        [Description("Full path to a StreamMapa.bin.")] string? filePath = null,
        [Description("Base64 of a StreamMap payload. Omit if using filePath.")] string? base64Data = null,
        [Description("Which part to return: 'summary' (default), 'lines' or 'loaders'.")] string? section = null,
        [Description("Index of the first entry to return, for the lines and loaders sections. Default 0.")] int offset = 0,
        [Description("How many entries to return, for the lines and loaders sections. Default 100.")] int limit = 0)
    {
        try
        {
            if (!TryRead(filePath, base64Data, out StreamMapFile map, out string complaint))
            {
                return ToolResult.Invalid(complaint);
            }

            string which = (section ?? "summary").Trim().ToLowerInvariant();
            (int start, int count) = Page.Clamp(offset, limit);
            string source = filePath ?? "base64Data";

            switch (which)
            {
                case "summary":
                    return ToolResult.Json(new
                    {
                        success = true,
                        source,
                        section = which,
                        groupCount = map.GroupHeaders.Length,
                        lineCount = map.Lines.Length,
                        loaderCount = map.Loaders.Length,
                        // Groups are few and every other section refers to them by index, so they
                        // come back whole rather than paginated.
                        groups = map.GroupHeaders.Select((name, index) => new { index, name }),
                    });

                case "lines":
                {
                    List<StreamMapLine> window = Page.Slice(map.Lines, start, count);
                    return ToolResult.Json(new
                    {
                        success = true,
                        source,
                        section = which,
                        total = map.Lines.Length,
                        offset = start,
                        limit = count,
                        returned = window.Count,
                        lines = window.Select(l => new
                        {
                            name = l.Name,
                            lineId = l.LineID,
                            groupId = l.GroupID,
                            group = l.GroupID >= 0 && l.GroupID < map.GroupHeaders.Length
                                ? map.GroupHeaders[l.GroupID]
                                : null,
                        }),
                    });
                }

                case "loaders":
                {
                    List<StreamMapLoader> window = Page.Slice(map.Loaders, start, count);
                    return ToolResult.Json(new
                    {
                        success = true,
                        source,
                        section = which,
                        total = map.Loaders.Length,
                        offset = start,
                        limit = count,
                        returned = window.Count,
                        loaders = window.Select(l => new
                        {
                            path = l.Path,
                            entity = l.Entity,
                            // The group type says what kind of asset this is — geometry (City, Car,
                            // Weapons) or service data (Script, Sound, GUI). Reported by name and
                            // number because the engine's values have gaps and a file can carry one
                            // the enum does not cover.
                            type = l.Type.ToString(),
                            typeId = (int)l.Type,
                            startLine = l.Start,
                            endLine = l.End,
                        }),
                    });
                }

                default:
                    return ToolResult.Invalid(
                        $"unknown section '{section}' — expected summary, lines or loaders");
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    /// <summary>Resolves the file-or-bytes pair every tool here accepts.</summary>
    internal static bool TryRead(
        string? filePath,
        string? base64Data,
        out StreamMapFile map,
        out string complaint)
    {
        map = null!;
        complaint = "";

        if ((filePath is null) == (base64Data is null))
        {
            complaint = "pass exactly one of filePath or base64Data";
            return false;
        }

        if (filePath is not null)
        {
            if (!File.Exists(filePath))
            {
                complaint = $"no such file: {filePath}";
                return false;
            }
            map = StreamMapFile.Load(filePath);
            return true;
        }

        map = StreamMapFile.Read(Convert.FromBase64String(base64Data!));
        return true;
    }
}
