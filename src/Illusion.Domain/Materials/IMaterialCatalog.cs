namespace Illusion.Domain.Materials;

/// <summary>
/// The app's material catalog port: browse the loaded MTL libraries and mutate their materials
/// (texture slots, create, rename, delete) without the UI depending on a format backend. Mutations are
/// in-memory; <see cref="SaveDirty"/> persists every dirty library at once (the common Save flow).
/// Deletion hands back an opaque token so an undoable edit can restore the exact removed material.
/// </summary>
public interface IMaterialCatalog
{
    /// <summary>File names of the loaded MTL libraries (e.g. "default.mtl"), load order preserved.</summary>
    IReadOnlyList<string> Libraries { get; }

    /// <summary>All materials of one library (empty for an unknown library name).</summary>
    IReadOnlyList<MaterialSummary> GetMaterials(string library);

    /// <summary>Full display snapshot of one material, or null when the hash is not loaded.</summary>
    MaterialInfo? GetMaterial(ulong hash);

    /// <summary>The library file name the hash lives in, or null when not loaded.</summary>
    string? LibraryOf(ulong hash);

    /// <summary>The texture name a slot binds ("" = bound empty), or null when the material/slot is unknown.</summary>
    string? GetTexture(ulong hash, string slotId);

    /// <summary>Rebinds a texture slot (empty string clears it). False when the material/slot is unknown.</summary>
    bool SetTexture(ulong hash, string slotId, string textureName);

    /// <summary>Creates a default-preset material in <paramref name="library"/> and returns its hash;
    /// null when the name is empty/taken or the library is unknown.</summary>
    ulong? CreateMaterial(string library, string name);

    /// <summary>Renames a material — its hash re-derives from the new name — and returns the new hash;
    /// null when the hash is not loaded or the name is empty/taken in the loaded libraries. The entry
    /// keeps its dictionary position, so the library's save order stays byte-faithful.
    /// <paramref name="restoreHash"/> pins the exact resulting hash instead of deriving it (the undo
    /// path: vanilla libraries carry hashes that are not always the FNV64 of the name).</summary>
    ulong? RenameMaterial(ulong hash, string newName, ulong? restoreHash = null);

    /// <summary>Removes a material and returns an opaque token that restores it (see
    /// <see cref="RestoreMaterial"/>); null when the hash is not loaded.</summary>
    object? RemoveMaterial(ulong hash);

    /// <summary>Puts a removed material back (undo of remove / redo of create). False for a foreign token
    /// or when the hash meanwhile reappeared.</summary>
    bool RestoreMaterial(object token);

    /// <summary>Every sampler slot code the format knows (e.g. "S000" → "DiffuseTexture") — the choices
    /// offered when adding a texture slot a material does not carry yet.</summary>
    IReadOnlyList<SlotDescriptor> KnownSamplerSlots { get; }

    /// <summary>Adds an empty sampler slot to a material. False when the material is unknown or the slot
    /// already exists.</summary>
    bool AddSampler(ulong hash, string slotId);

    /// <summary>Removes a sampler slot and returns an opaque token that restores it (exact instance, at its
    /// original position); null when the material/slot is unknown.</summary>
    object? RemoveSampler(ulong hash, string slotId);

    /// <summary>Puts a removed sampler back (undo of remove). False for a foreign token or when the slot
    /// meanwhile reappeared.</summary>
    bool RestoreSampler(ulong hash, object token);

    /// <summary>Every known shader parameter code (the S-sampler codes excluded), with the canonical
    /// float count observed in the loaded libraries — the full editable set the editor offers.</summary>
    IReadOnlyList<ParamDescriptor> KnownParameters { get; }

    /// <summary>The float payload of one shader parameter, or null when the material/parameter is unknown.</summary>
    IReadOnlyList<float>? GetParameter(ulong hash, string paramId);

    /// <summary>Replaces a parameter's floats. The count must match the existing payload (the byte length
    /// is part of the format) — false otherwise, and for an unknown material/parameter.</summary>
    bool SetParameter(ulong hash, string paramId, IReadOnlyList<float> values);

    /// <summary>Adds a parameter the material does not carry yet. The float count must match the code's
    /// canonical length when the loaded libraries know it — false otherwise, for an empty payload, a
    /// duplicate, or an unknown material.</summary>
    bool AddParameter(ulong hash, string paramId, IReadOnlyList<float> values);

    /// <summary>Removes a parameter (the undo of <see cref="AddParameter"/>). False when the
    /// material/parameter is unknown.</summary>
    bool RemoveParameter(ulong hash, string paramId);

    /// <summary>Whether any library has in-memory edits not yet written to disk.</summary>
    bool HasUnsavedChanges { get; }

    /// <summary>Writes every dirty library back to its .mtl (timestamped backup + atomic replace).
    /// Returns null on success, else the first failure's reason; <paramref name="saved"/> counts the
    /// libraries actually written either way.</summary>
    string? SaveDirty(out int saved);
}
