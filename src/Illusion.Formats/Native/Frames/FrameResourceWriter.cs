using System.Numerics;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;
using Illusion.Formats.Mathematics;
using Wire = Illusion.Formats.Native.Model;

namespace Illusion.Formats.Native.Frames;

/// <summary>
/// Maps the editable managed document to the FrameResource wire image, reproducing the managed
/// writer's flow: the caller has already run UpdateFrameData, objects emit in dictionary order,
/// per-object save fixups run first, and FrameObjectFrame serializes with an identity transform.
/// </summary>
internal static class FrameResourceWriter
{
    internal static Wire.FrameModel ToWire(FrameResource source)
    {
        var wire = new Wire.FrameModel();
        FrameHeader header = source.Header;

        wire.Header.IsScene = (byte)(header.IsScene ? 1 : 0);
        if (header.IsScene)
        {
            wire.Header.Unk1 = header.Unk1;
            wire.Header.Unk2 = header.Unk2;
            wire.Header.SceneName = ToWireName(header.SceneName);
            wire.Header.UnkFloats = [.. header.UnkFloats];
            wire.Header.Unk3 = (byte)(header.Unk3 ? 1 : 0);
        }

        foreach (FrameHeaderScene scene in header.SceneFolders)
        {
            wire.SceneFolders.Add(new Wire.SceneFolderW { Name = ToWireName(scene.Name) });
        }
        foreach (FrameGeometry geometry in source.FrameGeometries.Values)
        {
            wire.Geometries.Add(MapGeometry(geometry));
        }
        foreach (FrameMaterial material in source.FrameMaterials.Values)
        {
            wire.Materials.Add(MapMaterial(material));
        }
        foreach (FrameBlendInfo blend in source.FrameBlendInfos.Values)
        {
            wire.BlendInfos.Add(MapBlendInfo(blend));
        }
        foreach (FrameSkeleton skeleton in source.FrameSkeletons.Values)
        {
            wire.Skeletons.Add(MapSkeleton(skeleton));
        }
        foreach (FrameSkeletonHierarchy hierarchy in source.FrameSkeletonHierachies.Values)
        {
            wire.Hierarchies.Add(new Wire.FrameHierarchyW
            {
                ParentIndices = [.. hierarchy.ParentIndices],
                UnkNum = hierarchy.Unk01,
                LastChildIndices = [.. hierarchy.LastChildIndices],
                UnkData = [.. hierarchy.UnkData],
            });
        }

        foreach (object value in source.FrameObjects.Values)
        {
            var entry = (FrameObjectBase)value;
            entry.SanitizeForSave();
            wire.Objects.Add(MapObject(wire, entry));
        }

        return wire;
    }

