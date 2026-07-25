using System.Xml;
using System.Xml.XPath;
using Illusion.Formats.IO;

namespace Illusion.Formats.Archive.Handlers;

/// <summary>Path helpers for manifest-supplied names.</summary>
internal static class ResourcePaths
{
    /// <summary>Joins a folder and a game-style resource name. Names out of the archive's ResourceInfo
    /// XML are often rooted ("/missions/…"); Path.Combine would treat those as absolute, so leading
    /// separators are stripped first (the historical extractor concatenated strings and was immune).</summary>
    public static string Join(string folder, string name) =>
        Path.Combine(folder, name.TrimStart('/', '\\'));
}

/// <summary>State an extraction pass hands each handler.</summary>
internal sealed class ExtractContext
{
    public required string Folder { get; init; }
    public required XmlWriter Manifest { get; init; }
    public required Endian Endian { get; init; }
    /// <summary>Index of the entry in the archive — part of the deterministic file names.</summary>
    public required int EntryIndex { get; init; }
    public required string TypeName { get; init; }
}

/// <summary>State a packing pass hands each handler.</summary>
internal sealed class PackContext
{
    public required string Folder { get; init; }
    public required Endian Endian { get; init; }
}

/// <summary>
/// Per-resource-type extraction/packing strategy. One handler per SDS resource-type name; the registry
/// replaces the switch(typeName) dispatch the vendored ArchiveFile duplicated across read and write.
/// </summary>
internal interface IResourceHandler
{
    /// <summary>
    /// Writes this entry's manifest fields (the manifest is positioned inside its &lt;ResourceEntry&gt;
    /// element, &lt;Type&gt; already written) and prepares <paramref name="entry"/>.Data for saving.
    /// Returns the file name the caller writes Data to (after appending &lt;Version&gt;), or null when
    /// the handler wrote its own file(s) and closed the manifest element itself.
    /// </summary>
    string? Extract(ExtractContext ctx, ResourceEntry entry, string name);

    /// <summary>
    /// Builds the packed entry from its manifest node. <paramref name="nav"/> is positioned on the
    /// &lt;Type&gt; child; implementations advance it through their fields exactly as extraction wrote
    /// them. <paramref name="descNode"/> receives the SourceDataDescription text.
    /// </summary>
    ResourceEntry Pack(PackContext ctx, XPathNavigator nav, XmlNode descNode);
}

/// <summary>The resource-type name → handler map for version-19 (Mafia II / Mafia II DE) archives.</summary>
internal static class ResourceHandlerRegistry
{
    private static readonly Dictionary<string, IResourceHandler> Handlers = new()
    {
        // Raw payloads with deterministic extraction names.
        ["IndexBufferPool"] = new BufferPoolHandler(".ibp"),
        ["VertexBufferPool"] = new BufferPoolHandler(".vbp"),
        // The vendor's extraction name for AnimalTrafficPaths historically lacks the underscore.
        ["AnimalTrafficPaths"] = new BasicHandler(".atp", underscoreBeforeIndex: false),
        ["FrameResource"] = new BasicHandler(".fr"),
        ["Effects"] = new BasicHandler(".eff"),
        ["FrameNameTable"] = new BasicHandler(".fnt"),
        ["EntityDataStorage"] = new EntityDataHandler(".eds"),
        ["PREFAB"] = new BasicHandler(".prf"),
        ["ItemDesc"] = new BasicHandler(".ids"),
        ["Actors"] = new BasicHandler(".act"),
        ["Collisions"] = new CollisionHandler(".col"),
        ["SoundTable"] = new BasicHandler(".stbl"),
        ["Speech"] = new BasicHandler(".spe"),
        ["FxAnimSet"] = new BasicHandler(".fas"),
        ["FxActor"] = new BasicHandler(".fxa"),
        ["Cutscene"] = new BasicHandler(".cut"),
        ["Translokator"] = new BasicHandler(".tra"),
        ["NAV_AIWORLD_DATA"] = new BasicHandler(".nav"),
        ["NAV_OBJ_DATA"] = new BasicHandler(".nov"),
        ["NAV_HPD_DATA"] = new BasicHandler(".nhv"),
        ["Animation2"] = new Animation2Handler(),
        ["Animated Texture"] = new AnimatedTextureHandler(),
        ["AudioSectors"] = new AudioSectorsHandler(),
        // Payloads with a serialized wrapper that extraction strips.
        ["Texture"] = new TextureHandler(),
        ["Mipmap"] = new MipmapHandler(),
        ["Sound"] = new SoundHandler(),
        ["MemFile"] = new MemFileHandler(),
        // Container payloads that extract to multiple files.
        ["Script"] = new ScriptHandler(),
        ["XML"] = new XmlHandler(),
        ["Table"] = new TableHandler(),
    };

    /// <summary>Default extraction file extension per type (used for the deterministic name stubs).</summary>
    public static readonly IReadOnlyDictionary<string, string> FileExtensions = new Dictionary<string, string>
    {
        ["Texture"] = ".dds",
        ["Mipmap"] = ".dds",
        ["IndexBufferPool"] = ".ibp",
        ["VertexBufferPool"] = ".vbp",
        ["AnimalTrafficPaths"] = ".atp",
        ["FrameResource"] = ".fr",
        ["Effects"] = ".eff",
        ["FrameNameTable"] = ".fnt",
        ["EntityDataStorage"] = ".eds",
        ["PREFAB"] = ".prf",
        ["ItemDesc"] = ".ids",
        ["Actors"] = ".act",
        ["Collisions"] = ".col",
        ["SoundTable"] = ".stbl",
        ["Speech"] = ".spe",
        ["FxAnimSet"] = ".fas",
        ["FxActor"] = ".fxa",
        ["Cutscene"] = ".cut",
        ["Translokator"] = ".tra",
        ["Animation2"] = ".an2",
        ["NAV_AIWORLD_DATA"] = ".nav",
        ["NAV_OBJ_DATA"] = ".nov",
        ["NAV_HPD_DATA"] = ".nhv",
        ["AudioSectors"] = ".auds",
        ["Script"] = ".luapack",
        ["Table"] = ".tblpack",
        ["Sound"] = ".fsb",
        ["MemFile"] = ".txt",
        ["XML"] = ".xml",
        ["Animated Texture"] = ".ifl",
    };

    public static IResourceHandler Get(string typeName) =>
        Handlers.TryGetValue(typeName, out IResourceHandler? handler)
            ? handler
            : throw new SdsFormatException($"unknown resource type '{typeName}'");

    public static bool Contains(string typeName) => Handlers.ContainsKey(typeName);
}
