using System.Numerics;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;
using Illusion.Formats.Mathematics;
using Wire = Illusion.Formats.Native.Model;

namespace Illusion.Formats.Native.Frames;

/// <summary>
/// Builds the editable managed document from a natively parsed FrameResource wire image,
/// reproducing the managed reader's construction flow exactly: the same asset-construction
/// order (RefIDs), the same reference wiring for meshes and models, and the same final
/// parent-resolution pass.
/// </summary>
internal static class FrameResourceReader
{
    internal static void Populate(FrameResource target, Wire.FrameModel wire)
    {
        var header = new FrameHeader()
        {
            IsScene = wire.Header.IsScene != 0,
            NumFolderNames = wire.SceneFolders.Count,
            NumGeometries = wire.Geometries.Count,
            NumMaterialResources = wire.Materials.Count,
            NumBlendInfos = wire.BlendInfos.Count,
            NumSkeletons = wire.Skeletons.Count,
            NumSkelHierachies = wire.Hierarchies.Count,
            NumObjects = wire.Objects.Count,
        };
        target.Header = header;
        if (header.IsScene)
        {
            header.Unk1 = wire.Header.Unk1;
            header.Unk2 = wire.Header.Unk2;
            header.SceneName = ToHashName(wire.Header.SceneName);
            header.UnkFloats = [.. wire.Header.UnkFloats];
            header.Unk3 = wire.Header.Unk3 != 0;
        }

        // The combined reference list, in the block order the file uses.
        var refs = new List<int>();
        foreach (Wire.SceneFolderW folder in wire.SceneFolders)
        {
            FrameHeaderScene scene = target.ConstructFrameAssetOfType<FrameHeaderScene>();
            scene.Name = ToHashName(folder.Name);
            refs.Add(scene.RefID);
        }
        foreach (Wire.FrameGeometryW source in wire.Geometries)
        {
            FrameGeometry geometry = target.ConstructFrameAssetOfType<FrameGeometry>();
            FillGeometry(geometry, source);
            refs.Add(geometry.RefID);
        }
        foreach (Wire.FrameMaterialW source in wire.Materials)
        {
            FrameMaterial material = target.ConstructFrameAssetOfType<FrameMaterial>();
            FillMaterial(material, source);
            refs.Add(material.RefID);
        }
        foreach (Wire.FrameBlendInfoW source in wire.BlendInfos)
        {
            FrameBlendInfo blend = target.ConstructFrameAssetOfType<FrameBlendInfo>();
            FillBlendInfo(blend, source);
            refs.Add(blend.RefID);
        }
        foreach (Wire.FrameSkeletonW source in wire.Skeletons)
        {
            FrameSkeleton skeleton = target.ConstructFrameAssetOfType<FrameSkeleton>();
            FillSkeleton(skeleton, source);
            refs.Add(skeleton.RefID);
        }
        foreach (Wire.FrameHierarchyW source in wire.Hierarchies)
        {
            FrameSkeletonHierarchy hierarchy = target.ConstructFrameAssetOfType<FrameSkeletonHierarchy>();
            hierarchy.ParentIndices = [.. source.ParentIndices];
            hierarchy.Unk01 = source.UnkNum;
            hierarchy.LastChildIndices = [.. source.LastChildIndices];
            hierarchy.UnkData = [.. source.UnkData];
            refs.Add(hierarchy.RefID);
        }

        for (int i = 0; i < wire.Objects.Count; i++)
        {
            Wire.FrameObjectRefW reference = wire.Objects[i];
            var kind = (FrameResourceObjectType)reference.Kind;
            FrameObjectBase created = FrameFactory.ConstructFrameByObjectID(target, kind);
            FillObject(created, kind, reference.Slot, wire);
            created.Index = i;
            created.MarkLoadedParents();

            // Reference wiring, exactly like the managed reader does per object.
            if (kind == FrameResourceObjectType.SingleMesh)
            {
                var mesh = (FrameObjectSingleMesh)created;
                if (mesh.MeshIndex != -1)
                {
                    mesh.AddRef(FrameEntryRefTypes.Geometry, refs[mesh.MeshIndex]);
                    mesh.Geometry = target.FrameGeometries[mesh.Refs[FrameEntryRefTypes.Geometry]];
                }
                if (mesh.MaterialIndex != -1)
                {
                    mesh.AddRef(FrameEntryRefTypes.Material, refs[mesh.MaterialIndex]);
                    mesh.Material = target.FrameMaterials[mesh.Refs[FrameEntryRefTypes.Material]];
                }
            }
            else if (kind == FrameResourceObjectType.Model)
            {
                var mesh = (FrameObjectModel)created;
                mesh.AddRef(FrameEntryRefTypes.Geometry, refs[mesh.MeshIndex]);
                mesh.Geometry = target.FrameGeometries[mesh.Refs[FrameEntryRefTypes.Geometry]];
                mesh.AddRef(FrameEntryRefTypes.Material, refs[mesh.MaterialIndex]);
                mesh.Material = target.FrameMaterials[mesh.Refs[FrameEntryRefTypes.Material]];
                mesh.AddRef(FrameEntryRefTypes.BlendInfo, refs[mesh.BlendInfoIndex]);
                mesh.BlendInfo = target.FrameBlendInfos[mesh.Refs[FrameEntryRefTypes.BlendInfo]];
                mesh.AddRef(FrameEntryRefTypes.Skeleton, refs[mesh.SkeletonIndex]);
                mesh.Skeleton = target.FrameSkeletons[mesh.Refs[FrameEntryRefTypes.Skeleton]];
                mesh.AddRef(FrameEntryRefTypes.SkeletonHierarchy, refs[mesh.SkeletonHierarchyIndex]);
                mesh.SkeletonHierarchy =
                    target.FrameSkeletonHierachies[mesh.Refs[FrameEntryRefTypes.SkeletonHierarchy]];

                // Part-2 joint names resolve against the skeleton, like the managed second phase.
                foreach (FrameObjectModel.AttachmentReference attachment in mesh.AttachmentReferences)
                {
                    attachment.JointName = mesh.Skeleton.BoneNames[attachment.JointIndex].ToString();
                }
            }
        }

        target.DefineFrameBlockParents();
    }

