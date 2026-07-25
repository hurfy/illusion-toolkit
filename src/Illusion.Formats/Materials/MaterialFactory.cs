using Illusion.Formats.Materials.Versions;

namespace Illusion.Formats.Materials;

public static class MaterialFactory
{
    public static IMaterial ConstructMaterial(MaterialVersion Version)
    {
        switch (Version)
        {
            case MaterialVersion.V_57:
                return new Material_v57();
            case MaterialVersion.V_58:
                return new Material_v58();
            default:
                throw new UnsupportedVersionException(
                    $"material version {(int)Version} is not supported (57 = Mafia II, 58 = Mafia II DE)");
        }
    }

    public static IMaterial ConvertMaterial(MaterialVersion Version, IMaterial OtherMaterial)
    {
        switch (Version)
        {
            case MaterialVersion.V_57:
                return new Material_v57(OtherMaterial);
            case MaterialVersion.V_58:
                return new Material_v58(OtherMaterial);
            default:
                throw new UnsupportedVersionException(
                    $"material version {(int)Version} is not supported (57 = Mafia II, 58 = Mafia II DE)");
        }
    }
}
