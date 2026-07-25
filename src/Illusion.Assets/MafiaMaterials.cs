using System.Diagnostics;
using Illusion.Domain.Materials;
using Illusion.Formats.Hashing;
using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;

namespace Illusion.Assets;

/// <summary>
/// The app's Mafia II material cache (every .mtl in edit\materials) and material resolution by 64-bit
/// hash → texture names. The format layer's <see cref="MaterialCollection"/> is a plain instance; this
/// class owns the app-wide one and guards its one-time load.
/// </summary>
public static class MafiaMaterials
{
    private static readonly object Sync = new();
    private static volatile MaterialCollection? _materials;

    // Callers live on background loaders that can outlive their viewport (close-and-reopen), so the
    // first .mtl load must be mutually exclusive: the lock makes a second caller WAIT for the load
    // instead of racing it or seeing it half-done.
    public static void EnsureLoaded()
    {
        if (_materials != null) return;
        lock (Sync)
        {
            if (_materials != null) return;

            var materials = new MaterialCollection();

            // The .mtl libraries sit in the game root (…\Mafia II\edit\materials\), not in pc.
            string? root = MafiaEnvironment.GameRoot;
            string dir = root == null ? "" : Path.Combine(root, "edit", "materials");
            if (root != null && Directory.Exists(dir))
            {
                // Canonical vanilla libraries first so they keep winning duplicate-hash lookups
                // (FindByHash walks libraries in insertion order), then every other .mtl the folder
                // carries (mod libraries such as LostHeavenMap.mtl) in stable name order. Backups
                // don't need filtering here — saves put them in a backups\ subfolder.
                string[] canonical = { "default.mtl", "default50.mtl", "default60.mtl" };
                IEnumerable<string> paths = canonical
                    .Select(name => Path.Combine(dir, name))
                    .Where(File.Exists)
                    .Concat(Directory.EnumerateFiles(dir, "*.mtl", SearchOption.TopDirectoryOnly)
                        .Where(p => Path.GetExtension(p).Equals(".mtl", StringComparison.OrdinalIgnoreCase)
                                    && !canonical.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

                foreach (string path in paths)
                {
                    try
                    {
                        materials.LoadLibrary(path);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("MTL load failed " + path + ": " + ex.Message);
                    }
                }
            }

            _materials = materials; // published LAST so waiters never observe a half-loaded collection
        }
    }

    /// <summary>The texture .dds names of one material: diffuse (S000), normal (S001), specular-level (S002).
    /// Any slot the material doesn't define is null.</summary>
    public readonly record struct MaterialTextures(string? Diffuse, string? Normal, string? Specular);

    /// <summary>Resolves a material by hash to its diffuse/normal/specular texture names (all null if unknown).</summary>
    public static MaterialTextures GetMaterialTextures(ulong materialHash)
    {
        IMaterial? mat = _materials?.FindByHash(materialHash);
        if (mat == null) return default;

        return new MaterialTextures(
            Clean(mat.GetTextureByID("S000")),   // S000 = diffuse/albedo
            Clean(mat.GetTextureByID("S001")),   // S001 = tangent-space normal map
            Clean(mat.GetTextureByID("S002")));  // S002 = specular-level map
    }

    private static string? Clean(HashName? tex)
    {
        string? name = tex?.String;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>The material's display name from the loaded MTL libraries — the frame stream itself
    /// carries only the hash. Null when the hash is unknown.</summary>
    public static string? GetMaterialName(ulong materialHash)
    {
        string? name = _materials?.FindByHash(materialHash)?.GetMaterialName();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>Whether the hash exists in the loaded MTL libraries — guards bridge pushes against
    /// assigning a material the game does not have.</summary>
    public static bool KnowsMaterial(ulong materialHash) => _materials?.FindByHash(materialHash) != null;

    /// <summary>Resolves a material NAME (exact spelling) to its hash — how an import binds the material
    /// names it finds in a model file. Null when no loaded library carries the name.</summary>
    public static ulong? FindHashByName(string name)
    {
        EnsureLoaded();
        return _materials?.FindByName(name)?.GetMaterialHash();
    }

    /// <summary>The loaded collection, for the asset layer's own material machinery (the import's
    /// material creator); the UI never touches it.</summary>
    internal static MaterialCollection? Collection
    {
        get
        {
            EnsureLoaded();
            return _materials;
        }
    }

    /// <summary>Resolves a material hash to a full <see cref="MaterialInfo"/> for display — name, flags, shader ids,
    /// every texture slot and every shader parameter (with friendly names). <paramref name="startIndex"/> and
    /// <paramref name="triangleCount"/> come from the mesh's material assignment and are passed through. When the
    /// hash is not in the loaded libraries the result is <see cref="MaterialInfo.Resolved"/> = false with only the
    /// hash and face range populated.</summary>
    public static MaterialInfo GetMaterialInfo(ulong hash, int startIndex, int triangleCount)
    {
        EnsureLoaded();
        IMaterial? mat = _materials?.FindByHash(hash);
        if (mat == null)
        {
            return new MaterialInfo(null, hash, startIndex, triangleCount, false,
                Array.Empty<string>(), 0, 0, Array.Empty<MaterialSlotInfo>(), Array.Empty<MaterialParamInfo>());
        }

        var flags = new List<string>();
        foreach (MaterialFlags flag in Enum.GetValues<MaterialFlags>())
            if (flag != 0 && mat.Flags.HasFlag(flag)) flags.Add(flag.ToString());

        var slots = new List<MaterialSlotInfo>();
        foreach (IMaterialSampler s in EnumerateSamplers(mat))
            slots.Add(new MaterialSlotInfo(s.ID, MaterialParameterNames.GetName(s.ID), Clean2(s.GetFileName()), s.GetFileHash()));

        var parameters = new List<MaterialParamInfo>();
        foreach (MaterialParameter p in mat.Parameters)
            parameters.Add(new MaterialParamInfo(p.ID, MaterialParameterNames.GetName(p.ID), p.Paramaters ?? Array.Empty<float>()));

        string? name = mat.GetMaterialName();
        return new MaterialInfo(string.IsNullOrEmpty(name) ? null : name, hash, startIndex, triangleCount, true,
            flags, mat.ShaderID, mat.ShaderHash, slots, parameters);
    }

    // The all-samplers list lives on the concrete version type (base IMaterial has no slot enumeration).
    private static IEnumerable<IMaterialSampler> EnumerateSamplers(IMaterial mat)
    {
        if (mat is Material_v57 v57) foreach (MaterialSampler_v57 s in v57.Samplers) yield return s;
        else if (mat is Material_v58 v58) foreach (MaterialSampler_v58 s in v58.Samplers) yield return s;
    }

    private static string? Clean2(string? name) =>
        string.IsNullOrEmpty(name) || name == "Invalid" ? null : name;
}