    // ── field mapping ──

    private static HashName ToHashName(Wire.HashNameW value)
    {
        // Name first (its setter rederives the hash), then the on-disk hash verbatim —
        // vanilla files carry hashes that are NOT the FNV64 of the name.
        var result = new HashName();
        result.String = value.Name;
        result.Hash = value.Hash;
        return result;
    }

    internal static Matrix4x4 ToMatrix(Wire.Mat34 value)
    {
        if (value.Values.Count != 12)
        {
            throw new FormatException($"a 3x4 matrix must carry 12 floats, got {value.Values.Count}");
        }
        // The identity pattern means the native side substituted a NaN transform —
        // return the full identity like the managed reader does.
        var matrix = new Matrix4x4();
        matrix.SetColumn(0, new Vector4(value.Values[0], value.Values[1], value.Values[2], value.Values[3]));
        matrix.SetColumn(1, new Vector4(value.Values[4], value.Values[5], value.Values[6], value.Values[7]));
        matrix.SetColumn(2, new Vector4(value.Values[8], value.Values[9], value.Values[10], value.Values[11]));
        if (matrix.GetColumn(0) == new Vector4(1, 0, 0, 0)
            && matrix.GetColumn(1) == new Vector4(0, 1, 0, 0)
            && matrix.GetColumn(2) == new Vector4(0, 0, 1, 0))
        {
            return Matrix4x4.Identity;
        }
        return matrix;
    }

    private static BoundingBox ToBounds(Wire.BBoxW value) => new(value.Min, value.Max);

    private static FrameObjectJoint.NodeStruct[] ToJointNodes(List<Wire.JointNode> nodes)
    {
        var result = new FrameObjectJoint.NodeStruct[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            result[i] = new FrameObjectJoint.NodeStruct
            {
                Unk_01 = nodes[i].Unk1,
                Unk_02_Hash = ToHashName(nodes[i].Unk2),
                Unk_03_Hash = ToHashName(nodes[i].Unk3),
            };
        }
        return result;
    }

    private static void FillBase(FrameObjectBase target, Wire.FrameBaseW source)
    {
        target.Name = ToHashName(source.Name);
        target.SecondaryFlags = source.SecondaryFlags;
        target.SetLocalTransformRaw(ToMatrix(source.Transform));
        target.Unk3 = source.Unk3;
        target.ParentIndex1 = new ParentInfo(source.ParentIndex1);
        target.ParentIndex2 = new ParentInfo(source.ParentIndex2);
        target.Unk6 = source.Unk6;
    }

    private static void FillJoint(FrameObjectJoint target, Wire.FrameBaseW source, List<Wire.JointNode> nodes)
    {
        FillBase(target, source);
        target.DataSize = (byte)nodes.Count;
        target.Data = ToJointNodes(nodes);
    }

