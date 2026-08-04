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
    /// <summary>
    /// Whether a TOP-LEVEL frame of this name is one of the shells rather than the thing in the archive.
    ///
    /// <para>
    /// Three shapes, measured across the shipped cars (<c>--probe-hidden-defaults</c>): a holder whose name
    /// is the archive's with <c>_rain</c> on the end, a holder numbered <c>NNN_NN_</c> in front of the
    /// archive's name, and the one car that says <c>emit_test_</c> instead. The body's own holder is named
    /// after the archive and nothing else. What the numbered
    /// ones contain varies by car — <c>emitter_horeni</c>, <c>Plane02</c>, <c>user_defined_hank_b01</c> — and
    /// that is exactly why the mesh names were the wrong thing to key on: the size gives them away where the
    /// name does not, every one of them a flat sheet the length of the whole vehicle.
    /// </para>
    /// </summary>
    internal static bool IsSceneryHolder(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.EndsWith("_rain", StringComparison.OrdinalIgnoreCase)) return true;

        // A holder that says outright what it is. Only as a PREFIX: districts are full of names with "emi"
        // buried in them — CS_PRIZEMI_Kabel_17 and its thirty-odd siblings — and none of them starts this way.
        if (name.StartsWith("emit", StringComparison.OrdinalIgnoreCase)) return true;

        // NNN_NN_Whatever — two runs of digits, then the name. Nothing else in the corpus starts this way,
        // and a bare leading digit is not enough: plenty of real objects are numbered.
        int first = Digits(name, 0);
        if (first == 0 || first >= name.Length || name[first] != '_') return false;
        int second = Digits(name, first + 1);
        return second > first + 1 && second < name.Length && name[second] == '_';
    }

    private static int Digits(string s, int from)
    {
        int i = from;
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        return i;
    }

    /// <summary>Whether a frame of this name is a particle-emitter shell rather than something to look at.</summary>
    internal static bool IsEmitterShell(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        // Both spellings are in the shipped data — the Czech-authored frames double the m as often as not.
        return name.Contains("emitter", StringComparison.OrdinalIgnoreCase)
               || name.Contains("emmiter", StringComparison.OrdinalIgnoreCase);
    }
}
