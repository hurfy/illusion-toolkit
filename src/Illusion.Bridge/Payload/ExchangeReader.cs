using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Illusion.Bridge.Payload;

/// <summary>
/// Reads an .ilx file back into an <see cref="ExchangeContainer"/>. Tolerant by design: unknown
/// header keys are ignored, blocks with an unknown dtype are loaded as raw bytes (their consumers
/// decide), and objects of unknown kinds are preserved untouched. Only a bad magic or a newer major
/// version is an error.
/// </summary>
public static class ExchangeReader
{
    public static ExchangeContainer Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        uint magic = reader.ReadUInt32();
        if (magic != ExchangeSchema.Magic)
            throw new InvalidDataException($"Not an .ilx container (magic 0x{magic:X8}): {path}");
        int version = reader.ReadInt32();
        if (version > ExchangeSchema.Version)
            throw new InvalidDataException($"Container version {version} is newer than supported {ExchangeSchema.Version}: {path}");

        int headerLength = reader.ReadInt32();
        byte[] headerBytes = reader.ReadBytes(headerLength);
        JsonObject header = JsonNode.Parse(headerBytes)?.AsObject()
            ?? throw new InvalidDataException("Container header is not a JSON object: " + path);

        var container = new ExchangeContainer
        {
            Session = (string?)header["session"] ?? "",
            Producer = (string?)header["producer"] ?? "",
            Source = header["source"] is JsonNode src ? src.Deserialize<ExchangeSourceInfo>() : null,
        };

        if (header["objects"] is JsonArray objects)
        {
            foreach (JsonNode? node in objects)
            {
                ExchangeObject? obj = node?.Deserialize<ExchangeObject>();
                if (obj != null) container.Objects.Add(obj);
            }
        }

        // Offsets in the header are relative to the aligned end of the preamble+header; see ExchangeWriter.
        long dataStart = Align(12 + headerLength);
        if (header["blocks"] is JsonArray blocks)
        {
            foreach (JsonNode? node in blocks)
            {
                if (node is not JsonObject b) continue;
                string dtype = (string?)b["dtype"] ?? "";
                int components = (int?)b["components"] ?? 1;
                int count = (int?)b["count"] ?? 0;
                long offset = (long?)b["offset"] ?? 0;
                long byteLength = (long?)b["byteLength"] ?? 0;

                fs.Position = dataStart + offset;
                byte[] data = reader.ReadBytes(checked((int)byteLength));
                if (data.Length != byteLength)
                    throw new InvalidDataException($"Container block truncated at offset {offset}: {path}");
                container.Blocks.Add(new ExchangeBlock(dtype, components, count, data));
            }
        }

        return container;
    }

    private static long Align(long offset)
    {
        long rem = offset % ExchangeSchema.BlockAlignment;
        return rem == 0 ? offset : offset + (ExchangeSchema.BlockAlignment - rem);
    }
}