    private static void FillSingleMeshFields(FrameObjectSingleMesh target, Wire.FrameBaseW baseW,
        List<Wire.JointNode> nodes, int flags, Wire.BBoxW bounds, byte deform, int meshIndex,
        int materialIndex, Wire.HashNameW omTexture, byte unk1, byte unk2, byte unk3)
    {
        FillJoint(target, baseW, nodes);
        target.SingleMeshFlags = (SingleMeshFlags)flags;
        target.Boundings = ToBounds(bounds);
        target.DeformPartIndex = deform;
        target.MeshIndex = meshIndex;
        target.MaterialIndex = materialIndex;
        target.OMTextureHash = ToHashName(omTexture);
        target.Unk_18_1 = unk1;
        target.Unk_18_2 = unk2;
        target.Unk_18_3 = unk3;
    }

    private static void FillObject(FrameObjectBase created, FrameResourceObjectType kind, uint slot,
        Wire.FrameModel wire)
    {
        switch (kind)
        {
            case FrameResourceObjectType.Point:
            {
                Wire.FramePointW w = wire.Points[(int)slot];
                FillJoint((FrameObjectJoint)created, w.Base, w.Nodes);
                break;
            }
            case FrameResourceObjectType.SingleMesh:
            {
                Wire.FrameSingleMeshW w = wire.SingleMeshes[(int)slot];
                FillSingleMeshFields((FrameObjectSingleMesh)created, w.Base, w.Nodes, w.Flags,
                    w.Bounds, w.DeformPartIndex, w.MeshIndex, w.MaterialIndex, w.OmTexture,
                    w.Unk181, w.Unk182, w.Unk183);
                break;
            }
            case FrameResourceObjectType.Frame:
            {
                Wire.FrameFrameW w = wire.Frames[(int)slot];
                var frame = (FrameObjectFrame)created;
                FillJoint(frame, w.Base, w.Nodes);
                frame.ActorHash = ToHashName(w.ActorHash);
                break;
            }
            case FrameResourceObjectType.Light:
                FillLight((FrameObjectLight)created, wire.Lights[(int)slot]);
                break;
            case FrameResourceObjectType.Camera:
            {
                Wire.FrameCameraW w = wire.Cameras[(int)slot];
                var camera = (FrameObjectCamera)created;
                FillJoint(camera, w.Base, w.Nodes);
                camera.SetLensData([.. w.Lens.Select(l =>
                    new FrameObjectCamera.LensData([.. l.Floats], ToHashName(l.Hash)))]);
                break;
            }
            case FrameResourceObjectType.Component_U00000005:
            {
                Wire.FrameComponentU005W w = wire.ComponentsU005[(int)slot];
                var component = (FrameObjectComponent_U005)created;
                FillBase(component, w.Base);
                component.Unk01 = w.Unk01;
                break;
            }
            case FrameResourceObjectType.Sector:
            {
                Wire.FrameSectorW w = wire.Sectors[(int)slot];
                var sector = (FrameObjectSector)created;
                FillJoint(sector, w.Base, w.Nodes);
                sector.Unk08 = w.Unk08;
                sector.PlanesSize = w.Planes.Count / 4;
                sector.Planes = ToPlanes(w.Planes);
                sector.Bounds = ToBounds(w.Bounds);
                sector.Unk13 = w.Unk13;
                sector.Unk14 = w.Unk14;
                sector.SectorName = ToHashName(w.SectorName);
                break;
            }
            case FrameResourceObjectType.Dummy:
            {
                Wire.FrameDummyW w = wire.Dummies[(int)slot];
                var dummy = (FrameObjectDummy)created;
                FillJoint(dummy, w.Base, w.Nodes);
                dummy.Bounds = ToBounds(w.Bounds);
                break;
            }
            case FrameResourceObjectType.ParticleDeflector:
            {
                Wire.FrameDeflectorW w = wire.Deflectors[(int)slot];
                FillJoint((FrameObjectJoint)created, w.Base, w.Nodes);
                break;
            }
            case FrameResourceObjectType.Area:
            {
                Wire.FrameAreaW w = wire.Areas[(int)slot];
                var area = (FrameObjectArea)created;
                FillJoint(area, w.Base, w.Nodes);
                area.Unk01 = w.Unk01;
                area.PlaneSize = w.Planes.Count / 4;
                area.Planes = ToPlanes(w.Planes);
                area.Bounds = ToBounds(w.Bounds);
                break;
            }
            case FrameResourceObjectType.Target:
            {
                Wire.FrameTargetW w = wire.Targets[(int)slot];
                var targetObject = (FrameObjectTarget)created;
                FillJoint(targetObject, w.Base, w.Nodes);
                targetObject.Unk01 = w.Unk01;
                targetObject.Unk02 = w.Unk02;
                break;
            }
            case FrameResourceObjectType.Model:
                FillModel((FrameObjectModel)created, wire.Models[(int)slot]);
                break;
            case FrameResourceObjectType.Collision:
            {
                Wire.FrameCollisionW w = wire.Collisions[(int)slot];
                var collision = (FrameObjectCollision)created;
                FillBase(collision, w.Base);
                collision.Hash = w.Hash;
                break;
            }
            default:
                throw new FormatException($"unknown frame object type {kind}");
        }
    }

