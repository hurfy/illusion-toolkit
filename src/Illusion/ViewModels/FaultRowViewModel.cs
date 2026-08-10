using Illusion.Assets.Cars;

namespace Illusion.ViewModels;

/// <summary>
/// One line of the diagnosis a car opens with: something the aggregate could not stitch, named rather than
/// dropped.
///
/// <para>
/// The list exists because the alternative is finding the damage one component at a time — and a fault that
/// has no component to sit on (a door row naming a bone nothing claims) would have nowhere to be seen at all.
/// Where there IS a component, the row leads to it, so reading the diagnosis and looking at what it is about
/// are the same gesture.
/// </para>
/// </summary>
public sealed class FaultRowViewModel
{
    internal FaultRowViewModel(CarFault fault, ComponentRowViewModel? component)
    {
        Fault = fault;
        Component = component;
    }

    /// <summary>The fault this row is.</summary>
    public CarFault Fault { get; }

    /// <summary>The row it belongs to, or null when the failure is about a prefab row rather than a
    /// component — the half of a disagreement that has no component at all.</summary>
    public ComponentRowViewModel? Component { get; }

    /// <summary>The failure in a few words.</summary>
    public string Title => Fault.Title;

    /// <summary>The one line a modder can act on — which part, which bone, which row.</summary>
    public string What => Fault.What;

    /// <summary>Whether clicking this row leads anywhere. A fault about a prefab row has no component, and so
    /// does one naming a component that has just stopped existing — which is what a lost-geometry fault says.
    /// </summary>
    public bool HasComponent => Component != null;

    /// <summary>Whether this is a failure no shipped car raises. Measured: 41 faults over 8 of the 85 shipped
    /// cars are things cars are simply written like, and colouring those as damage is how the whole strip
    /// stops being read.</summary>
    public bool IsBreak => !Fault.ShipsThisWay;

    public override string ToString() => Fault.ToString();
}
