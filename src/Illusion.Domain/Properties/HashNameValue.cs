namespace Illusion.Domain.Properties;

/// <summary>
/// Transport value for <see cref="PropertyKind.HashName"/>: the FNV64 <see cref="Hash"/> and the source
/// <see cref="Name"/> it was derived from. A UI shows the name for editing and the hash read-only; committing a
/// new name lets the adapter re-derive the hash.
/// </summary>
public readonly record struct HashNameValue(ulong Hash, string Name);
