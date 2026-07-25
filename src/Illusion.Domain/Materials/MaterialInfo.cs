namespace Illusion.Domain.Materials;

/// <summary>
/// One material assigned to a mesh (an engine-neutral snapshot for display). Comes from a mesh's LOD0 material
/// table joined with the loaded MTL library: <see cref="Resolved"/> is false when the <see cref="Hash"/> is not
/// in the library (then only the hash and face range are meaningful).
/// </summary>
public sealed record MaterialInfo(
    string? Name,
    ulong Hash,
    int StartIndex,
    int TriangleCount,
    bool Resolved,
    IReadOnlyList<string> Flags,
    ulong ShaderId,
    uint ShaderHash,
    IReadOnlyList<MaterialSlotInfo> TextureSlots,
    IReadOnlyList<MaterialParamInfo> Parameters);

/// <summary>One texture slot of a material — the slot code (e.g. "S000"), its friendly name (e.g. "DiffuseTexture")
/// and the texture it binds (name + hash; <see cref="TextureName"/> null when the slot binds nothing).</summary>
public sealed record MaterialSlotInfo(string SlotId, string FriendlyName, string? TextureName, ulong TextureHash);

/// <summary>One shader parameter of a material — the code (e.g. "C030"), its friendly name and its float payload.</summary>
public sealed record MaterialParamInfo(string ParamId, string FriendlyName, IReadOnlyList<float> Values);