    private static Wire.FrameObjectRefW MapObject(Wire.FrameModel wire, FrameObjectBase entry)
    {
        switch (entry)
        {
            case FrameObjectModel model: // before SingleMesh — Model derives from it
            {
                var w = new Wire.FrameModelObjectW();
                MapSingleMeshFields(model, w.Base = MapBase(model), out int flags, out Wire.BBoxW bounds,
                    out byte deform, out Wire.HashNameW om);
                w.Nodes = MapJointNodes(model);
                w.Flags = flags;
                w.Bounds = bounds;
                w.DeformPartIndex = deform;
                w.MeshIndex = model.MeshIndex;
                w.MaterialIndex = model.MaterialIndex;
                w.OmTexture = om;
                w.Unk181 = model.Unk_18_1;
                w.Unk182 = model.Unk_18_2;
                w.Unk183 = model.Unk_18_3;
                w.BlendInfoIndex = model.BlendInfoIndex;
                w.SkeletonIndex = model.SkeletonIndex;
                w.SkeletonHierarchyIndex = model.SkeletonHierarchyIndex;
                w.RestTransforms = [.. model.RestTransform.Select(ToWireMatrix)];
                w.UnkTransform = ToWireMatrix(model.UnkTransform);
                w.Attachments = [.. model.AttachmentReferences.Select(a => new Wire.ModelAttachmentW
                {
                    AttachmentIndex = a.AttachmentIndex,
                    JointIndex = a.JointIndex,
                })];
                w.UnkFlags = model.UnkFlags;
                (int physSplitSize, int hitBoxSize, short _) = model.SplitCounters;
                w.PhysSplitSize = physSplitSize;
                w.HitBoxSize = hitBoxSize;
                w.Splits = [.. model.BlendMeshSplits.Select(s => new Wire.WeightedSplitW
                {
                    BlendIndex = s.BlendIndex,
                    Data = [.. s.Data.Select(d => new Wire.BlendMeshSplitW
                    {
                        Bursts = [.. d.Data.Select(b => new Wire.MiniMaterialBurstW
                        {
                            MaterialIndex = b.MaterialIndex,
                            Bursts = [.. b.Data.Select(f => new Wire.FacesBurstW
                            {
                                StartIndex = f.StartIndex,
                                NumFaces = f.NumFaces,
                            })],
                        })],
                    })],
                })];
                w.HitBoxes = [.. model.HitBoxes.Select(h => new Wire.HitBoxW
                {
                    Unk = h.Unk,
                    Px = h.Position.S1, Py = h.Position.S2, Pz = h.Position.S3,
                    Sx = h.Size.S1, Sy = h.Size.S2, Sz = h.Size.S3,
                })];
                wire.Models.Add(w);
                return Ref(FrameResourceObjectType.Model, wire.Models.Count - 1);
            }
            case FrameObjectSingleMesh mesh:
            {
                var w = new Wire.FrameSingleMeshW();
                MapSingleMeshFields(mesh, w.Base = MapBase(mesh), out int flags, out Wire.BBoxW bounds,
                    out byte deform, out Wire.HashNameW om);
                w.Nodes = MapJointNodes(mesh);
                w.Flags = flags;
                w.Bounds = bounds;
                w.DeformPartIndex = deform;
                w.MeshIndex = mesh.MeshIndex;
                w.MaterialIndex = mesh.MaterialIndex;
                w.OmTexture = om;
                w.Unk181 = mesh.Unk_18_1;
                w.Unk182 = mesh.Unk_18_2;
                w.Unk183 = mesh.Unk_18_3;
                wire.SingleMeshes.Add(w);
                return Ref(FrameResourceObjectType.SingleMesh, wire.SingleMeshes.Count - 1);
            }
            case FrameObjectFrame frame:
            {
                // The managed writer strips the transform from linked actors (the original
                // pipeline removed it); the wire carries identity without touching the object.
                var w = new Wire.FrameFrameW
                {
                    Base = MapBase(frame),
                    Nodes = MapJointNodes(frame),
                    ActorHash = ToWireName(frame.ActorHash),
                };
                w.Base.Transform = IdentityWire();
                wire.Frames.Add(w);
                return Ref(FrameResourceObjectType.Frame, wire.Frames.Count - 1);
            }
            case FrameObjectLight light:
                wire.Lights.Add(MapLight(light));
                return Ref(FrameResourceObjectType.Light, wire.Lights.Count - 1);
            case FrameObjectCamera camera:
            {
                var w = new Wire.FrameCameraW
                {
                    Base = MapBase(camera),
                    Nodes = MapJointNodes(camera),
                    Lens = [.. (camera.Lens ?? []).Select(l => new Wire.CameraLensW
                    {
                        Floats = [.. l.UnkFloats],
                        Hash = ToWireName(l.UnkHash),
                    })],
                };
                wire.Cameras.Add(w);
                return Ref(FrameResourceObjectType.Camera, wire.Cameras.Count - 1);
            }
            case FrameObjectComponent_U005 component:
                wire.ComponentsU005.Add(new Wire.FrameComponentU005W
                {
                    Base = MapBase(component),
                    Unk01 = component.Unk01,
                });
                return Ref(FrameResourceObjectType.Component_U00000005, wire.ComponentsU005.Count - 1);
            case FrameObjectSector sector:
                wire.Sectors.Add(new Wire.FrameSectorW
                {
                    Base = MapBase(sector),
                    Nodes = MapJointNodes(sector),
                    Unk08 = sector.Unk08,
                    Planes = ToWirePlanes(sector.Planes),
                    Bounds = ToWireBounds(sector.Bounds),
                    Unk13 = sector.Unk13,
                    Unk14 = sector.Unk14,
                    SectorName = ToWireName(sector.SectorName),
                });
                return Ref(FrameResourceObjectType.Sector, wire.Sectors.Count - 1);
            case FrameObjectDummy dummy:
                wire.Dummies.Add(new Wire.FrameDummyW
                {
                    Base = MapBase(dummy),
                    Nodes = MapJointNodes(dummy),
                    Bounds = ToWireBounds(dummy.Bounds),
                });
                return Ref(FrameResourceObjectType.Dummy, wire.Dummies.Count - 1);
            case FrameObjectDeflector deflector:
                wire.Deflectors.Add(new Wire.FrameDeflectorW
                {
                    Base = MapBase(deflector),
                    Nodes = MapJointNodes(deflector),
                });
                return Ref(FrameResourceObjectType.ParticleDeflector, wire.Deflectors.Count - 1);
            case FrameObjectArea area:
                wire.Areas.Add(new Wire.FrameAreaW
                {
                    Base = MapBase(area),
                    Nodes = MapJointNodes(area),
                    Unk01 = area.Unk01,
                    Planes = ToWirePlanes(area.Planes),
                    Bounds = ToWireBounds(area.Bounds),
                });
                return Ref(FrameResourceObjectType.Area, wire.Areas.Count - 1);
            case FrameObjectTarget target:
                wire.Targets.Add(new Wire.FrameTargetW
                {
                    Base = MapBase(target),
                    Nodes = MapJointNodes(target),
                    Unk01 = target.Unk01,
                    Unk02 = target.Unk02,
                });
                return Ref(FrameResourceObjectType.Target, wire.Targets.Count - 1);
            case FrameObjectCollision collision:
                wire.Collisions.Add(new Wire.FrameCollisionW
                {
                    Base = MapBase(collision),
                    Hash = collision.Hash,
                });
                return Ref(FrameResourceObjectType.Collision, wire.Collisions.Count - 1);
            case FrameObjectPoint point:
                wire.Points.Add(new Wire.FramePointW
                {
                    Base = MapBase(point),
                    Nodes = MapJointNodes(point),
                });
                return Ref(FrameResourceObjectType.Point, wire.Points.Count - 1);
            default:
                throw new FormatException($"unmapped frame object type {entry.GetType().Name}");
        }
    }

