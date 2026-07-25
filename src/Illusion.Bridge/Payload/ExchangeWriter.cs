using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Illusion.Bridge.Payload;

/// <summary>
/// Writes an .ilx file: <c>ILEX magic · version u32 · header-JSON length u32 · header-JSON UTF-8 ·
/// aligned raw blocks</c>. The file appears atomically (written to a sibling .tmp, then renamed), so
/// a concurrently-reading peer never sees a half-written container.
/// </summary>
public static class ExchangeWriter
{
    public static void Write(string path, ExchangeContainer container)
    {
        string tmp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
        {
            byte[] header = BuildHeader(container, out long[] blockOffsets);
            writer.Write(ExchangeSchema.Magic);
            writer.Write(ExchangeSchema.Version);
            writer.Write(header.Length);
            writer.Write(header);

            for (int i = 0; i < container.Blocks.Count; i++)
            {
                PadTo(writer, blockOffsets[i]);
                writer.Write(container.Blocks[i].Data);
            }
        }

        File.Move(tmp, path, overwrite: true);
    }

    // The header must carry final absolute block offsets, which depend on the header's own length —
    // resolved by measuring a draft header with zeroed offsets (same array shape → same JSON length,
    // because offsets are written as fixed-width strings via long values... not true for JSON).
    // Instead of fixed-point iteration, offsets are computed relative to the DATA SECTION start and
    // the section start itself is implied by the header end, aligned. Readers do the same math.
    private static byte[] BuildHeader(ExchangeContainer container, out long[] blockOffsets)
    {
        blockOffsets = new long[container.Blocks.Count];
        long relative = 0;
        var blocks = new JsonArray();
        for (int i = 0; i < container.Blocks.Count; i++)
        {
            ExchangeBlock b = container.Blocks[i];
            relative = Align(relative);
            blockOffsets[i] = relative;
            blocks.Add(new JsonObject
            {
                ["dtype"] = b.Dtype,
                ["components"] = b.Components,
                ["count"] = b.Count,
                ["offset"] = relative,
                ["byteLength"] = b.Data.LongLength,
            });
            relative += b.Data.LongLength;
        }

        var root = new JsonObject
        {
            ["format"] = ExchangeSchema.FormatName,
            ["version"] = ExchangeSchema.Version,
            ["session"] = container.Session,
            ["producer"] = container.Producer,
            ["source"] = container.Source == null ? null : JsonSerializer.SerializeToNode(container.Source),
            ["objects"] = JsonSerializer.SerializeToNode(container.Objects),
            ["blocks"] = blocks,
        };

        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString());

        // Block offsets are relative to the aligned end of the header; convert to absolute now that
        // the header length is known (preamble = magic + version + length field).
        long dataStart = Align(12 + json.Length);
        for (int i = 0; i < blockOffsets.Length; i++) blockOffsets[i] += dataStart;
        return json;
    }

    private static long Align(long offset)
    {
        long rem = offset % ExchangeSchema.BlockAlignment;
        return rem == 0 ? offset : offset + (ExchangeSchema.BlockAlignment - rem);
    }

    private static void PadTo(BinaryWriter writer, long absoluteOffset)
    {
        long pad = absoluteOffset - writer.BaseStream.Position;
        for (long i = 0; i < pad; i++) writer.Write((byte)0);
    }
}
