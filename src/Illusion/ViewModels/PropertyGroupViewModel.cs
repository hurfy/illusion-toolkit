using Illusion.Domain.Properties;

namespace Illusion.ViewModels;

/// <summary>A titled card of property rows. Unknown groups render collapsed behind an expander.</summary>
public sealed class PropertyGroupViewModel
{
    public PropertyGroupViewModel(PropertyGroup group, Action<PropertyDescriptor, object?, object?>? commit)
    {
        Title = group.Title;
        IsUnknown = group.IsUnknown;
        Rows = group.Properties.Select(d => new PropertyRowViewModel(d, commit)).ToList();
        HeaderText = group.IsUnknown
            ? $"{group.Title} — {Rows.Count} field{(Rows.Count == 1 ? "" : "s")}"
            : group.Title;
    }

    public string Title { get; }
    public bool IsUnknown { get; }

    /// <summary>Card caption / expander header ("Unknown — N fields" for the unmapped catch-all).</summary>
    public string HeaderText { get; }

    public IReadOnlyList<PropertyRowViewModel> Rows { get; }

    /// <summary>Re-reads every row's value in place (after undo/redo or an external edit).</summary>
    public void Refresh()
    {
        foreach (PropertyRowViewModel r in Rows) r.Refresh();
    }
}
