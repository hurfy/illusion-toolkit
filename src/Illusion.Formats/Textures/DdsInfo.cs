using System.Globalization;

namespace Illusion.Formats.Textures;

/// <summary>
/// What a DDS file's header says about itself: dimensions, format, mip count, the capability flags.
/// <para>
/// Header only — nothing here touches pixel data, and nothing here needs a GPU. That is the point:
/// the toolkit's other DDS reader (<c>Illusion.Rendering.DdsTexture</c>) exists to hand a surface to
/// D3D11 and speaks in DXGI formats, so anything that merely wants to <i>describe</i> a texture
/// would have to drag the whole rendering stack in behind it. Describing textures is what the
/// browsing tools do, so the description lives here instead.
/// </para>
/// The two readers agree on the layout by construction: the offsets below are the same ones the
/// rendering loader indexes, spelled out as named constants rather than repeated as literals.
/// </summary>
public sealed class DdsInfo
{
    /// <summary>'DDS ' — the four bytes every DDS file opens with.</summary>
    public const uint Magic = 0x20534444;

    // Byte offsets into the file. The DDS_HEADER follows the 4-byte magic and is 124 bytes, so the
    // pixel data of a plain DDS starts at 128; a DX10 file carries 20 more bytes of header first.
    private const int HeaderSize = 128;
    private const int Dx10HeaderSize = 20;
    private const int OffsetFlags = 8;
    private const int OffsetHeight = 12;
    private const int OffsetWidth = 16;
    private const int OffsetPitchOrLinearSize = 20;
    private const int OffsetDepth = 24;
    private const int OffsetMipMapCount = 28;
    private const int OffsetPixelFormatFlags = 80;
    private const int OffsetFourCc = 84;
    private const int OffsetRgbBitCount = 88;
    private const int OffsetCaps = 108;
    private const int OffsetCaps2 = 112;
    private const int OffsetDxgiFormat = 128;

    private const uint DdpfFourCc = 0x4;
    private const uint FourCcDx10 = 0x30315844;

    /// <summary>Cubemap bit of dwCaps2.</summary>
    private const uint Caps2Cubemap = 0x200;

    /// <summary>Volume-texture bit of dwCaps2.</summary>
    private const uint Caps2Volume = 0x200000;

    public int Width { get; private init; }
    public int Height { get; private init; }
    public int Depth { get; private init; }

    /// <summary>Mip levels the header declares. 0 means the header did not say, which in practice
    /// means one level.</summary>
    public int MipCount { get; private init; }

    /// <summary>The pixel format's FourCC, or 0 for an uncompressed layout that declares none.</summary>
    public uint FourCc { get; private init; }

    /// <summary>The FourCC as its four characters ("DXT1", "DX10"), or null when there is none.</summary>
    public string? FourCcText { get; private init; }

    /// <summary>
    /// A readable format name: the FourCC for a compressed surface, the DXGI format for a DX10 one,
    /// and the bit depth for an uncompressed surface.
    /// </summary>
    public string Format { get; private init; } = "unknown";

    /// <summary>The DXGI format id from the DX10 extension header, or null when there is none.</summary>
    public uint? DxgiFormat { get; private init; }

    public bool IsCompressed { get; private init; }
    public bool IsCubemap { get; private init; }
    public bool IsVolume { get; private init; }

    /// <summary>Bits per pixel for an uncompressed surface; 0 when the surface is block-compressed.</summary>
    public uint RgbBitCount { get; private init; }

    public uint HeaderFlags { get; private init; }
    public uint Caps { get; private init; }
    public uint Caps2 { get; private init; }

    /// <summary>Byte offset where the mip-0 surface begins — 128, or 148 for a DX10 file.</summary>
    public int DataOffset { get; private init; }

    /// <summary>Bytes of surface data actually present after the header.</summary>
    public long DataBytes { get; private init; }