    private static Wire.FrameObjectRefW Ref(FrameResourceObjectType kind, int slot) =>
        new() { Kind = (int)kind, Slot = (uint)slot };

    // ── field mapping ──

    private static Wire.HashNameW ToWireName(HashName name) =>
        new() { Hash = name?.Hash ?? 0, Name = name?.String ?? "" };

    internal static Wire.Mat34 ToWireMatrix(Matrix4x4 matrix)
    {
        var wire = new Wire.Mat34();
        for (int column = 0; column < 3; column++)
        {
            Vector4 v = matrix.GetColumn(column);
            wire.Values.Add(v.X);
            wire.Values.Add(v.Y);
            wire.Values.Add(v.Z);
            wire.Values.Add(v.W);
        }
        return wire;
    }

    private static Wire.Mat34 IdentityWire()
    {
        var wire = new Wire.Mat34();
        wire.Values.AddRange([1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f]);
        return wire;
    }

    private static Wire.BBoxW ToWireBounds(BoundingBox bounds) =>
        new() { Min = bounds.Min, Max = bounds.Max };

    private static Wire.FrameBaseW MapBase(FrameObjectBase entry) => new()
    {
        Name = ToWireName(entry.Name),
        SecondaryFlags = entry.SecondaryFlags,
        Transform = ToWireMatrix(entry.LocalTransform),
        Unk3 = entry.Unk3,
        ParentIndex1 = entry.ParentIndex1.Index,
        ParentIndex2 = entry.ParentIndex2.Index,
        Unk6 = entry.Unk6,
    };

