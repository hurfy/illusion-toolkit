using System.ComponentModel;
using Illusion.Mcp.Services;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// One call instead of three. Reaching a decoded resource otherwise means
/// <c>list_resources</c> to find it, <c>extract_resource</c> to get its bytes, then the matching
/// <c>decode_*</c> — and the middle step moves a megabyte of base64 through the model's context for
/// no reason, or silently truncates and makes the decode fail somewhere confusing.
/// <para>
/// This routes on the resource's own type name, so the caller does not have to know which decoder a
/// payload needs either.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class ResourceDecodeTools
{
    /// <summary>The resource types this tool can route, named once so the check and the message a
    /// caller gets back cannot drift apart.</summary>
    private static readonly string[] Decodable =
        ["Actors", "FrameResource", "ItemDesc", "Collisions", "Effects"];

    [McpServerTool(Name = "decode_resource")]
    [Description("Extract a resource from an SDS and decode it in one step, choosing the decoder from its type (Actors, FrameResource, ItemDesc, Collisions, Effects). Select it by resourceIndex, or by typeName to take the first resource of that type. For a FrameResource the archive's own FrameNameTable is paired automatically. offset/limit page the decoded array where there is one.")]
    public static string DecodeResource(
        ArchiveService archives,
        [Description("Full path to the .sds file.")] string sdsPath,
        [Description("Zero-based index of the resource. Omit to select by typeName instead.")] int resourceIndex = -1,
        [Description("Resource type to take the first of (Actors, FrameResource, ItemDesc, Collisions, Effects). Ignored when resourceIndex is given.")] string? typeName = null,
        [Description("Index of the first element to return in the decoded array. Default 0.")] int offset = 0,
        [Description("How many elements to return from the decoded array. Default 100.")] int limit = 0)
    {
        try
        {
            CachedArchive cached = archives.Open(sdsPath);

            int index = resourceIndex;
            if (index < 0)
            {
                if (typeName is null)
                {
                    return ToolResult.Invalid("pass either resourceIndex or typeName");
                }

                index = IndexOfFirst(cached, typeName);
                if (index < 0)
                {
                    return ToolResult.Invalid(
                        $"'{cached.Path}' holds no '{typeName}' resource");
                }
            }

            if (!SdsTools.InRange(cached, index, out string complaint))
            {
                return ToolResult.Invalid(complaint);
            }

            string type = SdsTools.TypeNameOf(cached, index);
            byte[] payload = cached.Archive.Entries[index].Data ?? [];

            // An undecodable type is a failure of the whole call, not a failed field inside a
            // successful one. Burying it in `decoded.success` while the envelope still said true
            // meant a caller reading the top-level flag — the one every other tool here sets —
            // would take it for a decode that worked and find an object with nothing in it.
            if (!Decodable.Contains(type))
            {
                return ToolResult.Invalid(
                    $"no decoder for resource type '{type}' — decodable types are "
                    + string.Join(", ", Decodable)
                    + "; use extract_resource to take this one as bytes");
            }

            object decoded = type switch
            {
                "Actors" => Decoders.Actors(payload, offset, limit),
                "FrameResource" => Decoders.FrameResource(payload, PairedNameTable(cached), offset, limit),
                "ItemDesc" => Decoders.ItemDesc(payload),
                "Collisions" => Decoders.Collisions(payload, offset, limit),
                "Effects" => Decoders.Effects(payload),
                _ => throw new InvalidOperationException($"unreachable: '{type}' passed the decodable check"),
            };

            return ToolResult.Json(new
            {
                success = true,
                path = cached.Path,
                resourceIndex = index,
                typeName = type,
                name = cached.EntryNames[index],
                payloadBytes = payload.Length,
                decoded,
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    private static int IndexOfFirst(CachedArchive cached, string typeName)
    {
        for (int i = 0; i < cached.Archive.Entries.Count; i++)
        {
            if (SdsTools.TypeNameOf(cached, i).Equals(typeName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// The archive's FrameNameTable, when it has exactly one. A scene archive ships a single table
    /// beside its FrameResource, so pairing them is what the caller wanted anyway — but an archive
    /// with several gets none, because guessing which table belongs to which resource would resolve
    /// names against the wrong scene and look like it worked.
    /// </summary>
    private static byte[]? PairedNameTable(CachedArchive cached)
    {
        byte[]? found = null;
        for (int i = 0; i < cached.Archive.Entries.Count; i++)
        {
            if (!SdsTools.TypeNameOf(cached, i).Equals("FrameNameTable", StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }
            found = cached.Archive.Entries[i].Data;
        }
        return found;
    }
}
