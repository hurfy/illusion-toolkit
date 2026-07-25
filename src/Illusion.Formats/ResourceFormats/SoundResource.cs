using Illusion.Formats.IO;

//THIS ISN'T GIBBED. BUT STILL USES GIBBED STUFF :)
namespace Illusion.Formats.ResourceFormats;

internal class SoundResource : IResourceFormat
{
    private byte[] _data = null!;

    public string Name = null!;
    public int FileSize;
    public byte[] Data
    {
        get { return _data; }
        set
        {
            _data = value;
            FileSize = value.Length;
        }
    }

    // The envelope codec runs in the native core.

    public void Deserialize(ushort version, Stream input, Endian endian) =>
        Native.Resources.NativeResources.SoundUnwrap(
            this, version, input.ReadBytes((int)(input.Length - input.Position)));

    public void Serialize(ushort version, Stream input, Endian endian) =>
        input.WriteBytes(Native.Resources.NativeResources.SoundWrap(this, version));
}