    private static List<Wire.JointNode> MapJointNodes(FrameObjectJoint joint) =>
        [.. (joint.Data ?? []).Select(n => new Wire.JointNode
        {
            Unk1 = n.Unk_01,
            Unk2 = ToWireName(n.Unk_02_Hash),
            Unk3 = ToWireName(n.Unk_03_Hash),
        })];

    private static void MapSingleMeshFields(FrameObjectSingleMesh mesh, Wire.FrameBaseW _,
        out int flags, out Wire.BBoxW bounds, out byte deform, out Wire.HashNameW omTexture)
    {
        flags = (int)mesh.SingleMeshFlags;
        bounds = ToWireBounds(mesh.Boundings);
        deform = mesh.DeformPartIndex;
        omTexture = ToWireName(mesh.OMTextureHash);
    }

    private static Wire.FrameLightW MapLight(FrameObjectLight light) => new()
    {
        Base = MapBase(light),
        Nodes = MapJointNodes(light),
        Flags = light.Flags,
        F0 = light.LUnk0, F1 = light.LUnk1, F2 = light.LUnk2, F3 = light.LUnk3,
        F4 = light.LUnk4, F5 = light.LUnk5, F6 = light.LUnk6,
        UnkInt1 = light.UnkInt1,
        V0 = light.UnkVector_0,
        F7 = light.LUnk7, F8 = light.LUnk8,
        B1 = light.UnkByte1,
        F9 = light.LUnk9, F10 = light.LUnk10, F11 = light.LUnk11, F12 = light.LUnk12,
        V1 = light.UnkVector_1,
        V2 = light.UnkVector_2,
        F13 = light.LUnk13,
        V3 = light.UnkVector_3,
        F14 = light.LUnk14, F15 = light.LUnk15, F16 = light.LUnk16,
        B2 = light.UnkByte2,
        F17 = light.LUnk17, F18 = light.LUnk18, F19 = light.LUnk19, F20 = light.LUnk20,
        F21 = light.LUnk21,
        ProjectionTexture = ToWireName(light.ProjectionTexture),
        UnkInt2 = light.UnkInt2,
        F22 = light.LUnk22, F23 = light.LUnk23,
        V4 = light.UnkVector_4,
        F24 = light.LUnk24, F25 = light.LUnk25, F26 = light.LUnk26, F27 = light.LUnk27,
        F28 = light.LUnk28,
        V5 = light.UnkVector_5,
        F29 = light.LUnk29, F30 = light.LUnk30, F31 = light.LUnk31, F32 = light.LUnk32,
        F33 = light.LUnk33, F34 = light.LUnk34, F35 = light.LUnk35,
        Texture0 = ToWireName(light.TextureHashes[0]),
        Texture1 = ToWireName(light.TextureHashes[1]),
        Texture2 = ToWireName(light.TextureHashes[2]),
        Texture3 = ToWireName(light.TextureHashes[3]),
        UnkBox = ToWireBounds(light.UnkBox),
        B3 = light.UnkByte3,
        UnkMatrix = ToWireMatrix(light.UnknownMatrix),
    };

