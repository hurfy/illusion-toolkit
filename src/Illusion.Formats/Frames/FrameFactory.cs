using System.Diagnostics;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Formats.Frames;

public class FrameFactory
{
    public static FrameObjectBase ConstructFrameByObjectID(FrameResource OwningResource, FrameResourceObjectType FrameType)
    {
        switch (FrameType)
        {
            case FrameResourceObjectType.Point:
                return OwningResource.ConstructFrameAssetOfType<FrameObjectPoint>();
            case FrameResourceObjectType.SingleMesh:
                return OwningResource.ConstructFrameAssetOfType<FrameObjectSingleMesh>();
            case FrameResourceObjectType.Frame:
                return OwningResource.ConstructFrameAssetOfType<FrameObjectFrame>();
            case FrameResourceObjectType.Light:
                return OwningResource.ConstructFrameAssetOfType<FrameObjectLight>();
            case FrameResourceObjectType.Camera:
                return OwningResource.ConstructFrameAssetOfType<FrameObjectCamera>();
            case FrameResourceObjectType.Component_U00000005:
                return OwningResource.ConstructFrameAssetOfType<FrameObjectComponent_U005>();
            case FrameResourceObjectType.Sector:
                return OwningResource.ConstructFrameAssetOfType<FrameObjectSector>();
            case FrameResourceObjectType.Dummy:
                return OwningResource.ConstructFrameAssetOfType<FrameObjectDummy>();
            case FrameResourceObjectType.ParticleDeflector:
                return OwningResource.ConstructFrameAssetOfType<FrameObjectDeflector>();
            case FrameResourceObjectType.Area:
                return OwningResource.ConstructFrameAssetOfType<FrameObjectArea>();
            case FrameResourceObjectType.Target:
                return OwningResource.ConstructFrameAssetOfType<FrameObjectTarget>();
            case FrameResourceObjectType.Model:
                return OwningResource.ConstructFrameAssetOfType<FrameObjectModel>();
            case FrameResourceObjectType.Collision:
                return OwningResource.ConstructFrameAssetOfType<FrameObjectCollision>();
            default:
                Debug.WriteLine("Missing frame type!");
                return null!;
        }
    }

    // ConstructFrameByObjectType(MT_ObjectType, ...) removed in vendored copy
    // (it belonged to the model-import/export path we do not vendor).
}
