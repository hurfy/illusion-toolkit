using Illusion.Formats.Hashing;

namespace Illusion.Formats.Frames.ObjectTypes;

public class FrameObjectCamera : FrameObjectJoint
{
    int numLens;
    LensData[] lens = null!;
    public LensData[] Lens
    {
        get { return lens; }
        set { lens = value; }
    }

    public FrameObjectCamera(FrameResource OwningResource) : base(OwningResource)
    {
        numLens = 0;
    }

    /// <summary>Replaces the lens data keeping the on-disk count in step (the writer emits the
    /// stored count, not the array length) — for the native-boundary mapper.</summary>
    internal void SetLensData(LensData[] data)
    {
        lens = data;
        numLens = data.Length;
    }

    public FrameObjectCamera(FrameObjectCamera other) : base(other)
    {
        numLens = other.numLens;
        lens = other.lens;
    }

    public override string ToString()
    {
        return string.Format("Camera Block");
    }

    public class LensData
    {
        float[] unkFloats;
        HashName unkHash;

        public float[] UnkFloats
        {
            get { return unkFloats; }
            set { unkFloats = value; }
        }
        public HashName UnkHash
        {
            get { return unkHash; }
            set { unkHash = value; }
        }

        /// <summary>Lens from decoded values — for the native-boundary mapper.</summary>
        internal LensData(float[] floats, HashName hash)
        {
            unkFloats = floats;
            unkHash = hash;
        }

    }
}
