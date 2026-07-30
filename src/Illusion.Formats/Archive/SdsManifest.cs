using System.Xml.XPath;
using Illusion.Formats.Archive.Handlers;

namespace Illusion.Formats.Archive;

/// <summary>
/// Read-only view of an extracted folder's SDSContent.xml: the (type, file) pairs the scene loaders and
/// the save path look up. Single-payload entries only — container types (Script/Table) list their pieces
/// under their own elements and are the packing side's concern.
/// </summary>
public sealed class SdsManifest
{
    private readonly List<(string Type, string File)> _entries;

    private SdsManifest(string folder, List<(string, string)> entries)
    {
        Folder = folder;
        _entries = entries;
    }

    public string Folder { get; }

    public static SdsManifest Load(string folder)
    {
        string path = Path.Combine(folder, "SDSContent.xml");
        if (!File.Exists(path))
        {
            throw new SdsFormatException($"SDSContent.xml not found in '{folder}'");
        }

        var entries = new List<(string, string)>();
        using (FileStream stream = File.OpenRead(path))
        {
            var doc = new XPathDocument(stream);
            XPathNodeIterator nodes = doc.CreateNavigator().Select("/SDSResource/ResourceEntry");
            while (nodes.MoveNext())
            {
                XPathNavigator entry = nodes.Current!;
                if (!entry.MoveToFirstChild())
                {
                    continue;
                }
                string type = entry.Value;
                if (entry.MoveToNext() && entry.Name == "File")
                {
                    entries.Add((type, entry.Value));
                }
            }
        }
        return new SdsManifest(folder, entries);
    }

    public bool HasType(string typeName)
    {
        foreach ((string type, _) in _entries)
        {
            if (string.Equals(type, typeName, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Full paths of every single-file entry of the given type, in manifest order.</summary>
    public IReadOnlyList<string> GetFiles(string typeName)
    {
        var files = new List<string>();
        foreach ((string type, string file) in _entries)
        {
            if (string.Equals(type, typeName, StringComparison.Ordinal))
            {
                files.Add(ResourcePaths.Join(Folder, file));
            }
        }
        return files;
    }

    /// <summary>Whether the manifest already lists this file name (any type).</summary>
    public bool HasFile(string fileName)
    {
        foreach ((_, string file) in _entries)
        {
            if (string.Equals(file, fileName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Appends a single-payload entry to the folder's SDSContent.xml and rewrites it.
    ///
    /// Packing builds an archive from the MANIFEST, never from the folder — a file added on disk and left out
    /// of here is silently dropped, and an archive that then names a resource nothing carries does not load.
    /// So a file the toolkit invents (a fresh buffer pool, say) has to be announced here or not written at all.
    /// A name already listed is left alone, which makes this safe to call after every save.
    /// </summary>
    /// <returns>True when the manifest gained an entry.</returns>
    public bool AddEntry(string typeName, string fileName, int version)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        if (HasFile(fileName)) return false;

        string path = Path.Combine(Folder, "SDSContent.xml");
        var document = new System.Xml.XmlDocument { PreserveWhitespace = true };
        document.Load(path);
        System.Xml.XmlNode root = document.DocumentElement
            ?? throw new SdsFormatException($"SDSContent.xml in '{Folder}' has no root element");

        System.Xml.XmlElement entry = document.CreateElement("ResourceEntry");
        foreach ((string name, string value) in new[]
                 { ("Type", typeName), ("File", fileName), ("Version", version.ToString(System.Globalization.CultureInfo.InvariantCulture)) })
        {
            System.Xml.XmlElement child = document.CreateElement(name);
            child.InnerText = value;
            entry.AppendChild(child);
        }
        root.AppendChild(entry);

        // Through a temp file: a half-written manifest is an archive that can never be packed OR re-extracted,
        // and it is the one file in the folder nothing else can reconstruct.
        string temp = path + ".tmp";
        document.Save(temp);
        File.Move(temp, path, overwrite: true);

        _entries.Add((typeName, fileName));
        return true;
    }
}
