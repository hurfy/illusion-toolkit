using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Illusion.Domain.Properties;
using Illusion.ViewModels;

namespace Illusion.Views;

/// <summary>
/// Renders a list of <see cref="PropertyGroupViewModel"/> as cards of editable rows — one row template per
/// <see cref="PropertyKind"/>, chosen by <see cref="PropertyRowTemplateSelector"/>. Hosted inside the Object and
/// per-type property tabs. Enter commits a focused text field and returns keyboard focus (so the fly camera's
/// WASD works again), mirroring <see cref="Vector3Box"/>.
/// </summary>
public partial class PropertyList : UserControl
{
    public PropertyList()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
    }

    /// <summary>The groups to render (a list of <see cref="PropertyGroupViewModel"/>).</summary>
    public static readonly DependencyProperty GroupsProperty = DependencyProperty.Register(
        nameof(Groups), typeof(IEnumerable), typeof(PropertyList), new PropertyMetadata(null));

    public IEnumerable? Groups
    {
        get => (IEnumerable?)GetValue(GroupsProperty);
        set => SetValue(GroupsProperty, value);
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.OriginalSource is not TextBox tb) return;
        tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
        e.Handled = true;
    }
}

/// <summary>Picks a group card template: a plain caption+rows card, or a collapsed expander for the Unknown group.</summary>
public sealed class PropertyGroupTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Known { get; set; }
    public DataTemplate? Unknown { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
        item is PropertyGroupViewModel { IsUnknown: true } ? Unknown : Known;
}

/// <summary>Picks a row editor template per <see cref="PropertyKind"/> and read-only state.</summary>
public sealed class PropertyRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ReadOnly { get; set; }
    public DataTemplate? Numeric { get; set; }
    public DataTemplate? Bool { get; set; }
    public DataTemplate? Hash { get; set; }
    public DataTemplate? HashReadOnly { get; set; }
    public DataTemplate? Vector { get; set; }
    public DataTemplate? Flags { get; set; }
    public DataTemplate? FlagsReadOnly { get; set; }
    public DataTemplate? Matrix { get; set; }
    public DataTemplate? StructList { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not PropertyRowViewModel r) return null;
        return r.Kind switch
        {
            PropertyKind.Vector3 => Vector,
            PropertyKind.Matrix => Matrix,
            PropertyKind.StructList => StructList,
            PropertyKind.Flags => r.IsReadOnly ? FlagsReadOnly : Flags,
            PropertyKind.HashName => r.IsReadOnly ? HashReadOnly : Hash,
            PropertyKind.Bool => r.IsReadOnly ? ReadOnly : Bool,
            _ => r.IsReadOnly ? ReadOnly : Numeric,
        };
    }
}