    private static Vector4[] ToPlanes(List<float> planes)
    {
        var result = new Vector4[planes.Count / 4];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new Vector4(planes[i * 4], planes[i * 4 + 1], planes[i * 4 + 2], planes[i * 4 + 3]);
        }
        return result;
    }

    private static void FillLight(FrameObjectLight light, Wire.FrameLightW w)
    {
        FillJoint(light, w.Base, w.Nodes);
        light.Flags = w.Flags;
        light.LUnk0 = w.F0; light.LUnk1 = w.F1; light.LUnk2 = w.F2; light.LUnk3 = w.F3;
        light.LUnk4 = w.F4; light.LUnk5 = w.F5; light.LUnk6 = w.F6;
        light.UnkInt1 = w.UnkInt1;
        light.UnkVector_0 = w.V0;
        light.LUnk7 = w.F7; light.LUnk8 = w.F8;
        light.UnkByte1 = w.B1;
        light.LUnk9 = w.F9; light.LUnk10 = w.F10; light.LUnk11 = w.F11; light.LUnk12 = w.F12;
        light.UnkVector_1 = w.V1;
        light.UnkVector_2 = w.V2;
        light.LUnk13 = w.F13;
        light.UnkVector_3 = w.V3;
        light.LUnk14 = w.F14; light.LUnk15 = w.F15; light.LUnk16 = w.F16;
        light.UnkByte2 = w.B2;
        light.LUnk17 = w.F17; light.LUnk18 = w.F18; light.LUnk19 = w.F19; light.LUnk20 = w.F20;
        light.LUnk21 = w.F21;
        light.ProjectionTexture = ToHashName(w.ProjectionTexture);
        light.UnkInt2 = w.UnkInt2;
        light.LUnk22 = w.F22; light.LUnk23 = w.F23;
        light.UnkVector_4 = w.V4;
        light.LUnk24 = w.F24; light.LUnk25 = w.F25; light.LUnk26 = w.F26; light.LUnk27 = w.F27;
        light.LUnk28 = w.F28;
        light.UnkVector_5 = w.V5;
        light.LUnk29 = w.F29; light.LUnk30 = w.F30; light.LUnk31 = w.F31; light.LUnk32 = w.F32;
        light.LUnk33 = w.F33; light.LUnk34 = w.F34; light.LUnk35 = w.F35;
        light.TextureHashes =
        [
            ToHashName(w.Texture0), ToHashName(w.Texture1), ToHashName(w.Texture2), ToHashName(w.Texture3),
        ];
        light.UnkBox = ToBounds(w.UnkBox);
        light.UnkByte3 = w.B3;
        light.UnknownMatrix = ToMatrix(w.UnkMatrix);
    }

    private static void FillModel(FrameObjectModel model, Wire.FrameModelObjectW w)
    {
        FillSingleMeshFields(model, w.Base, w.Nodes, w.Flags, w.Bounds, w.DeformPartIndex,
            w.MeshIndex, w.MaterialIndex, w.OmTexture, w.Unk181, w.Unk182, w.Unk183);
        model.BlendInfoIndex = w.BlendInfoIndex;
        model.SkeletonIndex = w.SkeletonIndex;
        model.SkeletonHierarchyIndex = w.SkeletonHierarchyIndex;
        model.RestTransform = [.. w.RestTransforms.Select(ToMatrix)];
        model.UnkTransform = ToMatrix(w.UnkTransform);
        model.AttachmentReferences = [.. w.Attachments.Select(a => new FrameObjectModel.AttachmentReference
        {
            AttachmentIndex = a.AttachmentIndex,
            JointIndex = a.JointIndex,
        })];
        model.UnkFlags = w.UnkFlags;
        model.SplitCounters = (w.PhysSplitSize, w.HitBoxSize, (short)w.Splits.Count);
        model.BlendMeshSplits = [.. w.Splits.Select(s => new FrameObjectModel.WeightedByMeshSplit
        {
            BlendIndex = s.BlendIndex,
            JointName = "",
            Data = [.. s.Data.Select(d => new FrameObjectModel.BlendMeshSplitInfo
            {
                Data = [.. d.Bursts.Select(b => new FrameObjectModel.MiniMaterialBurst
                {
                    MaterialIndex = b.MaterialIndex,
                    Data = [.. b.Bursts.Select(f => new FrameObjectModel.FacesBurst
                    {
                        StartIndex = f.StartIndex,
                        NumFaces = f.NumFaces,
                    })],
                })],
            })],
        })];
        model.HitBoxes = [.. w.HitBoxes.Select(h => new FrameObjectModel.HitBoxInfo
        {
            Unk = h.Unk,
            Position = new Short3 { S1 = h.Px, S2 = h.Py, S3 = h.Pz },
            Size = new Short3 { S1 = h.Sx, S2 = h.Sy, S3 = h.Sz },
        })];
    }

    private static void FillGeometry(FrameGeometry geometry, Wire.FrameGeometryW w)
    {
        geometry.NumLods = (byte)w.Lods.Count;
        geometry.Unk01 = w.Unk01;
        geometry.DecompressionOffset = w.DecompressionOffset;
        geometry.DecompressionFactor = w.DecompressionFactor;
        geometry.LOD = new FrameLOD[w.Lods.Count];
        for (int i = 0; i < w.Lods.Count; i++)
        {
            Wire.FrameLodW lod = w.Lods[i];
            geometry.LOD[i] = new FrameLOD();
            geometry.LOD[i].LoadFromWireParts(lod.Distance, ToHashName(lod.IndexBuffer),
                lod.VertexDecl, ToHashName(lod.VertexBuffer), lod.NumVerts, lod.NZero1,
                lod.OpcodeCapsule, lod.SplitCapsule);
        }
    }

    private static void FillMaterial(FrameMaterial material, Wire.FrameMaterialW w)
    {
        material.NumLods = (uint)w.Lods.Count;
        material.LodMatCount = [.. w.Lods.Select(l => l.Faces.Count)];
        material.Bounds = ToBounds(w.Bounds);
        material.Materials.Clear();
        foreach (Wire.MaterialLodW lod in w.Lods)
        {
            var faces = new MaterialStruct[lod.Faces.Count];
            for (int i = 0; i < faces.Length; i++)
            {
                faces[i] = new MaterialStruct
                {
                    NumFaces = lod.Faces[i].NumFaces,
                    StartIndex = lod.Faces[i].StartIndex,
                    MaterialHash = lod.Faces[i].MaterialHash,
                    Unk3 = lod.Faces[i].Unk3,
                };
            }
            material.Materials.Add(faces);
        }
    }

    private static void FillBlendInfo(FrameBlendInfo blend, Wire.FrameBlendInfoW w)
    {
        blend.Bound = ToBounds(w.Bounds);
        blend.BoneTransforms = [.. w.BoneTransforms.Select(t => new FrameBlendInfo.BoneTransform
        {
            Transform = ToMatrix(t.Transform),
            Bounds = ToBounds(t.Bounds),
            IsValid = t.IsValid,
        })];
        blend.BoneIndexInfos = [.. w.Lods.Select(l => new FrameBlendInfo.BoneIndexInfo
        {
            BonesPerRemapPool = [.. l.BonesPerRemapPool],
            BoneRemapIDs = [.. l.BoneRemapIds],
            SkinnedMaterialInfo = [.. l.Materials.Select(m => new FrameBlendInfo.SkinnedMaterialInfo
            {
                AssignedPoolIndex = m.AssignedPoolIndex,
                NumWeightsPerVertex = m.NumWeights,
            })],
        })];
    }

    private static void FillSkeleton(FrameSkeleton skeleton, Wire.FrameSkeletonW w)
    {
        skeleton.NumBones = [w.NumBones0, w.NumBones1, w.NumBones2, w.NumBones3];
        skeleton.NumBlendIDs = w.NumBlendIds;
        skeleton.LodRemapIDCount = [.. w.LodRemapCounts];
        skeleton.IDType = w.IdType;
        skeleton.BoneNames = [.. w.BoneNames.Select(ToHashName)];
        skeleton.JointTransforms = [.. w.JointTransforms.Select(ToMatrix)];
        skeleton.NumUnkCount2 = w.BoneLodUsage.Length;
        skeleton.BoneLODUsage = [.. w.BoneLodUsage];
        skeleton.WorldTransforms = [.. w.WorldTransforms.Select(ToMatrix)];
        skeleton.MappingForBlendingInfos = [.. w.Mappings.Select(m => new FrameSkeleton.MappingForBlendingInfo
        {
            Bounds = [.. m.Bounds.Select(ToBounds)],
            RefToUsageArray = [.. m.RefToUsage],
            UsageArray = [.. m.Usage],
        })];
    }
}
