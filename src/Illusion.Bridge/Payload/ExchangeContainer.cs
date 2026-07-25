using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Illusion.Bridge.Payload;

/// <summary>Origin of the data in a container (game install + source archive), informational.</summary>
public sealed class ExchangeSourceInfo
{
    [JsonPropertyName("game")] public string? Game { get; set; }
    [JsonPropertyName("gameRoot")] public string? GameRoot { get; set; }
    [JsonPropertyName("archive")] public string? Archive { get; set; }
}

/// <summary>One typed object of a container. <see cref="Meta"/> is kind-specific and carried as raw
/// JSON so unknown kinds survive a roundtrip untouched.</summary>
public sealed class ExchangeObject
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = ExchangeSchema.KindMesh;
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("parentId")] public string? ParentId { get; set; }

    /// <summary>World matrix, 16 floats row-major in the toolkit's row-vector convention
    /// (translation in elements 12..14). Blender transposes it into its column-vector Matrix.</summary>
    [JsonPropertyName("world")] public float[]? World { get; set; }

    /// <summary>Local matrix in the same layout (informational for Blender; authoritative on push).</summary>
    [JsonPropertyName("local")] public float[]? Local { get; set; }

    [JsonPropertyName("meta")] public JsonObject? Meta { get; set; }

    /// <summary>Array name → index into the container's block list.</summary>
    [JsonPropertyName("arrays")] public Dictionary<string, int> Arrays { get; set; } = new();
}

/// <summary>One raw array block: dtype metadata + payload bytes (written 16-byte aligned).</summary>
public sealed class ExchangeBlock
{
    public ExchangeBlock(string dtype, int components, int count, byte[] data)
    {
        Dtype = dtype;
        Components = components;
        Count = count;
        Data = data;
    }

    public string Dtype { get; }
    public int Components { get; }
    public int Count { get; }
    public byte[] Data { get; }
}

/// <summary>
/// In-memory model of an .ilx file: a JSON header (session, producer, source, typed objects) plus
/// raw little-endian array blocks the objects reference by index.
/// </summary>
public sealed class ExchangeContainer
{
    public string Session { get; set; } = "";

    /// <summary>Who wrote the file: "toolkit" or "blender-addon".</summary>
    public string Producer { get; set; } = "toolkit";

    public ExchangeSourceInfo? Source { get; set; }

    public List<ExchangeObject> Objects { get; } = new();

    public List<ExchangeBlock> Blocks { get; } = new();

    /// <summary>Adds a block and returns its index for an object's <see cref="ExchangeObject.Arrays"/> map.</summary>
    public int AddBlock(string dtype, int components, int count, byte[] data)
    {
        Blocks.Add(new ExchangeBlock(dtype, components, count, data));
        return Blocks.Count - 1;
    }
}