    /// <summary>
    /// Reads the header of <paramref name="dds"/>.
    /// </summary>
    /// <exception cref="FileFormatException">The buffer is too short to hold a DDS header, or does
    /// not start with the DDS magic.</exception>
    public static DdsInfo Read(ReadOnlySpan<byte> dds)
    {
        if (dds.Length < HeaderSize)
        {
            throw new FileFormatException(
                $"not a DDS file: {dds.Length} bytes is shorter than the {HeaderSize}-byte header");
        }

        uint magic = BitConverter.ToUInt32(dds[..4]);
        if (magic != Magic)
        {
            throw new FileFormatException("not a DDS file: the leading four bytes are not 'DDS '");
        }

        uint pixelFormatFlags = ReadU32(dds, OffsetPixelFormatFlags);
        uint fourCc = ReadU32(dds, OffsetFourCc);
        bool hasFourCc = (pixelFormatFlags & DdpfFourCc) != 0 && fourCc != 0;

        uint? dxgi = null;
        int dataOffset = HeaderSize;
        if (hasFourCc && fourCc == FourCcDx10)
        {
            if (dds.Length < HeaderSize + Dx10HeaderSize)
            {
                throw new FileFormatException(
                    "DDS declares the DX10 extension but the file is too short to hold its header");
            }
            dxgi = ReadU32(dds, OffsetDxgiFormat);
            dataOffset = HeaderSize + Dx10HeaderSize;
        }

        uint caps2 = ReadU32(dds, OffsetCaps2);
        uint bitCount = ReadU32(dds, OffsetRgbBitCount);
        string? fourCcText = hasFourCc ? FourCcToText(fourCc) : null;

        return new DdsInfo
        {
            Width = (int)ReadU32(dds, OffsetWidth),
            Height = (int)ReadU32(dds, OffsetHeight),
            Depth = (int)ReadU32(dds, OffsetDepth),
            MipCount = (int)ReadU32(dds, OffsetMipMapCount),
            FourCc = hasFourCc ? fourCc : 0,
            FourCcText = fourCcText,
            DxgiFormat = dxgi,
            Format = DescribeFormat(fourCcText, dxgi, bitCount),
            // Every FourCC Mafia II ships is a block-compressed one; an uncompressed surface
            // declares no FourCC at all and carries its bit depth instead.
            IsCompressed = hasFourCc,
            IsCubemap = (caps2 & Caps2Cubemap) != 0,
            IsVolume = (caps2 & Caps2Volume) != 0,
            RgbBitCount = hasFourCc ? 0 : bitCount,
            HeaderFlags = ReadU32(dds, OffsetFlags),
            Caps = ReadU32(dds, OffsetCaps),
            Caps2 = caps2,
            DataOffset = dataOffset,
            DataBytes = Math.Max(0, dds.Length - dataOffset),
        };
    }

    /// <summary>Reads the header of a .dds on disk.</summary>
    public static DdsInfo Load(string path) => Read(File.ReadAllBytes(path));

    /// <summary>The declared pitch or linear size, straight out of the header.</summary>
    public static uint ReadPitchOrLinearSize(ReadOnlySpan<byte> dds) => ReadU32(dds, OffsetPitchOrLinearSize);

    private static uint ReadU32(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.ToUInt32(bytes.Slice(offset, sizeof(uint)));

    /// <summary>The four characters of a FourCC, with anything unprintable shown as '?' so a garbage
    /// value cannot smuggle control characters into a response.</summary>
    private static string FourCcToText(uint fourCc)
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            char c = (char)((fourCc >> (i * 8)) & 0xFF);
            chars[i] = c is >= ' ' and <= '~' ? c : '?';
        }
        return new string(chars);
    }

    private static string DescribeFormat(string? fourCcText, uint? dxgi, uint bitCount)
    {
        if (dxgi is { } format)
        {
            // The numeric DXGI id, named where it is one of the block-compressed formats a Mafia II
            // texture actually uses; anything else is reported by number rather than guessed at.
            string name = format switch
            {
                70 or 71 or 72 => "BC1",
                73 or 74 or 75 => "BC2",
                76 or 77 or 78 => "BC3",
                79 or 80 or 81 => "BC4",
                82 or 83 or 84 => "BC5",
                94 or 95 or 96 => "BC6H",
                97 or 98 or 99 => "BC7",
                _ => "DXGI",
            };
            return string.Create(CultureInfo.InvariantCulture, $"{name} (DXGI {format})");
        }

        if (fourCcText is not null)
        {
            return fourCcText;
        }

        return bitCount > 0
            ? string.Create(CultureInfo.InvariantCulture, $"uncompressed {bitCount}-bit")
            : "uncompressed";
    }
}
