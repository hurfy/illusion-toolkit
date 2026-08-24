using System.ComponentModel;
using Illusion.Formats.Archive;
using Illusion.Formats.ResourceFormats;
using Illusion.Formats.Textures;
using Illusion.Mcp.Services;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// Inspecting textures, on disk or inside an archive.
/// <para>
/// A Texture resource is not a bare .dds: the archive wraps it in a small record carrying the
/// texture's FNV64 name hash and a flag for whether its mip chain lives in a separate Mipmap
/// resource. These tools unwrap that first, so what they report about dimensions and format
/// describes the actual surface rather than the wrapper's first bytes.
/// </para>
/// Everything here reads headers only — no pixels are decoded and no GPU is involved, so it works
/// in a headless run and costs nothing on a 4K texture.
/// </summary>
[McpServerToolType]
public sealed class TextureTools
{
    [McpServerTool(Name = "inspect_dds_file")]
    [Description("Inspect a standalone .dds file: dimensions, format (DXT1/DXT5/BC7/…), mip count, and the cubemap/volume flags.")]
    public static string InspectDdsFile(
        [Description("Full path to the .dds file.")] string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return ToolResult.Invalid($"no such file: {filePath}");
            }

            byte[] bytes = File.ReadAllBytes(filePath);
            return ToolResult.Json(new
            {
                success = true,
                path = filePath,
                fileSize = bytes.LongLength,
                dds = Describe(DdsInfo.Read(bytes)),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "inspect_dds_bytes")]
    [Description("Inspect a DDS surface from base64 bytes — for analysing texture data that came out of extract_resource or somewhere else.")]
    public static string InspectDdsBytes(
        [Description("Base64 of a .dds file's bytes.")] string base64Data)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(base64Data);
            return ToolResult.Json(new
            {
                success = true,
                sourceBytes = bytes.LongLength,
                dds = Describe(DdsInfo.Read(bytes)),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "inspect_sds_texture")]
    [Description("Inspect one Texture resource inside an SDS archive: the wrapper's name hash and mip flag, plus the DDS metadata of the surface it holds.")]
    public static string InspectSdsTexture(
        ArchiveService archives,
        [Description("Full path to the .sds file.")] string sdsPath,
        [Description("Zero-based index of the Texture resource, as reported by list_resources.")] int resourceIndex)
    {
        try
        {
            CachedArchive cached = archives.Open(sdsPath);
            if (!SdsTools.InRange(cached, resourceIndex, out string complaint))
            {
                return ToolResult.Invalid(complaint);
            }

            string type = SdsTools.TypeNameOf(cached, resourceIndex);
            if (!type.Equals("Texture", StringComparison.Ordinal))
            {
                return ToolResult.Invalid(
                    $"resource {resourceIndex} is a '{type}', not a Texture");
            }

            ResourceEntry entry = cached.Archive.Entries[resourceIndex];
            TextureResource wrapper = Unwrap(entry, cached.Archive.Endian);

            return ToolResult.Json(new
            {
                success = true,
                path = cached.Path,
                resourceIndex,
                name = cached.EntryNames[resourceIndex],
                nameHash = wrapper.NameHash,
                // The engine splits big textures: the low mips ship here and the rest in a separate
                // Mipmap resource. A caller measuring texture memory has to know which it is looking at.
                hasSeparateMipChain = wrapper.HasMIP != 0,
                isDx10 = wrapper.bIsDX10,
                surfaceBytes = wrapper.Data?.LongLength ?? 0,
                dds = Describe(DdsInfo.Read(wrapper.Data ?? [])),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "list_sds_textures")]
    [Description("List the Texture resources in an SDS archive. With includeMetadata the DDS header of each is read too, giving dimensions and format per texture — the quickest way to audit an archive's texture budget. Paginated.")]
    public static string ListSdsTextures(
        ArchiveService archives,
        [Description("Full path to the .sds file.")] string sdsPath,
        [Description("Read each texture's DDS header as well. Default true.")] bool includeMetadata = true,
        [Description("Index of the first texture to return. Default 0.")] int offset = 0,
        [Description("How many textures to return. Default 100.")] int limit = 0)
    {
        try
        {
            CachedArchive cached = archives.Open(sdsPath);

            var indices = new List<int>();
            for (int i = 0; i < cached.Archive.Entries.Count; i++)
            {
                if (SdsTools.TypeNameOf(cached, i).Equals("Texture", StringComparison.Ordinal))
                {
                    indices.Add(i);
                }
            }

            (int start, int count) = Page.Clamp(offset, limit);
            List<int> window = Page.Slice(indices, start, count);

            var textures = new List<object>(window.Count);
            foreach (int index in window)
            {
                ResourceEntry entry = cached.Archive.Entries[index];
                // One malformed texture must not fail the listing — auditing an archive is exactly
                // when a caller wants to be told which entry is the broken one.
                try
                {
                    TextureResource wrapper = Unwrap(entry, cached.Archive.Endian);
                    textures.Add(new
                    {
                        index,
                        name = cached.EntryNames[index],
                        nameHash = wrapper.NameHash,
                        hasSeparateMipChain = wrapper.HasMIP != 0,
                        surfaceBytes = wrapper.Data?.LongLength ?? 0,
                        dds = includeMetadata ? Describe(DdsInfo.Read(wrapper.Data ?? [])) : null,
                    });
                }
                catch (Exception ex)
                {
                    textures.Add(new { index, name = cached.EntryNames[index], error = ex.Message });
                }
            }

            return ToolResult.Json(new
            {
                success = true,
                path = cached.Path,
                includeMetadata,
                total = indices.Count,
                offset = start,
                limit = count,
                returned = textures.Count,
                textures,
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    // ── shared ──

    private static TextureResource Unwrap(ResourceEntry entry, Illusion.Formats.IO.Endian endian)
    {
        var resource = new TextureResource();
        using var stream = new MemoryStream(entry.Data ?? [], writable: false);
        resource.Deserialize(entry.Version, stream, endian);
        return resource;
    }

    private static object Describe(DdsInfo info) => new
    {
        width = info.Width,
        height = info.Height,
        depth = info.Depth,
        // A header that declares no mip count means one level, which is worth stating rather than
        // reporting a bare 0 that reads like "none".
        mipCount = info.MipCount == 0 ? 1 : info.MipCount,
        mipCountDeclared = info.MipCount,
        format = info.Format,
        fourCC = info.FourCcText,
        dxgiFormat = info.DxgiFormat,
        compressed = info.IsCompressed,
        cubemap = info.IsCubemap,
        volume = info.IsVolume,
        rgbBitCount = info.RgbBitCount == 0 ? (uint?)null : info.RgbBitCount,
        headerFlags = info.HeaderFlags,
        caps = info.Caps,
        caps2 = info.Caps2,
        dataOffset = info.DataOffset,
        dataBytes = info.DataBytes,
    };
}
