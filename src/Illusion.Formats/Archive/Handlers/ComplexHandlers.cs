using System.Xml;
using System.Xml.XPath;
using Illusion.Formats.Hashing;
using Illusion.Formats.IO;
using Illusion.Formats.ResourceFormats;

namespace Illusion.Formats.Archive.Handlers;

/// <summary>
/// Script: a package of Lua files. Extraction writes each script to its own path and lists them in the
/// manifest; these handlers close their manifest element themselves (returning null), because their
/// layout diverges from the standard File/Version pair.
/// </summary>
internal sealed class ScriptHandler : IResourceHandler
{
    public string? Extract(ExtractContext ctx, ResourceEntry entry, string name)
    {
        var resource = new ScriptResource();
        resource.Deserialize(entry.Version, new MemoryStream(entry.Data!), ctx.Endian);
        ctx.Manifest.WriteElementString("File", resource.Path);
        ctx.Manifest.WriteElementString("ScriptNum", XmlConvert.ToString(resource.Scripts.Count));

        foreach (ScriptData script in resource.Scripts)
        {
            string? scriptDirectory = Path.GetDirectoryName(script.Name);
            string scriptName = Path.GetFileName(script.Name);
            string directory = ctx.Folder + scriptDirectory;
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, scriptName), script.Data);
            ctx.Manifest.WriteElementString("Name", script.Name);
        }

        ctx.Manifest.WriteElementString("Version", XmlConvert.ToString(entry.Version));
        ctx.Manifest.WriteEndElement();
        return null;
    }

    public ResourceEntry Pack(PackContext ctx, XPathNavigator nav, XmlNode descNode)
    {
        var entry = new ResourceEntry();
        nav.MoveToNext();
        string path = nav.Value;
        nav.MoveToNext();
        int numScripts = XmlConvert.ToInt32(nav.Value);

        var resource = new ScriptResource { Path = path };
        for (int i = 0; i < numScripts; i++)
        {
            var data = new ScriptData();
            nav.MoveToNext();
            data.Name = nav.Value;
            data.Data = File.ReadAllBytes(ctx.Folder + data.Name);
            resource.Scripts.Add(data);
        }

        nav.MoveToNext();
        ushort version = XmlConvert.ToUInt16(nav.Value);

        using (var stream = new MemoryStream())
        {
            resource.Serialize(version, stream, Endian.Little);
            entry.Data = stream.ToArray();
            entry.SlotRamRequired = resource.GetRawBytes();
        }
        entry.Version = version;
        descNode.InnerText = "not available";
        return entry;
    }
}

/// <summary>XML: the payload is a compiled XML resource; extraction decompiles it to a .xml file named
/// by the resource's own path (falling back to raw bytes when decompilation failed originally).</summary>
internal sealed class XmlHandler : IResourceHandler
{
    public string? Extract(ExtractContext ctx, ResourceEntry entry, string name)
    {
        var resource = new XmlResource();
        using (var stream = new MemoryStream(entry.Data!))
        {
            resource.Deserialize(entry.Version, stream, ctx.Endian);
            name = resource.Name;

            string[] dirs = name.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            string dir = ctx.Folder;
            for (int i = 0; i < dirs.Length - 1; i++)
            {
                dir = Path.Combine(dir, dirs[i]);
                Directory.CreateDirectory(dir);
            }

            string fileName = Path.Combine(dir, Path.GetFileName(name) + ".xml");
            if (resource.bFailedToDecompile)
            {
                byte[] data = stream.ReadBytes((int)(stream.Length - stream.Position));
                File.WriteAllBytes(fileName, data);
            }
            else
            {
                using var writer = new StreamWriter(File.Open(fileName, FileMode.Create));
                writer.WriteLine(resource.Content);
            }
        }

        ctx.Manifest.WriteElementString("File", name);
        ctx.Manifest.WriteElementString("XMLTag", resource.Tag);
        ctx.Manifest.WriteElementString("Unk1", XmlConvert.ToString(Convert.ToByte(resource.Unk1)));
        ctx.Manifest.WriteElementString("Unk3", XmlConvert.ToString(Convert.ToByte(resource.Unk3)));
        ctx.Manifest.WriteElementString("FailedToDecompile", XmlConvert.ToString(Convert.ToByte(resource.bFailedToDecompile)));
        ctx.Manifest.WriteElementString("Version", XmlConvert.ToString(entry.Version));
        ctx.Manifest.WriteEndElement();
        return null;
    }

