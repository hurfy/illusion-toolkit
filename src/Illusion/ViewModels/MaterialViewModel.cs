using System.Globalization;
using System.Windows.Media;
using Illusion.Domain.Materials;

namespace Illusion.ViewModels;

/// <summary>View of one <see cref="MaterialInfo"/> for the Materials tab tiles — display strings plus the
/// sphere thumbnail and the identity (hash + mesh slot index) a tile click hands to the material editor.
/// Rebuilt fresh on each selection change / material edit, so no change notification.</summary>
public sealed class MaterialViewModel
{
    public MaterialViewModel(MaterialInfo m, int slotIndex = 0, ImageSource? thumbnail = null)
    {
        Hash = m.Hash;
        SlotIndex = slotIndex;
        Thumbnail = thumbnail;
        Resolved = m.Resolved;
        Title = m.Resolved ? (string.IsNullOrEmpty(m.Name) ? "(unnamed material)" : m.Name!) : "Unresolved material";
        HashText = "0x" + m.Hash.ToString("X", CultureInfo.InvariantCulture);
        RangeText = $"{m.TriangleCount:N0} tris · index {m.StartIndex:N0}";

        HasFlags = m.Flags.Count > 0;
        FlagsText = string.Join(" · ", m.Flags);

        HasShader = m.Resolved;
        ShaderText = $"id 0x{m.ShaderId:X} · hash 0x{m.ShaderHash:X}";

        Slots = m.TextureSlots.Select(s => new MaterialSlotViewModel(s)).ToList();
        HasSlots = Slots.Count > 0;

        Parameters = m.Parameters.Select(p => new MaterialParamViewModel(p)).ToList();
        HasParameters = Parameters.Count > 0;
        ParametersHeader = $"Parameters ({Parameters.Count})";
    }

    public ulong Hash { get; }
    public int SlotIndex { get; }
    public ImageSource? Thumbnail { get; }
    public bool HasThumbnail => Thumbnail != null;

    public bool Resolved { get; }
    public string Title { get; }
    public string HashText { get; }
    public string RangeText { get; }

    public bool HasFlags { get; }
    public string FlagsText { get; }

    public bool HasShader { get; }
    public string ShaderText { get; }

    public IReadOnlyList<MaterialSlotViewModel> Slots { get; }
    public bool HasSlots { get; }

    public IReadOnlyList<MaterialParamViewModel> Parameters { get; }
    public bool HasParameters { get; }
    public string ParametersHeader { get; }
}

/// <summary>One texture slot row: friendly name (with the raw slot code) → bound texture file.</summary>
public sealed class MaterialSlotViewModel
{
    public MaterialSlotViewModel(MaterialSlotInfo s)
    {
        FriendlyName = s.FriendlyName;
        SlotCode = s.FriendlyName == s.SlotId ? "" : s.SlotId; // hide the code when it IS the label (unknown slot)
        TextureText = s.TextureName ?? "—";
    }

    public string FriendlyName { get; }
    public string SlotCode { get; }
    public string TextureText { get; }
}

/// <summary>One shader-parameter row: friendly name (with the raw code) → its float values.</summary>
public sealed class MaterialParamViewModel
{
    public MaterialParamViewModel(MaterialParamInfo p)
    {
        FriendlyName = p.FriendlyName;
        ParamCode = p.FriendlyName == p.ParamId ? "" : p.ParamId;
        ValuesText = string.Join(", ", p.Values.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
    }

    public string FriendlyName { get; }
    public string ParamCode { get; }
    public string ValuesText { get; }
}
