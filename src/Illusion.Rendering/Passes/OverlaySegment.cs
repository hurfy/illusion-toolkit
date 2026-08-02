using System.Numerics;
using System.Runtime.InteropServices;

namespace Illusion.Rendering.Passes;

/// <summary>
/// How an <see cref="OverlaySegment"/>'s offsets are read.
/// </summary>
[Flags]
internal enum OverlaySegmentFlags : uint
{
    /// <summary>Offsets are metres, in the same world space the meshes use.</summary>
    World = 0,

    /// <summary>Offsets are PIXELS around the anchor: the shader converts them to metres at the anchor's
    /// depth, so the glyph keeps the same size on screen however far away it is.</summary>
    ScreenSized = 1,
}

/// <summary>
/// One overlay line segment, as the GPU consumes it: one instance of the quad the vertex shader expands.
/// <para>
/// A segment runs from <c>Anchor + OffsetA</c> to <c>Anchor + OffsetB</c>. Splitting the anchor from the
/// offsets is what lets a glyph keep a constant SIZE on screen while staying at a fixed PLACE in the world:
/// the anchor fixes the place (and its depth sets the pixel scale), the offsets shape the glyph around it.
/// A plain world-space line just puts its first endpoint in the anchor and leaves <see cref="OffsetA"/> zero.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OverlaySegment
{
    /// <summary>World position the segment hangs off — also the depth its pixel scale is measured at.</summary>
    public Vector3 Anchor;

    /// <summary>First endpoint, relative to <see cref="Anchor"/> (metres, or pixels — see <see cref="Flags"/>).</summary>
    public Vector3 OffsetA;

    /// <summary>Second endpoint, relative to <see cref="Anchor"/>.</summary>
    public Vector3 OffsetB;

    /// <summary>
    /// Radius of the glyph this segment belongs to, in metres. Used only by world-space glyphs and only for
    /// the minimum-size floor (<c>MinGlyphPixels</c>): the whole glyph scales up by one shared factor when it
    /// would otherwise project smaller than that, which is why the factor cannot be derived per segment.
    /// Zero opts out.
    /// </summary>
    public float Extent;

    /// <summary>Colour, packed R,G,B,A one byte each (R in the low byte) — multiplied by the pass tint.</summary>
    public uint Rgba;

    /// <summary>See <see cref="OverlaySegmentFlags"/>.</summary>
    public uint Flags;
}

/// <summary>Builders for the segment lists the overlay passes upload.</summary>
internal static class OverlaySegments
{
    /// <summary>Packs a colour into the byte order the input layout reads (R8G8B8A8_UNORM).</summary>
    public static uint Pack(Vector4 color)
    {
        static uint Byte(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        return Byte(color.X) | (Byte(color.Y) << 8) | (Byte(color.Z) << 16) | (Byte(color.W) << 24);
    }

    /// <summary>White — the neutral per-segment colour for a layer that colours itself through the pass tint.</summary>
    public const uint White = 0xFFFFFFFF;

    /// <summary>
    /// World-space segments from a flat endpoint list (A,B,A,B,…) — the shape every existing overlay
    /// (navigation graph, rig, path boxes) already produces. An odd trailing vertex is ignored.
    /// </summary>
    public static OverlaySegment[] FromLineList(IReadOnlyList<Vector3> endpoints, uint rgba = White)
    {
        int count = endpoints.Count / 2;
        var segments = new OverlaySegment[count];
        for (int i = 0; i < count; i++)
        {
            Vector3 a = endpoints[i * 2], b = endpoints[i * 2 + 1];
            segments[i] = new OverlaySegment
            {
                Anchor = a,
                OffsetA = Vector3.Zero,
                OffsetB = b - a,
                Rgba = rgba,
            };
        }

        return segments;
    }

    /// <summary>
    /// World-space segments from a flat endpoint list that carries its own per-vertex colours (the actor
    /// glyphs). The first vertex of each pair decides the segment's colour — both ends always match.
    /// </summary>
    public static OverlaySegment[] FromLineList(IReadOnlyList<Vector3> endpoints, IReadOnlyList<Vector4> colors)
    {
        int count = endpoints.Count / 2;
        var segments = new OverlaySegment[count];
        for (int i = 0; i < count; i++)
        {
            Vector3 a = endpoints[i * 2], b = endpoints[i * 2 + 1];
            segments[i] = new OverlaySegment
            {
                Anchor = a,
                OffsetA = Vector3.Zero,
                OffsetB = b - a,
                Rgba = Pack(colors[i * 2]),
            };
        }

        return segments;
    }
}
