namespace Illusion.Scene;

/// <summary>
/// What a scene opens with its eye already unticked.
///
/// <para>
/// A car's archive holds more than the car. Around the body sit particle-emitter shells — the volume rain
/// falls in, the one that sits over the bonnet — which are ordinary meshes in the frame tree with ordinary
/// geometry, so a viewport draws them and the car comes up inside a couple of translucent boxes. The game
/// never draws them, and nobody opens a car to look at them, so neither does the editor: they are still in
/// the hierarchy, still selectable, still one click from being shown again — just not in the way.
/// </para>
/// <para>
/// The rule is a name rule and therefore only as good as the names it was measured against
/// (<c>--probe-hidden-defaults</c> lists every spelling in the shipped cars). It is deliberately narrow: a
/// missed spelling leaves a shell on screen, which is what the editor did before and is merely untidy, while
/// an over-broad word switches off a body on load, which reads as a broken archive.
/// </para>
/// </summary>
internal static class DefaultHidden
{
    /// <summary>Whether a frame of this name is a particle-emitter shell rather than something to look at.</summary>
    internal static bool IsEmitterShell(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        // Both spellings are in the shipped data — the Czech-authored frames double the m as often as not.
        return name.Contains("emitter", StringComparison.OrdinalIgnoreCase)
               || name.Contains("emmiter", StringComparison.OrdinalIgnoreCase);
    }
}
