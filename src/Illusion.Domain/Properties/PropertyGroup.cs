namespace Illusion.Domain.Properties;

/// <summary>
/// A titled bundle of <see cref="PropertyDescriptor"/>s. <see cref="IsTypeSpecific"/> routes it between the common
/// object tab and the per-type tab; <see cref="IsUnknown"/> marks the reverse-engineering catch-all (unmapped
/// fields) a UI can collapse by default.
/// </summary>
public sealed class PropertyGroup
{
    /// <summary>Section heading (e.g. "Identity", "Mesh", "Unknown").</summary>
    public required string Title { get; init; }

    /// <summary>True for the unmapped-field catch-all — a UI renders it collapsed.</summary>
    public bool IsUnknown { get; init; }

    /// <summary>False → belongs on the shared object tab (properties every frame object has);
    /// true → belongs on the per-type tab (properties specific to the selected object's type).</summary>
    public bool IsTypeSpecific { get; init; }

    public required IReadOnlyList<PropertyDescriptor> Properties { get; init; }
}
