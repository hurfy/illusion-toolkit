using System.ComponentModel;
using System.Text;
using Arrowgene.Lua.Decompiler;
using Arrowgene.Lua.Decompiler.Decompile;
using Arrowgene.Lua.Decompiler.Parse;
using Illusion.Formats.Archive;
using Illusion.Formats.ResourceFormats;
using Illusion.Mcp.Services;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// Reading the game's logic. Missions, doors, telephones and the whole director are compiled Lua,
/// shipped as bytecode inside an SDS <c>Script</c> resource — so "what does this mission actually
/// do?" is a decompiler question, and nothing else in the toolkit answers it.
/// <para>
/// Mafia II's scripts are Lua 5.1, but built with <c>lua_Number</c> as a 4-byte float rather than
/// the usual double. A reader that assumes the standard widths misparses every constant in every
/// script, so what matters in a decompiler here is that it honours the sizes the bytecode header
/// declares. Measured against the retail corpus, this one decompiles all 2021 scripts in the game.
/// </para>
/// The scripts are stripped of debug information, so locals come back as <c>L0_1</c>, <c>A0_2</c>
/// and so on. That is the bytecode's own limit rather than the decompiler's — the names were never
/// shipped. Globals, calls, string constants and control flow are all recovered intact, which is
/// what makes the output readable.
/// </summary>
[McpServerToolType]
public sealed class LuaTools
{
    /// <summary>
    /// Decompiled Lua runs long — one mission script in the retail game is 3,000 lines — so source
    /// is paged like every other array here rather than returned whole by default.
    /// </summary>
    private const int DefaultLines = 400;

