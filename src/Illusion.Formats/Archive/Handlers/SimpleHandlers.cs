using System.Xml;
using System.Xml.XPath;

namespace Illusion.Formats.Archive.Handlers;

/// <summary>
/// Raw-payload resource: extraction dumps the entry bytes under a deterministic
/// "{TypeName}_{index}{ext}" name; packing reads them back with SlotRam = payload size.
/// </summary>
internal class BasicHandler : IResourceHandler
{
    private readonly string _extension;
    private readonly bool _underscore;

    public BasicHandler(string extension, bool underscoreBeforeIndex = true)
    {
        _extension = extension;
        _underscore = underscoreBeforeIndex;
    }

    public virtual string? Extract(ExtractContext ctx, ResourceEntry entry, string name)
    {
        string fileName = _underscore
            ? $"{ctx.TypeName}_{ctx.EntryIndex}{_extension}"
            : $"{ctx.TypeName}{ctx.EntryIndex}{_extension}";
        ctx.Manifest.WriteElementString("File", fileName);
        return fileName;
    }

    public virtual ResourceEntry Pack(PackContext ctx, XPathNavigator nav, XmlNode descNode)
    {
        var entry = new ResourceEntry();
        nav.MoveToNext();
        string file = nav.Value;
        nav.MoveToNext();
        entry.Version = XmlConvert.ToUInt16(nav.Value);

        entry.Data = File.ReadAllBytes(ResourcePaths.Join(ctx.Folder, file));
        entry.SlotRamRequired = (uint)entry.Data.Length;
        descNode.InnerText = file;
        return entry;
    }
}

/// <summary>Collision resource: like Basic but SlotRam is payload+1 and the source description is
/// withheld (matching the game's own archives).</summary>
internal sealed class CollisionHandler : BasicHandler
{
    public CollisionHandler(string extension) : base(extension) { }

    public override ResourceEntry Pack(PackContext ctx, XPathNavigator nav, XmlNode descNode)
    {
        var entry = new ResourceEntry();
        nav.MoveToNext();
        string file = nav.Value;
        nav.MoveToNext();
        entry.Version = XmlConvert.ToUInt16(nav.Value);

        entry.Data = File.ReadAllBytes(ResourcePaths.Join(ctx.Folder, file));
        entry.SlotRamRequired = (uint)entry.Data.Length + 1;
        descNode.InnerText = "not available";
        return entry;
    }
}

/// <summary>Index/vertex buffer pools: VRAM requirement is the pool size stored at payload offset 5.</summary>
internal sealed class BufferPoolHandler : BasicHandler
{
    public BufferPoolHandler(string extension) : base(extension) { }

    public override ResourceEntry Pack(PackContext ctx, XPathNavigator nav, XmlNode descNode)
    {
        var entry = new ResourceEntry();
        nav.MoveToNext();
        string file = nav.Value;
        nav.MoveToNext();
        entry.Version = XmlConvert.ToUInt16(nav.Value);

        entry.Data = File.ReadAllBytes(ResourcePaths.Join(ctx.Folder, file));
        entry.SlotVramRequired = BitConverter.ToUInt32(entry.Data, 5);
        descNode.InnerText = "not available";
        return entry;
    }
}

/// <summary>EntityDataStorage: Basic payload whose source description is withheld.</summary>
internal sealed class EntityDataHandler : BasicHandler
{
    public EntityDataHandler(string extension) : base(extension) { }

    public override ResourceEntry Pack(PackContext ctx, XPathNavigator nav, XmlNode descNode)
    {
        ResourceEntry entry = base.Pack(ctx, nav, descNode);
        descNode.InnerText = "not available";
        return entry;
    }
}

/// <summary>Animation2: extraction names the file after the resource (its resolved item name + .an2).</summary>
internal sealed class Animation2Handler : IResourceHandler
{
    public string? Extract(ExtractContext ctx, ResourceEntry entry, string name)
    {
        string fileName = name + ".an2";
        ctx.Manifest.WriteElementString("File", fileName);
        return fileName;
    }

    public ResourceEntry Pack(PackContext ctx, XPathNavigator nav, XmlNode descNode)
    {
        var entry = new ResourceEntry();
        nav.MoveToNext();
        string file = nav.Value;
        nav.MoveToNext();
        entry.Version = XmlConvert.ToUInt16(nav.Value);

        entry.Data = File.ReadAllBytes(ResourcePaths.Join(ctx.Folder, file));
        descNode.InnerText = file.Remove(file.Length - 4, 4);
        return entry;
    }
}

/// <summary>Animated Texture (.ifl): raw payload under its resolved item name; no RAM accounting.</summary>
internal sealed class AnimatedTextureHandler : IResourceHandler
{
    public string? Extract(ExtractContext ctx, ResourceEntry entry, string name)
    {
        ctx.Manifest.WriteElementString("File", name);
        return name;
    }

    public ResourceEntry Pack(PackContext ctx, XPathNavigator nav, XmlNode descNode)
    {
        var entry = new ResourceEntry();
        nav.MoveToNext();
        string file = nav.Value;
        nav.MoveToNext();
        entry.Version = XmlConvert.ToUInt16(nav.Value);

        entry.Data = File.ReadAllBytes(ResourcePaths.Join(ctx.Folder, file));
        descNode.InnerText = file;
        return entry;
    }
}

/// <summary>AudioSectors: the resolved item name is a nested path; extraction creates the directories
/// (the payload itself is written by the caller like any basic entry).</summary>
internal sealed class AudioSectorsHandler : IResourceHandler
{
    public string? Extract(ExtractContext ctx, ResourceEntry entry, string name)
    {
        string[] dirs = name.Split('/');
        string dir = ctx.Folder;
        for (int i = 0; i < dirs.Length - 1; i++)
        {
            dir = Path.Combine(dir, dirs[i]);
            Directory.CreateDirectory(dir);
        }
        ctx.Manifest.WriteElementString("File", name);
        return name;
    }

    public ResourceEntry Pack(PackContext ctx, XPathNavigator nav, XmlNode descNode)
    {
        var entry = new ResourceEntry();
        nav.MoveToNext();
        string file = nav.Value;
        entry.Data = File.ReadAllBytes(ResourcePaths.Join(ctx.Folder, file));
        nav.MoveToNext();
        entry.Version = XmlConvert.ToUInt16(nav.Value);
        descNode.InnerText = file;
        return entry;
    }
}
