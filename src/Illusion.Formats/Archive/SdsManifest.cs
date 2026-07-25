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
}
