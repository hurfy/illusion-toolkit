using Illusion.Formats.ItemDesc;

namespace Illusion.Formats.Native.Misc;

/// <summary>Maps the .ids wire model onto the editable managed objects and back. The wire
/// carries the rigid-body tree flattened in preorder (each node with its child count).</summary>
internal static class NativeItemDesc
{
    internal static ItemDescFile Read(ReadOnlySpan<byte> bytes)
    {
        Model.IdsFileW wire = NativeMisc.Load(bytes, "mf_ids_load", Model.IdsFileW.ReadFrom);
        var file = new ItemDescFile
        {
            Hash = wire.Hash,
            Type = (ItemDescType)wire.Type,
            SubType = wire.SubType,
        };
        if (wire.Kind == 1)
        {
            file.Element = new SimulationSceneElement
            {
                DataHash = wire.SimScene.DataHash,
                Unk1 = (ushort)wire.SimScene.Unk1,
                Unk2 = (ushort)wire.SimScene.Unk2,
                Unk3 = (ushort)wire.SimScene.Unk3,
                Unk4 = wire.SimScene.Unk4,
                Unk5 = wire.SimScene.Unk5,
                Unk6 = wire.SimScene.Unk6,
                Unk7 = wire.SimScene.Unk7,
                Unk8 = wire.SimScene.Unk8,
                BoundsMin = wire.SimScene.BoundsMin,
                BoundsMax = wire.SimScene.BoundsMax,
                Unk9 = wire.SimScene.Unk9,
                Unk10 = wire.SimScene.Unk10,
                Unk11 = wire.SimScene.Unk11,
                SimulationBoundsMin = wire.SimScene.SimulationBoundsMin,
                SimulationBoundsMax = wire.SimScene.SimulationBoundsMax,
            };
        }
        else if (wire.Kind == 2)
        {
            int cursor = 0;
            file.Element = ReadNode(wire.RigidNodes, ref cursor);
        }
        else
        {
            file.Element = new OpaqueElement { Body = wire.OpaqueBody };
        }
        return file;
    }

    internal static byte[] ToBytes(ItemDescFile file)
    {
        var wire = new Model.IdsFileW
        {
            Hash = file.Hash,
            Type = (byte)file.Type,
            SubType = file.SubType,
        };
        switch (file.Element)
        {
            case SimulationSceneElement sim:
                wire.Kind = 1;
                wire.SimScene = new Model.IdsSimSceneW
                {
                    DataHash = sim.DataHash,
                    Unk1 = sim.Unk1,
                    Unk2 = sim.Unk2,
                    Unk3 = sim.Unk3,
                    Unk4 = sim.Unk4,
                    Unk5 = sim.Unk5,
                    Unk6 = sim.Unk6,
                    Unk7 = sim.Unk7,
                    Unk8 = sim.Unk8,
                    BoundsMin = sim.BoundsMin,
                    BoundsMax = sim.BoundsMax,
                    Unk9 = sim.Unk9,
                    Unk10 = sim.Unk10,
                    Unk11 = sim.Unk11,
                    SimulationBoundsMin = sim.SimulationBoundsMin,
                    SimulationBoundsMax = sim.SimulationBoundsMax,
                };
                break;
            case RigidBodyElement rigid:
                wire.Kind = 2;
                AppendNode(wire.RigidNodes, rigid);
                break;
            case OpaqueElement opaque:
                wire.Kind = 0;
                wire.OpaqueBody = opaque.Body;
                break;
            case null:
                wire.Kind = 0;
                break;
        }
        return NativeMisc.Save(wire.WriteTo, "mf_ids_save");
    }

    private static RigidBodyElement ReadNode(List<Model.IdsRigidBodyW> nodes, ref int cursor)
    {
        Model.IdsRigidBodyW node = nodes[cursor++];
        var element = new RigidBodyElement
        {
            Shape = (RigidBodyShape)node.Shape,
            DataHash = node.DataHash,
            MaterialId = (ushort)node.MaterialId,
            Transform = [.. node.Transform],
            Layer = (sbyte)node.Layer,
            BoxDimensions = node.BoxDimensions,
            Radius = node.Radius,
            Height = node.Height,
        };
        if (element.Shape is RigidBodyShape.TriangleMesh or RigidBodyShape.ConvexPolyhedron)
        {
            element.CookedMesh = node.CookedMesh;
        }
        foreach (Model.IdsMaterialInfoW info in node.MaterialInfos)
        {
            element.MaterialInfos.Add(((ushort)info.StartTriangleIndex, info.NumTriangles));
        }
        for (uint i = 0; i < node.NumChildren; i++)
        {
            element.Elements.Add(ReadNode(nodes, ref cursor));
        }
        return element;
    }

    private static void AppendNode(List<Model.IdsRigidBodyW> nodes, RigidBodyElement element)
    {
        var node = new Model.IdsRigidBodyW
        {
            Shape = (uint)element.Shape,
            DataHash = element.DataHash,
            MaterialId = element.MaterialId,
            Layer = element.Layer,
            BoxDimensions = element.BoxDimensions,
            Radius = element.Radius,
            Height = element.Height,
            CookedMesh = element.CookedMesh ?? [],
            NumChildren = (uint)element.Elements.Count,
        };
        node.Transform.AddRange(element.Transform);
        foreach ((ushort start, uint count) in element.MaterialInfos)
        {
            node.MaterialInfos.Add(new Model.IdsMaterialInfoW
            {
                StartTriangleIndex = start,
                NumTriangles = count,
            });
        }
        nodes.Add(node);
        foreach (RigidBodyElement child in element.Elements)
        {
            AppendNode(nodes, child);
        }
    }
}
