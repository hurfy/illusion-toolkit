using Illusion.Formats.Archive;

namespace Illusion.Assets.Sds;

/// <summary>
/// What the toolkit is willing to put INTO an archive, and how the manifest has to say it.
///
/// <para>
/// The list is deliberately short. An archive is packed FROM ITS MANIFEST, and a packing handler reads an
/// entry's fields positionally — so announcing a resource wrongly does not fail loudly, it writes a
/// structurally different payload that packs and ships broken. A type earns a place here only when a fresh
/// entry for it can be written with nothing guessed:
/// </para>
/// <list type="bullet">
/// <item>its payload is a plain file the handler copies or wraps, with no field baked into the bytes that the
/// packer reads back out (which rules out the buffer pools — <c>BufferPoolHandler</c> takes the VRAM figure
/// from offset 5 of the payload itself);</item>
/// <item>nothing else in the archive addresses it structurally, so adding or dropping one cannot leave a
/// dangling reference (which rules out the frame resource, its name table, collisions, item descriptions,
/// actors, prefabs and the rest of the entity layer — those are edited by the editor that owns them);</item>
/// <item>and every extra manifest field it carries can be DERIVED. Only <c>XML</c> fails this last one: its
/// entry names a system tag (<c>RadioConfig</c>, <c>AI_BRAIN_CFG</c>) that the .xml file does not contain,
/// so a brand-new XML resource cannot be announced — though replacing the payload of one already in the
/// archive is fine, and is what editing a weather preset outside the toolkit actually needs.</item>
/// </list>
/// <para>
/// The version numbers are measured, not guessed: every shipped entry of a given type in Mafia II carries one
/// and the same <c>Version</c> (surveyed across 400 manifests of the retail install).
/// </para>
/// </summary>
public static class SdsImportTypes
{
    /// <summary>One importable type: what the manifest calls it, what the browser files it under, and the
    /// manifest version its entries carry.</summary>
    public sealed record Entry(string Type, SdsResourceKind Kind, int Version, string Label);

    private static readonly Entry Texture = new("Texture", SdsResourceKind.Texture, 2, "Texture");
    private static readonly Entry Mipmap = new("Mipmap", SdsResourceKind.Mipmap, 2, "Texture MIP chain");
    private static readonly Entry Sound = new("Sound", SdsResourceKind.Sound, 5, "Sound bank");
    private static readonly Entry Speech = new("Speech", SdsResourceKind.Speech, 2, "Speech");
    private static readonly Entry Xml = new("XML", SdsResourceKind.Xml, 3, "XML");
    private static readonly Entry MemFile = new("MemFile", SdsResourceKind.Binary, 2, "Data file");

    /// <summary>
    /// The payload extension a type is recognised by. A dropped file is typed by its extension and by nothing
    /// else — the toolkit cannot tell a frame resource from a collision by looking at the bytes, and guessing
    /// would be the one mistake this whole table exists to prevent.
    /// </summary>
    private static readonly Dictionary<string, Entry> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".dds"] = Texture,     // ...or Mipmap, decided by the MIP_ prefix — see Classify
        [".fsb"] = Sound,
        [".spe"] = Speech,
        [".xml"] = Xml,
        [".bin"] = MemFile,
        [".txt"] = MemFile,
    };

    /// <summary>Every type that may be pasted from one archive into another. Pasting carries the source
    /// entry's own fields verbatim, so nothing has to be derived — XML rides along.</summary>
    private static readonly HashSet<string> Pasteable = new(StringComparer.Ordinal)
    {
        Texture.Type, Mipmap.Type, Sound.Type, Speech.Type, Xml.Type, MemFile.Type,
    };

    /// <summary>The filter for the import file dialog, in Win32's own format.</summary>
    public static string FileDialogFilter =>
        "Importable resources|*.dds;*.fsb;*.spe;*.xml;*.bin;*.txt"
        + "|Textures (*.dds)|*.dds"
        + "|Sound banks (*.fsb)|*.fsb"
        + "|Speech (*.spe)|*.spe"
        + "|XML (*.xml)|*.xml"
        + "|Data files (*.bin;*.txt)|*.bin;*.txt";

    /// <summary>What a file on disk would be brought in as, or null when the toolkit will not type it.</summary>
    public static Entry? Classify(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!ByExtension.TryGetValue(Path.GetExtension(path), out Entry? entry)) return null;
        // A MIP chain is a .dds like any other; only its name says which of the two it is, and the packer
        // strips exactly those four characters to hash the texture it belongs to.
        return entry == Texture && IsMipName(Path.GetFileName(path)) ? Mipmap : entry;
    }

    /// <summary>Why a file cannot be brought in — one sentence, for the notice the user actually reads.</summary>
    public static string RefusalFor(string path) =>
        $"{Path.GetFileName(path)} — the toolkit imports textures (.dds), sound banks (.fsb), speech (.spe), "
        + "XML and plain data (.bin, .txt). Everything else in an archive is written by the editor that owns "
        + "it, so that its references stay intact.";

    /// <summary>Whether a resource already in an archive may be copied into another one.</summary>
    public static bool CanPaste(string type) => Pasteable.Contains(type);

    /// <summary>Whether a fresh entry of this type can be announced at all, or only replaced in place.</summary>
    public static bool CanAddNew(Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Type != Xml.Type;
    }

    /// <summary>The manifest name a file lands under. Flat for everything the toolkit adds: a nested name is
    /// how the game groups its own sound banks and data files, and inventing a folder for an imported one
    /// would put it somewhere nothing looks. Pasting keeps the source name instead, nesting and all.</summary>
    public static string ManifestNameFor(Entry entry, string path)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(path);
        string name = Path.GetFileName(path);
        // The XML entry names the resource, and the payload beside it adds the suffix (see
        // ArchiveContents.PayloadPath) — so the manifest name is the file name without it.
        return entry.Type == Xml.Type ? Path.GetFileNameWithoutExtension(name) : name;
    }

    /// <summary>
    /// The elements that sit between <c>File</c> and <c>Version</c> for a fresh entry, in the order the
    /// packing handler reads them. Empty for the types that carry none.
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> ExtrasFor(
        Entry entry, string manifestName, SdsManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(manifest);

        if (entry.Type == Texture.Type)
        {
            // The flag says a separate Mipmap entry carries this texture's high-resolution tail. It is not a
            // preference: with it set and no MIP_ entry present the game streams a chain that is not there.
            bool hasMip = manifest.HasFile(MipNameFor(manifestName));
            return [("HasMIP", hasMip ? "1" : "0")];
        }
        if (entry.Type == MemFile.Type)
        {
            // Zero in every shipped archive, and not written to the payload at all below version 4 — but the
            // manifest still carries the field, and the handler counts elements rather than naming them.
            return [("Unk2_V4", "0")];
        }
        return [];
    }

    /// <summary>The Mipmap entry that belongs to a texture — the same name behind a <c>MIP_</c> prefix.</summary>
    public static string MipNameFor(string textureName)
    {
        ArgumentException.ThrowIfNullOrEmpty(textureName);
        return "MIP_" + textureName;
    }

    /// <summary>Whether a manifest name is a texture's MIP chain rather than a texture.</summary>
    public static bool IsMipName(string name) =>
        name is not null && name.StartsWith("MIP_", StringComparison.Ordinal);

    /// <summary>The texture a MIP chain belongs to, given the chain's own name.</summary>
    public static string TextureNameFor(string mipName)
    {
        ArgumentException.ThrowIfNullOrEmpty(mipName);
        return IsMipName(mipName) ? mipName[4..] : mipName;
    }
}
