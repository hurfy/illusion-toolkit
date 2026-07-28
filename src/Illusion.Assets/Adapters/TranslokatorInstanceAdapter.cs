using System.Numerics;
using Illusion.Assets.Properties;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Formats.Translokator;

namespace Illusion.Assets.Adapters;

/// <summary>
/// Adapts one Translokator <see cref="Instance"/> — a single copy of a city_crash prop — into an
/// <see cref="IFrameNode"/> (so the standard selection + gizmo + undo pipeline drags it) and an
/// <see cref="IPropertySource"/> (so the property panel shows its fields). The crash analog of
/// <see cref="CollisionInstanceAdapter"/>, and canonical by reference (one adapter per placement, cached in
/// <see cref="TranslokatorDocumentAdapter.Node"/>). A placement has no parent hierarchy, so
/// <see cref="ParentWorldTransform"/> is identity and <see cref="WorldTransform"/> equals
/// <see cref="LocalTransform"/> — which makes <c>TransformOps.WorldDeltaToLocal</c> degenerate to
/// "local = world", exactly what a bare world placement wants.
/// </summary>
public sealed class TranslokatorInstanceAdapter : IFrameNode, IPropertySource
{
    private readonly Instance _instance;
    private readonly Formats.Translokator.Object _owner;
    private readonly TranslokatorDocumentAdapter _document;

    internal TranslokatorInstanceAdapter(Instance instance, Formats.Translokator.Object owner,
        TranslokatorDocumentAdapter document)
    {
        _instance = instance;
        _owner = owner;
        _document = document;
        // The shipped summer and winter tables hold the very same placements, so anything that came out of the
        // game is in both seasons and starts linked. A placement the editor creates decides for itself.
        SeasonLinked = document.HasTwinOf(instance, owner);
    }

    /// <summary>The wrapped placement — the property descriptors read/write it directly.</summary>
    public Instance Instance => _instance;

    /// <summary>The table row this placement belongs to: the prop's name, its mesh reference and the draw
    /// distances that also decide which streaming grid counts it.</summary>
    public Formats.Translokator.Object Owner => _owner;

    /// <summary>The document that owns this placement (its .tra save unit).</summary>
    public TranslokatorDocumentAdapter Document => _document;

    /// <summary>
    /// Whether edits to this placement also apply to the other season's archive. NOT a field of the file: the two
    /// seasonal tables simply hold the same placements, and this tracks whether this one is being kept in step
    /// with its twin. Turning it off leaves the twin where it is and stops mirroring — so a later delete removes
    /// the placement from the current season only. Turning it on for a placement with no twin creates one.
    /// </summary>
    public bool SeasonLinked { get; set; }

    /// <summary>The placement's world matrix. Setting it (gizmo drag / numeric field) writes the position,
    /// re-derives the file's Euler rotation and the single scale factor, keeps the streaming grid's per-cell
    /// counts in step, and mirrors the whole change into the other season when
    /// <see cref="SeasonLinked"/>.</summary>
    public Matrix4x4 LocalTransform
    {
        get => TransformMath.Compose(_instance.Quaternion, new Vector3(_instance.Scale), _instance.Position);
        set
        {
            if (TransformMath.TryDecompose(value, out Vector3 scale, out Quaternion rot, out Vector3 pos))
            {
                if (pos != _instance.Position)
                {
                    _document.MovePlacement(_owner, _instance.Position, pos);
                    _instance.Position = pos;
                }

                // Only re-derive the Euler triple when the orientation actually changed. Triples are not unique,
                // so a round trip through the quaternion returns an equivalent but differently-spelled one, which
                // would re-quantize to different bytes on a pure translate — bytes the user never touched.
                if (!ApproximatelyEqual(rot, _instance.Quaternion))
                {
                    _instance.Rotation = TransformMath.TranslokatorEulerFromQuaternion(rot) * (180f / MathF.PI);
                }

                // The record stores ONE scale factor, so a non-uniform gizmo drag has to collapse to an average;
                // there is nowhere to put the difference between the axes.
                float uniform = (scale.X + scale.Y + scale.Z) / 3f;
                if (MathF.Abs(uniform - _instance.Scale) > 1e-6f)
                {
                    _instance.Scale = uniform;
                }
            }
            else
            {
                // Degenerate matrix — keep at least the translation, and keep the grid honest about it.
                Vector3 fallback = value.Translation;
                if (fallback != _instance.Position)
                {
                    _document.MovePlacement(_owner, _instance.Position, fallback);
                    _instance.Position = fallback;
                }
            }

            _document.MirrorTransform(this);
            // Only THIS row's copies are stale; the streamer re-uploads exactly those next frame. Flagging the
            // whole document instead would rebuild every prototype in the archive on every frame of a drag.
            _document.MarkRowDirty(_owner);
        }
    }

    // Quaternion equality up to sign (q and -q are the same orientation) and float noise from the
    // matrix→quaternion decomposition.
    private static bool ApproximatelyEqual(Quaternion a, Quaternion b) =>
        MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))) > 0.9999995f;

    public Matrix4x4 WorldTransform => LocalTransform;           // no parent hierarchy → world == local
    public Matrix4x4 ParentWorldTransform => Matrix4x4.Identity; // parentless: gizmo re-localization is a no-op
    public IFrameNode? Parent => null;
    public bool IsOnNameTable => false;
    public int NameTableFlags => 0;

    public string TypeName => "Crash object";

    public IReadOnlyList<PropertyGroup> GetPropertyGroups() => TranslokatorPropertyCatalog.Build(this);
}
