namespace Illusion.Domain.Materials;

/// <summary>A known sampler slot code and its friendly name (e.g. "S001" → "NormalTexture") — what the
/// material editor offers when adding a texture slot.</summary>
public sealed record SlotDescriptor(string Id, string FriendlyName)
{
    /// <summary>Display form for pickers: "S001 · NormalTexture" (just the id when no better name).</summary>
    public string Display => FriendlyName == Id ? Id : $"{Id} · {FriendlyName}";
}