    private static Wire.FrameGeometryW MapGeometry(FrameGeometry geometry)
    {
        var wire = new Wire.FrameGeometryW
        {
            Unk01 = geometry.Unk01,
            DecompressionOffset = geometry.DecompressionOffset,
            DecompressionFactor = geometry.DecompressionFactor,
        };
        foreach (FrameLOD lod in geometry.LOD)
        {
            wire.Lods.Add(new Wire.FrameLodW
            {
                Distance = lod.Distance,
                IndexBuffer = ToWireName(lod.IndexBufferRef),
                VertexDecl = (uint)lod.VertexDeclaration,
                VertexBuffer = ToWireName(lod.VertexBufferRef),
                NumVerts = lod.NumVerts,
                NZero1 = lod.NZero1,
                OpcodeCapsule = lod.OpcodeCapsule,
                SplitCapsule = lod.SplitCapsule,
            });
        }
        return wire;
    }

    private static Wire.FrameMaterialW MapMaterial(FrameMaterial material)
    {
        var wire = new Wire.FrameMaterialW { Bounds = ToWireBounds(material.Bounds) };
        foreach (MaterialStruct[] faces in material.Materials)
        {
            var lod = new Wire.MaterialLodW();
            foreach (MaterialStruct face in faces)
            {
                lod.Faces.Add(new Wire.MaterialFaceW
                {
                    NumFaces = face.NumFaces,
                    StartIndex = face.StartIndex,
                    MaterialHash = face.MaterialHash,
                    Unk3 = face.Unk3,
                });
            }
            wire.Lods.Add(lod);
        }
        return wire;
    }

    private static Wire.FrameBlendInfoW MapBlendInfo(FrameBlendInfo blend)
    {
        var wire = new Wire.FrameBlendInfoW { Bounds = ToWireBounds(blend.Bound) };
        foreach (FrameBlendInfo.BoneTransform transform in blend.BoneTransforms)
        {
            wire.BoneTransforms.Add(new Wire.BoneTransformW
            {
                Transform = ToWireMatrix(transform.Transform),
                Bounds = ToWireBounds(transform.Bounds),
                IsValid = transform.IsValid,
            });
        }
        foreach (FrameBlendInfo.BoneIndexInfo lod in blend.BoneIndexInfos)
        {
            wire.Lods.Add(new Wire.BoneIndexInfoW
            {
                BonesPerRemapPool = [.. lod.BonesPerRemapPool],
                BoneRemapIds = [.. lod.BoneRemapIDs],
                Materials = [.. lod.SkinnedMaterialInfo.Select(m => new Wire.SkinnedMatInfoW
                {
                    AssignedPoolIndex = m.AssignedPoolIndex,
                    NumWeights = m.NumWeightsPerVertex,
                })],
            });
        }
        return wire;
    }

    private static Wire.FrameSkeletonW MapSkeleton(FrameSkeleton skeleton) => new()
    {
        NumBones0 = skeleton.NumBones[0],
        NumBones1 = skeleton.NumBones[1],
        NumBones2 = skeleton.NumBones[2],
        NumBones3 = skeleton.NumBones[3],
        NumBlendIds = skeleton.NumBlendIDs,
        LodRemapCounts = [.. skeleton.LodRemapIDCount],
        IdType = skeleton.IDType,
        BoneNames = [.. skeleton.BoneNames.Select(ToWireName)],
        JointTransforms = [.. skeleton.JointTransforms.Select(ToWireMatrix)],
        BoneLodUsage = [.. skeleton.BoneLODUsage],
        WorldTransforms = [.. skeleton.WorldTransforms.Select(ToWireMatrix)],
        Mappings = [.. skeleton.MappingForBlendingInfos.Select(m => new Wire.SkeletonMappingW
        {
            Bounds = [.. m.Bounds.Select(b => new Wire.BBoxW { Min = b.Min, Max = b.Max })],
            RefToUsage = [.. m.RefToUsageArray],
            Usage = [.. m.UsageArray],
        })],
    };

    private static List<float> ToWirePlanes(Vector4[] planes)
    {
        var result = new List<float>(planes.Length * 4);
        foreach (Vector4 plane in planes)
        {
            result.Add(plane.X);
            result.Add(plane.Y);
            result.Add(plane.Z);
            result.Add(plane.W);
        }
        return result;
    }
}
