using System.Numerics;
using Illusion.Assets.Properties;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Formats.Collisions;

namespace Illusion.Assets.Adapters;

/// <summary>
/// Adapts one <see cref="CollisionInstance"/> placement into an <see cref="IFrameNode"/> (so the standard
/// selection + gizmo + undo pipeline drags it) and an <see cref="IPropertySource"/> (so the property panel shows
/// its fields) — the collision analog of <see cref="FrameNodeAdapter"/>. Canonical by reference (one adapter per
/// instance, cached in <see cref="CollisionDocumentAdapter.Node"/>). A placement has no parent hierarchy, so
/// <see cref="ParentWorldTransform"/> is identity and <see cref="WorldTransform"/> equals <see cref="LocalTransform"/>
/// — which makes <c>TransformOps.WorldDeltaToLocal</c> degenerate to "local = world", exactly what a bare world
/// placement wants. The .col record stores no scale, so a scale lives on the adapter as
/// <see cref="PreviewScale"/> (session-only) until a derived hull is minted for it.
/// </summary>
public sealed class CollisionInstanceAdapter : IFrameNode, IPropertySource
{
    private readonly CollisionInstance _instance;
    private readonly CollisionDocumentAdapter _document;

    internal CollisionInstanceAdapter(CollisionInstance instance, CollisionDocumentAdapter document)
    {
        _instance = instance;
        _document = document;
    }

    /// <summary>The wrapped placement — the property descriptors read/write it directly.</summary>
    public CollisionInstance Instance => _instance;

    /// <summary>The document that owns this placement (its .col save unit).</summary>
    internal CollisionDocumentAdapter Document => _document;

    /// <summary>
    /// Scale the placement is currently shown at. NOT part of the .col: the instance record has no scale field,
    /// so this lives only for the session. A gizmo drag parks the scale here and the hull renders at it, which is
    /// what makes a resize visible before the derived hull exists. Turning it into something the game sees means
    /// minting a scaled hull and repointing <see cref="CollisionInstance.Hash"/> at it — see
    /// <c>CollisionMeshMinter</c>.
    /// </summary>
    public Vector3 PreviewScale { get; set; } = Vector3.One;

    /// <summary>The placement's world matrix in the verified collision convention — the same one
    /// <c>CollisionSceneBuilder</c> instances the hull at, carrying <see cref="PreviewScale"/>. Setting it (gizmo
    /// drag / numeric field) writes the position, re-derives the .col Euler rotation, and parks the scale.</summary>
    public Matrix4x4 LocalTransform
    {
        get => TransformMath.Compose(
            TransformMath.CollisionEulerToQuaternion(_instance.Rotation), PreviewScale, _instance.Position);
        set
        {
            if (TransformMath.TryDecompose(value, out Vector3 scale, out Quaternion rot, out Vector3 pos))
            {
                _instance.Position = pos;
                PreviewScale = scale;
                // Only re-derive the Euler rotation when the orientation actually changed. Euler triples are not
                // unique, so a round-trip through the quaternion returns an equivalent but differently-spelled
                // triple (it flips -0.0 to +0.0, and can renormalise angles the game authored outside the
                // extractor's range). Rewriting it on a pure translate would edit bytes the user never touched.
                if (!ApproximatelyEqual(rot, TransformMath.CollisionEulerToQuaternion(_instance.Rotation)))
                {
                    _instance.Rotation = TransformMath.CollisionEulerFromQuaternion(rot);
                }
            }
            else
            {
                _instance.Position = value.Translation; // degenerate matrix — keep at least the translation
            }
            _document.RenderDirty = true; // the hull overlay is now stale — the streamer re-uploads it next frame
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

    public string TypeName => "Collision";

    public IReadOnlyList<PropertyGroup> GetPropertyGroups() => CollisionPropertyCatalog.Build(this);
}