    [McpServerTool(Name = "decompile_lua")]
    [Description("Decompile one compiled Lua chunk back to source. Pass either filePath (a .lua the toolkit unpacked) or base64Data. Plain-text Lua and non-Lua data are rejected rather than half-read. Output is paged by line — offset/limit.")]
    public static string DecompileLua(
        [Description("Path to a compiled .lua file. Omit if using base64Data.")] string? filePath = null,
        [Description("Base64 of one compiled Lua chunk. Omit if using filePath.")] string? base64Data = null,
        [Description("First source line to return. Default 0.")] int offset = 0,
        [Description("How many source lines to return. Default 400.")] int limit = 0)
    {
        try
        {
            if (!DecodeTools.TryPayload(filePath, base64Data, allowNeither: false, out byte[]? payload, out string complaint))
            {
                return ToolResult.Invalid(complaint);
            }

            if (!IsBytecode(payload!, out LuaFileInfo? info, out complaint))
            {
                return ToolResult.Invalid(complaint);
            }

            return ToolResult.Json(new
            {
                success = true,
                source = filePath ?? "base64Data",
                luaVersion = info!.GetVersionString(),
                bytecodeBytes = payload!.Length,
                script = Source(Decompile(payload), offset, limit),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "decompile_script_resource")]
    [Description("Open an SDS, unwrap its Script resource and read the Lua inside. With scriptIndex left at -1 it LISTS the scripts (names, sizes, whether each is bytecode) without decompiling — start there. Set scriptIndex to decompile that one script. Select the resource with resourceIndex, or leave it -1 to take the first Script resource.")]
    public static string DecompileScriptResource(
        ArchiveService archives,
        [Description("Full path to the .sds file.")] string sdsPath,
        [Description("Index of the Script resource. -1 (default) takes the first one.")] int resourceIndex = -1,
        [Description("Which script inside the resource to decompile. -1 (default) lists them instead.")] int scriptIndex = -1,
        [Description("First source line to return. Default 0.")] int offset = 0,
        [Description("How many source lines to return. Default 400.")] int limit = 0)
    {
        try
        {
            CachedArchive cached = archives.Open(sdsPath);

            int index = resourceIndex;
            if (index < 0)
            {
                index = -1;
                for (int i = 0; i < cached.Archive.Entries.Count; i++)
                {
                    if (SdsTools.TypeNameOf(cached, i).Equals("Script", StringComparison.Ordinal))
                    {
                        index = i;
                        break;
                    }
                }
                if (index < 0)
                {
                    return ToolResult.Invalid($"'{cached.Path}' holds no Script resource");
                }
            }

            if (!SdsTools.InRange(cached, index, out string complaint))
            {
                return ToolResult.Invalid(complaint);
            }

            string type = SdsTools.TypeNameOf(cached, index);
            if (!type.Equals("Script", StringComparison.Ordinal))
            {
                return ToolResult.Invalid($"resource {index} is a '{type}', not a Script");
            }

            ResourceEntry entry = cached.Archive.Entries[index];
            var resource = new ScriptResource();
            using (var stream = new MemoryStream(entry.Data ?? [], writable: false))
            {
                resource.Deserialize(entry.Version, stream, cached.Archive.Endian);
            }

            // Listing first, and by default. A Script resource holds many chunks and decompiling one
            // is expensive next to naming them all, so the cheap answer is the one a caller gets
            // without asking for anything in particular.
            if (scriptIndex < 0)
            {
                return ToolResult.Json(new
                {
                    success = true,
                    path = cached.Path,
                    resourceIndex = index,
                    resourcePath = resource.Path,
                    count = resource.Scripts.Count,
                    scripts = resource.Scripts.Select((s, i) => new
                    {
                        index = i,
                        name = s.Name,
                        nameHash = s.NameHash,
                        bytes = s.Data?.Length ?? 0,
                        isBytecode = IsBytecode(s.Data ?? [], out LuaFileInfo? info, out _),
                        luaVersion = LuaVersionOrNull(s.Data ?? []),
                    }),
                });
            }

            if (scriptIndex >= resource.Scripts.Count)
            {
                return ToolResult.Invalid(
                    $"scriptIndex {scriptIndex} is out of range — the resource holds {resource.Scripts.Count} scripts");
            }

            ScriptData script = resource.Scripts[scriptIndex];
            byte[] payload = script.Data ?? [];
            if (!IsBytecode(payload, out LuaFileInfo? scriptInfo, out complaint))
            {
                return ToolResult.Invalid($"'{script.Name}': {complaint}");
            }

            return ToolResult.Json(new
            {
                success = true,
                path = cached.Path,
                resourceIndex = index,
                scriptIndex,
                name = script.Name,
                nameHash = script.NameHash,
                luaVersion = scriptInfo!.GetVersionString(),
                bytecodeBytes = payload.Length,
                script = Source(Decompile(payload), offset, limit),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    // ── decompilation ──

    /// <summary>
    /// Decompiles in memory. The library's own entry point reads and writes files; driving the same
    /// pieces over a MemoryStream avoids a temp file per call for payloads that came out of an
    /// archive and were never on disk. Verified to produce byte-identical output to the file path.
    /// </summary>
    private static string Decompile(byte[] bytecode)
    {
        var header = new BHeader(LuaByteBuffer.Wrap(bytecode), new Configuration());
        var decompiler = new Decompiler(header.main);
        var state = decompiler.Decompile();

        using var buffer = new MemoryStream();
        var output = new Output(new FileOutputProvider(buffer));
        decompiler.Print(state, output);
        output.Finish();
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Whether this is compiled bytecode at all. Refusing plain-text Lua matters: handed a source
    /// file the parser would read the first characters as a header and fail somewhere deep, and the
    /// caller would be told about a malformed chunk rather than that they passed the wrong file.
    /// </summary>
    private static bool IsBytecode(byte[] payload, out LuaFileInfo? info, out string complaint)
    {
        info = null;
        complaint = "";
        if (payload.Length == 0)
        {
            complaint = "the payload is empty";
            return false;
        }

        try
        {
            info = LuaFile.Identify(payload);
        }
        catch (Exception ex)
        {
            complaint = "could not identify the payload: " + ex.Message;
            return false;
        }

        if (info.Type == LuaFileType.LuaCompiled)
        {
            return true;
        }

        complaint = info.Type == LuaFileType.LuaSource
            ? "this is plain-text Lua, not compiled bytecode — there is nothing to decompile"
            : "this is not Lua bytecode (no Lua signature)";
        return false;
    }

    private static string? LuaVersionOrNull(byte[] payload) =>
        IsBytecode(payload, out LuaFileInfo? info, out _) ? info!.GetVersionString() : null;

    /// <summary>Pages the decompiled source by line and reports how much there is.</summary>
    private static object Source(string source, int offset, int limit)
    {
        string[] all = source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        (int start, int count) = Page.Clamp(offset, limit <= 0 ? DefaultLines : limit);
        List<string> window = Page.Slice(all, start, count);

        return new
        {
            totalLines = all.Length,
            offset = start,
            limit = count,
            returned = window.Count,
            truncated = start + window.Count < all.Length,
            // One string rather than an array of lines: it is source code, and a caller pasting it
            // somewhere wants it to already look like source.
            text = string.Join("\n", window),
        };
    }
}
