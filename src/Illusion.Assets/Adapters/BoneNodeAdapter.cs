using System.Numerics;
using Illusion.Assets.Properties;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Adapters;

/// <summary>
/// One bone of a skinned model, dressed as an ordinary transformable object so the gizmo, the undo history,
/// the property panel and the save path can all treat it like anything else in the scene.
/// <para>
/// A bone is not a frame — it is a slot in the model's <see cref="FrameObjectModel.RestTransform"/> table —
/// but for a car it is the only place a part exists at all: <c>doorFL</c>, <c>coverF</c>, <c>axleFR</c> are
/// bones, and the door hulls, locks, handles and climb boxes hang off them. Moving one moves everything
/// attached to it, because an attached frame is placed THROUGH its joint
/// (see <see cref="FrameObjectBase.AttachedTo"/>). The body mesh itself does not follow yet: it is one skinned
/// mesh and needs the vertex weights applied, which the viewport does not do.
/// </para>
/// </summary>
public sealed class BoneNodeAdapter : IFrameNode, IPropertySource
{
    private readonly FrameObjectModel _model;
    private readonly SceneDocumentAdapter _document;
    private readonly int _index;

    internal BoneNodeAdapter(FrameObjectModel model, int index, SceneDocumentAdapter document)
    {
        _model = model;
        _document = document;
        _index = index;
    }

    /// <summary>The model this bone belongs to.</summary>
    public FrameObjectModel Model => _model;

    /// <summary>Index into the model's rest-transform table and its skeleton's bone names.</summary>
    public int Index => _index;

    /// <summary>The bone's name, from the model's skeleton block; empty when the block cannot be read.</summary>
    public string BoneName
    {
        get
        {
            try
            {
                HashName[] names = _model.GetSkeletonObject().BoneNames ?? [];
                return _index < names.Length ? names[_index].ToString() ?? "" : "";
            }
            catch (Exception)
            {
                return "";
            }
        }
    }

    /// <summary>
    /// The bone's rest transform, in the MODEL's space rather than the parent bone's (measured on the corpus,
    /// see <c>--probe-cars</c>).
    /// <para>
    /// Setting it carries the bone's DESCENDANTS with it. The rest transforms being model-space means nothing
    /// follows on its own — and a car's rig is full of bones that must: <c>doorFL</c> parents
    /// <c>window_doorFL</c> and <c>deform_doorFL</c>, so a door swung open without them leaves its own window
    /// hanging in the air. Each descendant is moved by the same delta this bone moved by, which is what keeps
    /// the part rigid; applying the inverse delta (what undo does when it sets the old matrix back) puts them
    /// all back.
    /// </para>
    /// </summary>
    public Matrix4x4 LocalTransform
    {
        get => _index >= 0 && _index < Rest.Length ? Rest[_index] : Matrix4x4.Identity;
        set
        {
            if (_index < 0 || _index >= Rest.Length) return;

            // The motion this bone just underwent, in model space. Computed BEFORE the bone is overwritten.
            Matrix4x4[] rest = Rest;
            bool carries = Matrix4x4.Invert(Affine(rest[_index]), out Matrix4x4 inverse);
            Matrix4x4 delta = carries ? inverse * Affine(value) : Matrix4x4.Identity;

            rest[_index] = value;

            IReadOnlyList<int> descendants = Descendants();
            if (carries)
            {
                foreach (int child in descendants)
                {
                    if (child >= 0 && child < rest.Length) rest[child] = Affine(rest[child]) * delta;
                }
            }

            // Everything attached to a joint that moved derives its world from that joint, so it only has to
            // be asked again — which is what refreshing the attached frames does.
            foreach (FrameObjectModel.AttachmentReference a in _model.AttachmentReferences ?? [])
            {
                if (a.JointIndex == _index || descendants.Contains(a.JointIndex)) a.Attachment?.SetWorldTransform();
            }
        }
    }

    /// <summary>
    /// Every bone below this one in the rig, by index. Best-effort: a model whose hierarchy block cannot be
    /// read simply carries nothing, which is the behaviour this had before it carried anything at all.
    /// </summary>
    private IReadOnlyList<int> Descendants()
    {
        byte[] parents;
        try { parents = _model.GetSkeletonHierarchyObject().ParentIndices ?? []; }
        catch (Exception) { return []; }
        if (parents.Length == 0) return [];

        var below = new List<int>();
        var claimed = new bool[parents.Length];
        if (_index < claimed.Length) claimed[_index] = true;

        // Parents come before children in every rig in the corpus, so one forward pass places them all; the
        // claimed flags also stop a malformed hierarchy from looping.
        for (int i = 0; i < parents.Length; i++)
        {
            int parent = parents[i];
            if (parent == i || parent >= parents.Length || i >= claimed.Length) continue;
            if (!claimed[parent] || claimed[i]) continue;
            claimed[i] = true;
            below.Add(i);
        }
        return below;
    }

    // A frame matrix rides as 4x3, so its fourth column is not (0,0,0,1) and neither inversion nor
    // multiplication behaves until the 1 is put back.
    private static Matrix4x4 Affine(Matrix4x4 m)
    {
        m.M14 = 0;
        m.M24 = 0;
        m.M34 = 0;
        m.M44 = 1;
        return m;
    }

    public Matrix4x4 WorldTransform => _model.GetJointWorldTransform(_index);

    /// <summary>The model's own world: a rest transform is model-space, so that is what a drag re-localizes
    /// against.</summary>
    public Matrix4x4 ParentWorldTransform =>
        _model.WorldTransform * _document.Placements.For(_model);

    /// <summary>The model, so a group drag that has both selected moves the model only and lets its bones
    /// ride along rather than applying the delta twice.</summary>
    public IFrameNode? Parent => _document.Node(_model);

    public bool IsOnNameTable => false;

    public int NameTableFlags => 0;

    /// <summary>What the panel calls this — "Bone", not the adapter's class name.</summary>
    public string TypeName => "Bone";

    public IReadOnlyList<PropertyGroup> GetPropertyGroups() => BonePropertyCatalog.Build(this);

    private Matrix4x4[] Rest => _model.RestTransform ?? [];
}
