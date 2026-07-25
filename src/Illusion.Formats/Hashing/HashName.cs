using Illusion.Formats.Geometry;

namespace Illusion.Formats.Hashing;

/// <summary>
/// A name paired with its FNV64 hash — the identity Mafia II's formats use for frames, materials,
/// textures and buffers. On disk it is the hash + a length-prefixed string; setting the string
/// re-derives the hash. A hash with no name prints as its skeleton-bone label when it matches one.
/// </summary>
public class HashName
{
    ulong hash;
    string name = null!;

    public ulong Hash
    {
        get { return hash; }
        set { hash = value; }
    }
    public string String
    {
        get { return name; }
        set { Set(value); }
    }

    public string Hex
    {
        get { return string.Format("{0:X}", hash); }
    }

    public HashName()
    {
        name = "";
        hash = 0;
    }
    public HashName(string name)
    {
        Set(name);
    }
    public HashName(HashName other)
    {
        this.hash = other.hash;
        this.name = other.name;
    }
    public string ConstructGUID()
    {
        byte[] GuidBytes = BitConverter.GetBytes(hash);
        uint GuidLeft = BitConverter.ToUInt32(GuidBytes, 0);
        uint GuidRight = BitConverter.ToUInt32(GuidBytes, 4);
        return string.Format("[{0}, {1}]", GuidLeft, GuidRight);
    }

    public void Set(string value)
    {
        name = value;

        // Cannot check string.IsNullOrWhitespace
        if (name != "")
        {
            hash = Fnv64.Hash(name);
        }
    }

    public int CalculateSize()
    {
        return 10 + name.Length;
    }

    public override string ToString()
    {
        if (string.IsNullOrEmpty(name))
        {
            return ((SkeletonBoneIDs)hash).ToString();
        }

        return name;
    }
}
