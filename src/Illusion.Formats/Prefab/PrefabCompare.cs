namespace Illusion.Formats.Prefab;

public sealed partial class PrefabFile
{
    /// <summary>
    /// Which FIELDS this container and another disagree on, each named by its path — the answer to "what did
    /// the write lose", which a byte offset cannot give.
    ///
    /// <para>
    /// The comparison is over what the file MEANS, not over the bytes it arrived in. An entry the core
    /// decoded is compared through its typed capsule, including the bit-packed tail it carries opaquely;
    /// an entry left opaque is compared as its blob. The blob beside a decoded entry is deliberately NOT
    /// compared: the core re-encodes a typed entry from its typed fields on write and leaves that copy as
    /// it was read, so after any edit it is stale on purpose, and reporting it would name a difference that
    /// is the write working rather than the write failing.
    /// </para>
    /// <para>
    /// This is what makes a lost field a diagnosis. Serialize, read the bytes back, compare — a field the
    /// writer dropped shows up as <c>prefab[0].car.Deformation[0].DeformParts[7].Unk14: count 3 vs 0</c>
    /// rather than as a length that no longer matches.
    /// </para>
    /// </summary>
    /// <returns>Empty when the two carry the same car. Never null.</returns>
    public IReadOnlyList<string> Diff(PrefabFile other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var diffs = new List<string>();

        if (Wire.SizeOfFile != other.Wire.SizeOfFile)
        {
            diffs.Add($"prefab.SizeOfFile: {Wire.SizeOfFile} vs {other.Wire.SizeOfFile}");
        }
        if (Wire.SizeOfFile2 != other.Wire.SizeOfFile2)
        {
            diffs.Add($"prefab.SizeOfFile2: {Wire.SizeOfFile2} vs {other.Wire.SizeOfFile2}");
        }
        if (Wire.Prefabs.Count != other.Wire.Prefabs.Count)
        {
            diffs.Add($"prefab.Prefabs: count {Wire.Prefabs.Count} vs {other.Wire.Prefabs.Count}");
            return diffs;
        }

        for (int i = 0; i < Wire.Prefabs.Count; i++)
        {
            DiffEntry($"prefab[{i}]", Wire.Prefabs[i], other.Wire.Prefabs[i], diffs);
        }
        return diffs;
    }

    private static void DiffEntry(
        string at, Native.Model.PrefabEntryW a, Native.Model.PrefabEntryW b, List<string> diffs)
    {
        if (a.Hash != b.Hash) diffs.Add($"{at}.Hash: {a.Hash:X16} vs {b.Hash:X16}");
        if (a.PrefabType != b.PrefabType) diffs.Add($"{at}.PrefabType: {a.PrefabType} vs {b.PrefabType}");
        if (a.Unk0 != b.Unk0) diffs.Add($"{at}.Unk0: {a.Unk0} vs {b.Unk0}");
        if (a.TypedKind != b.TypedKind)
        {
            // The variant itself changed, so nothing below is comparable — an entry that was read as a car
            // and came back opaque has lost everything, and saying so once is the honest report.
            diffs.Add($"{at}.TypedKind: {a.TypedKind} vs {b.TypedKind}");
            return;
        }

        if (a.TypedKind == 0)
        {
            Native.Model.Wire.DiffBytes($"{at}.Data", a.Data, b.Data, diffs);
            return;
        }

        DiffList($"{at}.car", a.CarInit, b.CarInit, Native.Model.PrefabCarInitW.Diff, diffs);
        DiffList($"{at}.wheel", a.WheelInit, b.WheelInit, Native.Model.PrefabWheelInitW.Diff, diffs);
        DiffList($"{at}.physThing", a.PhysThing, b.PhysThing, Native.Model.PrefabPhysThingInitW.Diff, diffs);
    }

    private static void DiffList<T>(
        string at, List<T> a, List<T> b, Action<string, T, T, List<string>> diff, List<string> diffs)
    {
        if (a.Count != b.Count)
        {
            diffs.Add($"{at}: count {a.Count} vs {b.Count}");
            return;
        }
        for (int i = 0; i < a.Count; i++)
        {
            diff(a.Count == 1 ? at : $"{at}[{i}]", a[i], b[i], diffs);
        }
    }
}
