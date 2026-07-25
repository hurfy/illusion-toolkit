using Illusion.Formats.Materials.Versions;

namespace Illusion.Formats.Materials;

/// <summary>
/// A set of loaded material libraries (.mtl) with hash/name lookup across all of them — the instance
/// replacement for the vendored static MaterialsManager. Owners decide lifetime and thread-safety;
/// nothing here is global.
/// </summary>
public sealed class MaterialCollection
{
    private readonly Dictionary<string, MaterialLibrary> _libraries = new();

    public IReadOnlyDictionary<string, MaterialLibrary> Libraries => _libraries;

    /// <summary>Loads one .mtl library and registers it under its file name.</summary>
    public MaterialLibrary LoadLibrary(string path)
    {
        var library = new MaterialLibrary(MaterialVersion.V_57); // version is re-read from the file
        library.ReadMatFile(path);
        _libraries[library.Name] = library;
        return library;
    }

    public IMaterial? FindByHash(ulong hash)
    {
        foreach (MaterialLibrary library in _libraries.Values)
        {
            if (library.Materials.TryGetValue(hash, out IMaterial? material))
            {
                return material;
            }
        }
        return null;
    }

    public IMaterial? FindByName(string name)
    {
        foreach (MaterialLibrary library in _libraries.Values)
        {
            foreach (IMaterial material in library.Materials.Values)
            {
                if (string.Equals(material.MaterialName.String, name, StringComparison.Ordinal))
                {
                    return material;
                }
            }
        }
        return null;
    }
}