    public ResourceEntry Pack(PackContext ctx, XPathNavigator nav, XmlNode descNode)
    {
        var entry = new ResourceEntry();
        nav.MoveToNext();
        string file = nav.Value;
        descNode.InnerText = file;

        nav.MoveToNext();
        string tag = nav.Value;
        nav.MoveToNext();
        bool unk1 = nav.ValueAsBoolean;
        nav.MoveToNext();
        bool unk3 = nav.ValueAsBoolean;
        nav.MoveToNext();
        bool failedToDecompile = nav.ValueAsBoolean;
        nav.MoveToNext();
        entry.Version = XmlConvert.ToUInt16(nav.Value);

        var resource = new XmlResource
        {
            Name = file,
            Content = ctx.Folder + "/" + file + ".xml",
            Tag = tag,
            Unk1 = unk1,
            Unk3 = unk3,
            bFailedToDecompile = failedToDecompile,
        };

        using var stream = new MemoryStream();
        resource.Serialize(entry.Version, stream, Endian.Little);
        entry.Data = stream.ToArray();
        return entry;
    }
}

/// <summary>Table: a package of data tables; each extracts under its own name (with the entry version
/// prefixed into the file) into the folder's tables\ subtree.</summary>
internal sealed class TableHandler : IResourceHandler
{
    public string? Extract(ExtractContext ctx, ResourceEntry entry, string name)
    {
        var resource = new TableResource();
        using (var stream = new MemoryStream(entry.Data!))
        {
            resource.Deserialize(entry.Version, stream, ctx.Endian);
        }

        Directory.CreateDirectory(Path.Combine(ctx.Folder, "tables"));
        ctx.Manifest.WriteElementString("NumTables", XmlConvert.ToString(resource.Tables.Count));

        foreach (TableData data in resource.Tables)
        {
            using (var stream = new MemoryStream())
            {
                stream.WriteValueU32(entry.Version, Endian.Little);
                stream.WriteBytes(TableResource.EncodeSingleTable(entry.Version, data));
                File.WriteAllBytes(ctx.Folder + data.Name, stream.ToArray());
            }
            ctx.Manifest.WriteElementString("Table", data.Name);
        }

        ctx.Manifest.WriteElementString("Version", XmlConvert.ToString(entry.Version));
        ctx.Manifest.WriteEndElement();
        return null;
    }

    public ResourceEntry Pack(PackContext ctx, XPathNavigator nav, XmlNode descNode)
    {
        var entry = new ResourceEntry();
        var resource = new TableResource();

        nav.MoveToNext();
        int count = nav.ValueAsInt;

        string[] fileNames = new string[count];
        for (int i = 0; i < count; i++)
        {
            nav.MoveToNext();
            fileNames[i] = nav.Value;
        }

        nav.MoveToNext();
        entry.Version = XmlConvert.ToUInt16(nav.Value);

        foreach (string file in fileNames)
        {
            byte[] bytes = File.ReadAllBytes(ctx.Folder + file);
            if (bytes.Length < 4)
            {
                throw new ResourcePackException($"table '{file}' is too short for its version dword");
            }
            int version = BitConverter.ToInt32(bytes, 0);
            if (version != entry.Version)
            {
                throw new ResourcePackException(
                    $"table '{file}' declares version {version} but the manifest entry is {entry.Version}");
            }
            TableData data = TableResource.DecodeSingleTable(entry.Version, bytes[4..]);
            data.Name = file;
            data.NameHash = Fnv64.Hash(data.Name);
            resource.Tables.Add(data);
        }

        using (var stream = new MemoryStream())
        {
            resource.Serialize(entry.Version, stream, Endian.Little);
            entry.Data = stream.ToArray();
            entry.SlotRamRequired = (uint)entry.Data.Length + 128;
        }
        return entry;
    }
}
