using System.ComponentModel;
using System.Globalization;
using System.Text;
using Illusion.Formats.Archive.Handlers;
using Illusion.Formats.Hashing;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// The small tools a modding session keeps reaching for: the two FNV hashes the game identifies
/// everything by, base conversion, and finding game files on disk.
/// <para>
/// Mafia II stores almost no names. A frame references its material by an FNV64 of the material's
/// name, a texture by the FNV64 of its file name, a data-table column by the FNV32 of the column's
/// name — so working out "what is 0x8A7C…?" is most of what reverse-engineering a file involves,
/// and going the other way (hash a guess, see whether it matches) is how that question gets
/// answered. These tools are that loop.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class UtilityTools
{
    /// <summary>
    /// The extensions a game-file listing recognizes. Taken from the archive's own extraction table
    /// rather than a hand-kept list, so a resource type gaining a handler shows up here too — plus
    /// the few formats that never live inside an archive.
    /// </summary>
    private static readonly HashSet<string> GameExtensions =
        new(ResourceHandlerRegistry.FileExtensions.Values, StringComparer.OrdinalIgnoreCase)
        {
            ".sds", ".mtl", ".bin", ".tbl", ".lua", ".sds_patch", ".patch",
        };

    [McpServerTool(Name = "hash_fnv32")]
    [Description("Compute the FNV32 hash of a string. Mafia II identifies data-table columns and several resource fields by this hash. Returns decimal, hex and the signed reinterpretation.")]
    public static string HashFnv32(
        [Description("String to hash.")] string input,
        [Description("Encode as Windows-1252 before hashing, which is what the game's own tools did. Default true; false hashes UTF-8 instead.")] bool useCodePage1252 = true)
    {
        try
        {
            uint hash = Fnv32.Hash(input, EncodingFor(useCodePage1252));
            return ToolResult.Json(new
            {
                success = true,
                input,
                useCodePage1252,
                fnv32 = hash,
                hex = Hex(hash),
                signed = unchecked((int)hash),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "hash_fnv64")]
    [Description("Compute the FNV64 hash of a string. Mafia II identifies textures, materials and named resources by this hash. Returns decimal, hex and the signed reinterpretation.")]
    public static string HashFnv64(
        [Description("String to hash.")] string input,
        [Description("Encode as Windows-1252 before hashing, which is what the game's own tools did. Default true; false hashes UTF-8 instead.")] bool useCodePage1252 = true)
    {
        try
        {
            ulong hash = Fnv64.Hash(input, EncodingFor(useCodePage1252));
            return ToolResult.Json(new
            {
                success = true,
                input,
                useCodePage1252,
                fnv64 = hash,
                hex = Hex(hash),
                signed = unchecked((long)hash),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "hash_batch")]
    [Description("Compute both FNV32 and FNV64 for many strings at once. This is how a guessed name list gets checked against hashes found in a file.")]
    public static string HashBatch(
        [Description("Strings to hash, separated by newlines or commas.")] string inputs,
        [Description("Encode as Windows-1252 before hashing. Default true.")] bool useCodePage1252 = true)
    {
        try
        {
            Encoding encoding = EncodingFor(useCodePage1252);
            string[] values = inputs.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries);

            var results = new List<object>(values.Length);
            foreach (string raw in values)
            {
                string value = raw.Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                uint h32 = Fnv32.Hash(value, encoding);
                ulong h64 = Fnv64.Hash(value, encoding);
                results.Add(new
                {
                    input = value,
                    fnv32 = h32,
                    fnv32Hex = Hex(h32),
                    fnv64 = h64,
                    fnv64Hex = Hex(h64),
                });
            }

            return ToolResult.Json(new { success = true, useCodePage1252, count = results.Count, hashes = results });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "convert_number")]
    [Description("Convert a number between decimal, hexadecimal (0x…) and binary (0b…). Returns every representation plus the signed reinterpretations and the little-endian bytes — the conversion a hash found in one file needs before it can be matched against another.")]
    public static string ConvertNumber(
        [Description("The value, as decimal, 0x-prefixed hex, or 0b-prefixed binary.")] string input)
    {
        try
        {
            string text = input.Trim();
            if (text.Length == 0)
            {
                return ToolResult.Invalid("input is empty");
            }

            // Signs are accepted so a value copied out of a debugger (which prints these hashes as
            // negative ints) converts back without the caller doing the two's-complement by hand.
            bool negative = text.StartsWith('-');
            string digits = negative || text.StartsWith('+') ? text[1..] : text;

            ulong magnitude;
            string detected;
            if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                detected = "hex";
                if (!ulong.TryParse(digits[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out magnitude))
                {
                    return ToolResult.Invalid($"'{input}' is not a 64-bit hexadecimal value");
                }
            }
            else if (digits.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                detected = "binary";
                try
                {
                    magnitude = Convert.ToUInt64(digits[2..], 2);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
                {
                    return ToolResult.Invalid($"'{input}' is not a 64-bit binary value");
                }
            }
            else
            {
                detected = "decimal";
                if (!ulong.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out magnitude))
                {
                    return ToolResult.Invalid($"'{input}' is not a 64-bit integer");
                }
            }

            ulong value = negative ? unchecked((ulong)-(long)magnitude) : magnitude;

            return ToolResult.Json(new
            {
                success = true,
                input,
                detectedBase = detected,
                unsigned64 = value,
                signed64 = unchecked((long)value),
                unsigned32 = unchecked((uint)value),
                signed32 = unchecked((int)value),
                hex = Hex(value),
                hex32 = Hex(unchecked((uint)value)),
                binary = "0b" + Convert.ToString(unchecked((long)value), 2),
                // Little-endian, because that is the order these values sit in on disk: a hash read
                // out of a hex dump matches this array directly.
                bytesLittleEndian = BitConverter.GetBytes(value),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "list_game_files")]
    [Description("List the game files in a directory — archives, material libraries, textures, navigation data and every other extension the toolkit recognizes. Use extensionFilter to narrow it.")]
    public static string ListGameFiles(
        [Description("Directory to search.")] string directoryPath,
        [Description("Comma-separated extensions to keep, with or without the dot (e.g. 'sds,mtl'). Omit for every recognized extension.")] string? extensionFilter = null,
        [Description("Search subdirectories too. Default true.")] bool recursive = true,
        [Description("Index of the first file to return. Default 0.")] int offset = 0,
        [Description("How many files to return. Default 100.")] int limit = 0)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                return ToolResult.Invalid($"no such directory: {directoryPath}");
            }

            HashSet<string> wanted = GameExtensions;
            if (!string.IsNullOrWhiteSpace(extensionFilter))
            {
                wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string part in extensionFilter.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string ext = part.Trim();
                    wanted.Add(ext.StartsWith('.') ? ext : "." + ext);
                }
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
            };
            List<FileInfo> found = Directory
                .EnumerateFiles(directoryPath, "*", options)
                .Where(p => wanted.Contains(Path.GetExtension(p)))
                .Select(p => new FileInfo(p))
                .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            (int start, int count) = Page.Clamp(offset, limit);
            List<FileInfo> window = Page.Slice(found, start, count);

            return ToolResult.Json(new
            {
                success = true,
                directoryPath,
                recursive,
                extensions = wanted.OrderBy(e => e, StringComparer.Ordinal),
                total = found.Count,
                offset = start,
                limit = count,
                returned = window.Count,
                files = window.Select(f => new
                {
                    path = f.FullName,
                    name = f.Name,
                    extension = f.Extension,
                    size = f.Length,
                }),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "get_configured_games")]
    [Description("Report the game install the toolkit is configured against: the configured path, whether it resolved, and the folders derived from it (pc, root, resources, StreamMapa.bin). Call this first to find out where the game's files actually are.")]
    public static string GetConfiguredGames(IGameEnvironment environment)
    {
        try
        {
            string? configured = environment.ConfiguredPath;
            string? pc = environment.PcFolder;

            return ToolResult.Json(new
            {
                success = true,
                // Illusion configures ONE install (unlike the legacy toolkit's multi-game list), so
                // this is a single object rather than an array — but it is still reported as "the
                // configured game" so a caller can tell "not set" from "set but unusable".
                configured = configured is not null,
                configuredPath = configured,
                configuredPathExists = configured is not null && Directory.Exists(configured),
                // False until the launcher has resolved the path into a usable install; every folder
                // below is null while that is the case.
                initialized = environment.IsInitialized,
                pcFolder = pc,
                gameRoot = environment.GameRoot,
                sdsFolder = pc is null ? null : Path.Combine(pc, "sds"),
                resourcesFolder = environment.ResourcesFolder,
                streamMapPath = environment.StreamMapPath,
                streamMapExists = environment.StreamMapPath is not null && File.Exists(environment.StreamMapPath),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    // ── shared ──

    /// <summary>
    /// Which bytes the hash actually runs over. Mafia's tools wrote strings as Windows-1252, so that
    /// is the default and the one that reproduces the hashes in the game's files; UTF-8 differs only
    /// for non-ASCII input, but when it differs the hash is simply wrong for the game.
    /// </summary>
    private static Encoding EncodingFor(bool useCodePage1252) =>
        useCodePage1252 ? Encoding.GetEncoding(1252) : Encoding.UTF8;

    private static string Hex(uint value) =>
        string.Create(CultureInfo.InvariantCulture, $"0x{value:X8}");

    private static string Hex(ulong value) =>
        string.Create(CultureInfo.InvariantCulture, $"0x{value:X16}");
}
