using Illusion.Domain;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Passes;

/// <summary>
/// Overlay layer for actors with nothing to draw (sounds, lights, triggers, script hooks…): one glyph per
/// actor, one instance buffer per resident district so streaming can drop a district's markers alone.
/// Colour rides per segment — unlike the other line layers, one layer covers every actor category.
/// Drawing is <see cref="OverlayLinePass"/>, shared with every other overlay.
/// </summary>
internal sealed unsafe class ActorMarkerRenderer : IDisposable
{
    private readonly OverlayLinePass _pass;

    private readonly Dictionary<object, District> _districts = new();

    private struct District
    {
        public ComPtr<ID3D11Buffer> Vb;
        public uint SegmentCount;
        public int MarkerCount;
    }

    /// <summary>True while any district's markers are uploaded.</summary>
    public bool HasData => _districts.Count > 0;

    /// <summary>Markers currently resident (for the status line and the probes).</summary>
    public int MarkerCount
    {
        get
        {
            int n = 0;
            foreach (District d in _districts.Values) n += d.MarkerCount;
            return n;
        }
    }

    public ActorMarkerRenderer(OverlayLinePass pass)
    {
        _pass = pass;
    }

    /// <summary>Uploads/replaces one district's markers. Null or empty removes them.</summary>
    public void SetDistrict(object key, ActorMarkerRenderData? data)
    {
        RemoveDistrict(key);
        if (data == null || data.VertexCount < 2) return;

        OverlaySegment[] segments = OverlaySegments.FromLineList(data.Positions, data.Colors);
        _districts[key] = new District
        {
            Vb = _pass.CreateBuffer(segments),
            SegmentCount = (uint)segments.Length,
            MarkerCount = data.MarkerCount,
        };
    }

    /// <summary>Drops one district's markers (district unload).</summary>
    public void RemoveDistrict(object key)
    {
        if (_districts.Remove(key, out District d)) d.Vb.Dispose();
    }

    /// <summary>Drops every district's markers (scene reset).</summary>
    public void Clear()
    {
        foreach (District d in _districts.Values) d.Vb.Dispose();
        _districts.Clear();
    }

    /// <summary>Draws the glyphs — faint where the scene hides them, unless the style says otherwise.</summary>
    public void Render(ComPtr<ID3D11DeviceContext> ctx, in OverlayFrame frame, in OverlayLineStyle style)
    {
        if (_districts.Count == 0) return;

        if (style.HiddenAlpha < 0.999f)
        {
            _pass.Begin(ctx, frame, style, OverlayDepth.Hidden, style.HiddenAlpha);
            DrawAll(ctx);
            _pass.Begin(ctx, frame, style, OverlayDepth.Visible, 1f);
            DrawAll(ctx);
        }
        else
        {
            _pass.Begin(ctx, frame, style, OverlayDepth.Always, 1f);
            DrawAll(ctx);
        }

        _pass.End(ctx);
    }

    private void DrawAll(ComPtr<ID3D11DeviceContext> ctx)
    {
        foreach (District d in _districts.Values) _pass.DrawSegments(ctx, d.Vb, d.SegmentCount);
    }

    public void Dispose() => Clear();
}
