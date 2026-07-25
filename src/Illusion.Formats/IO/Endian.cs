namespace Illusion.Formats.IO;

/// <summary>Byte order of a serialized value. PC archives are little-endian; the console platforms
/// (Xbox 360 / PS3) the SDS header can declare are big-endian.</summary>
public enum Endian
{
    Little,
    Big,
}
