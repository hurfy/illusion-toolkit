using Illusion.Assets.Adapters;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Properties;

/// <summary>
/// What the property panel shows for a selected bone. A bone owns no fields of its own beyond its rest
/// transform — which the gizmo and the Position/Rotation/Scale editors already own — so everything here is
/// read-only context: which model it belongs to, where it sits in the rig, and what hangs off it. That last
/// one is the useful part on a car: the bone is the handle, and this is the list of what the handle moves.
/// </summary>
internal static class BonePropertyCatalog
{
    public static IReadOnlyList<PropertyGroup> Build(BoneNodeAdapter bone)
    {
        var groups = new List<PropertyGroup>();
        FrameObjectModel model = bone.Model;

        var identity = new List<PropertyDescriptor>
        {
            Text("Bone.Name", "Name", bone.BoneName),
            Text("Bone.Index", "Index", bone.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Text("Bone.Model", "Of model", model.Name.ToString() ?? "?"),
            Text("Bone.Parent", "Parent bone", ParentName(model, bone.Index)),
            // What the FILE and the loader give — not what is on screen. This layer cannot see a GpuMesh at
            // all, so it cannot answer "does the body follow this bone"; saying it could was a mistake that
            // sent four rounds of fixes at a path that was already right.
            Text("Bone.Deforms", "Skin resolves", SkinState(model)),
        };
        groups.Add(new PropertyGroup { Title = "Bone", IsTypeSpecific = true, Properties = identity });

        var attached = new List<string>();
        foreach (FrameObjectModel.AttachmentReference a in model.AttachmentReferences ?? [])
        {
            if (a.JointIndex != bone.Index || a.Attachment is not { } frame) continue;
            string type = frame.GetType().Name;
            if (type.StartsWith("FrameObject", StringComparison.Ordinal)) type = type[11..];
            attached.Add($"{frame.Name}  ({type})");
        }

        groups.Add(new PropertyGroup
        {
            Title = "Attached frames",
            IsTypeSpecific = true,
            Properties =
            [
                new PropertyDescriptor
                {
                    Id = "Bone.Attachments",
                    Label = attached.Count == 1 ? "1 frame" : $"{attached.Count} frames",
                    Kind = PropertyKind.StructList,
                    IsReadOnly = true,
                    Tooltip = "These move with the bone — their own matrices are in its space.",
                    Get = () => attached.Count > 0 ? attached : (IReadOnlyList<string>)["(nothing hangs here)"],
                },
            ],
        });

        return groups;
    }

    /// <summary>
    /// Why the model's skin did or did not survive the loader — the one thing that decides whether moving a
    /// bone moves any geometry. Named for the check that fails, so a report says which of them it was.
    /// </summary>
    private static string SkinState(FrameObjectModel model)
    {
        Sds.DecodedMesh? decoded;
        try { decoded = Sds.SdsMeshLoader.DecodeLod0(model); }
        catch (Exception) { return "no — the mesh does not decode"; }
        if (decoded == null) return "no — the mesh has no LOD 0";
        if (decoded.BoneIndices == null) return "no — the vertices carry no skin";

        MeshPart[] parts = Sds.SdsMeshLoader.BuildParts(model, decoded.Indices.Length);
        if (Sds.SdsMeshLoader.ResolveBoneRemap(model, parts, decoded) == null)
        {
            FrameBlendInfo blend;
            try { blend = model.GetBlendInfoObject(); }
            catch (Exception) { return "no — the blend info cannot be read"; }
            int groups = blend.BoneIndexInfos is { Length: > 0 } lods
                ? lods[0].SkinnedMaterialInfo?.Length ?? 0
                : 0;
            return $"no — the blend info does not line up ({groups} face groups for {parts.Length} material parts)";
        }

        int bones = 0;
        try { bones = model.GetSkeletonObject().BoneNames?.Length ?? 0; }
        catch (Exception) { /* reported as a bone count of zero */ }
        if (bones > 128) return $"no — {bones} bones is past the 128 the palette holds";

        MeshData? built = Sds.SdsMeshLoader.TryConvert(model);
        if (built == null) return $"no — the mesh does not build ({bones} bones)";
        if (!built.IsSkinned) return $"no — the built mesh has no skin ({bones} bones)";
        return $"yes ({bones} bones)";
    }

    // The parent bone's name, from the model's hierarchy block. A root bone is its own parent in this format,
    // which is why that case reads as "(root)" rather than repeating the bone's own name back at the user.
    private static string ParentName(FrameObjectModel model, int index)
    {
        try
        {
            byte[] parents = model.GetSkeletonHierarchyObject().ParentIndices ?? [];
            HashName[] names = model.GetSkeletonObject().BoneNames ?? [];
            if (index < 0 || index >= parents.Length) return "(root)";
            int parent = parents[index];
            if (parent == index || parent >= names.Length) return "(root)";
            return names[parent].ToString() ?? "?";
        }
        catch (Exception)
        {
            return "(unreadable)";
        }
    }

    private static PropertyDescriptor Text(string id, string label, string value) => new()
    {
        Id = id,
        Label = label,
        Kind = PropertyKind.Text,
        IsReadOnly = true,
        Get = () => value,
    };
}
