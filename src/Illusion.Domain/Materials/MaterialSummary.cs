namespace Illusion.Domain.Materials;

/// <summary>Lightweight identity of one material inside an MTL library — for browsing long lists
/// (default.mtl alone carries ~7300 materials) without building a full <see cref="MaterialInfo"/> each.</summary>
public sealed record MaterialSummary(string Name, ulong Hash);
