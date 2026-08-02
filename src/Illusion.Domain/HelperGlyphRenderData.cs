using System.Numerics;

namespace Illusion.Domain;

/// <summary>
/// One line of a helper glyph, in the render-neutral form the viewport's overlay understands: a world anchor
/// plus two offsets. Splitting them is what lets a glyph sit at a fixed PLACE while keeping a fixed SIZE on
/// screen — see <see cref="ScreenSized"/>.
/// </summary>
/// <param name="Anchor">World position the glyph hangs off.</param>
/// <param name="OffsetA">First endpoint relative to the anchor.</param>
/// <param name="OffsetB">Second endpoint relative to the anchor.</param>
/// <param name="Extent">Glyph radius in metres — world-sized glyphs use it for the minimum-size floor, so a
/// one-centimetre dummy is still clickable-sized on screen. Zero opts out.</param>
/// <param name="Color">Line colour (rgb + alpha).</param>
/// <param name="ScreenSized">True when the offsets are PIXELS rather than metres.</param>
public readonly record struct GlyphSegment(
    Vector3 Anchor,
    Vector3 OffsetA,
    Vector3 OffsetB,
    float Extent,
    Vector4 Color,
    bool ScreenSized);

/// <summary>
/// The helper drawing of one archive: the nodes that place something without being anything to look at —
/// dummies, points, volumes — and, in the rig layer, the bones themselves.
/// </summary>
public sealed class HelperGlyphRenderData
{
    /// <summary>Every line of every glyph.</summary>
    public GlyphSegment[] Segments { get; init; } = [];

    /// <summary>Glyphs drawn (a glyph is several segments) — for the status line and the probes.</summary>
    public int GlyphCount { get; init; }

    /// <summary>
    /// Nodes deliberately not drawn: unnamed placeholders parked at their parent's origin, of which a single
    /// car carries seventeen. Drawing them would pile every one on the same spot; the count is reported so
    /// they are not silently lost.
    /// </summary>
    public int HiddenCount { get; init; }

    public int SegmentCount => Segments.Length;

    /// <summary>Nothing to draw and nothing hidden.</summary>
    public bool IsEmpty => Segments.Length == 0 && HiddenCount == 0;
}
