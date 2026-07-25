using System.Collections.Concurrent;

namespace Illusion.Formats.Geometry;

/// <summary>Byte offset and length of one channel inside a packed vertex.</summary>
public readonly record struct VertexOffset(int Offset, int Length);

/// <summary>
/// Where each channel of a packed vertex lands, for callers that read a channel straight out of the
/// packed bytes. The packing order and the channel widths are the engine's, so the native core
/// answers them; a declaration's plan never changes, so answers are cached per declaration.
/// </summary>
public static class VertexLayout
{
    private static readonly ConcurrentDictionary<uint, Plan> Plans = new();

    private sealed record Plan(IReadOnlyDictionary<VertexFlags, VertexOffset> Offsets, int Stride);

    /// <summary>Per-channel offsets for a declaration, plus the total vertex stride.</summary>
    public static Dictionary<VertexFlags, VertexOffset> ComputeOffsets(VertexFlags declaration, out int stride)
    {
        Plan plan = Plans.GetOrAdd((uint)declaration, Describe);
        stride = plan.Stride;
        return new Dictionary<VertexFlags, VertexOffset>(plan.Offsets);
    }

    private static Plan Describe(uint declaration)
    {
        Native.Model.VertexLayoutW layout = Native.Frames.NativeFrames.VertexLayout(declaration);
        var offsets = new Dictionary<VertexFlags, VertexOffset>(layout.Channels.Count);
        foreach (Native.Model.VertexChannelW channel in layout.Channels)
        {
            offsets[(VertexFlags)channel.Flag] = new VertexOffset(channel.Offset, channel.Length);
        }
        return new Plan(offsets, layout.Stride);
    }
}
