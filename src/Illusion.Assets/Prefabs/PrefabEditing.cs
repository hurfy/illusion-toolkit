using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Prefabs;

/// <summary>
/// Changing which frame plays a role in a car's assembly — the one edit the toolkit makes to a PREFAB.
///
/// <para>
/// A reference is only ever set to a frame that is ALREADY IN THE ARCHIVE, picked by name. There is no path
/// here that takes a typed hash: a prefab addresses a frame by the FNV64 of its name and a hash that names
/// nothing does not fail, it just makes the part stop working, silently. Refusing to accept one is the whole
/// reason this layer exists rather than a text box.
/// </para>
/// <para>
/// The write lands in the extracted working copy immediately, like a collision box does — the archive still
/// has to be built. Undo is exact: a slot held one hash before and holds another now, and putting the first
/// one back is the same call with the two swapped.
/// </para>
/// </summary>
public static class PrefabEditing
{
    /// <summary>What one edit did, and everything needed to take it back.</summary>
    public sealed record Change(
        string PrefabPath, CarFrameSlot Slot, int Index, ulong Before, ulong After, string Label);

    /// <summary>
    /// Points a slot at a different frame. <paramref name="hash"/> must be the name hash of a frame in this
    /// very archive — <see cref="PrefabAssembly.FrameChoices"/> is where the caller gets one.
    /// </summary>
    /// <returns>The change, or null when the slot is not in this archive's prefab (nothing is written).</returns>
    public static Change? SetFrame(FileInfo archive, CarFrameSlot slot, int index, ulong hash, string label)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return SetFrameIn(MafiaEnvironment.ExtractedDir(archive), slot, index, hash, label);
    }

    /// <summary>The same, against a working copy that is not the archive's own — what the regression harness
    /// edits, so the player's install is never written to.</summary>
    public static Change? SetFrameIn(
        string extracted, CarFrameSlot slot, int index, ulong hash, string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(extracted);

        foreach (string path in Files(extracted))
        {
            PrefabFile prefab;
            try { prefab = PrefabFile.Load(path); }
            catch (Exception ex) when (ex is IOException or SdsFormatException) { continue; }
            if (prefab.Car == null) continue;

            // Read through the SAME accessor the panel reads with. A second hand-written mapping of slot to
            // field is a mapping that falls behind: the one that used to live here ended in "everything else
            // is 0", so eleven of the thirty-two slots recorded an undo value of "empty" and taking a pick
            // back cleared the slot instead of restoring it.
            ulong before = prefab.GetCarFrame(slot, index);
            if (!prefab.SetCarFrame(slot, index, hash)) continue;

            AtomicFile.WriteAllBytes(path, prefab.ToBytes());
            return new Change(path, slot, index, before, hash, label);
        }
        return null;
    }

    /// <summary>Puts a slot back where it was — the undo of <see cref="SetFrame"/>. Redo is the same call
    /// with <paramref name="hash"/> set to the change's After.</summary>
    public static bool Restore(Change change, ulong hash)
    {
        ArgumentNullException.ThrowIfNull(change);

        PrefabFile prefab;
        try { prefab = PrefabFile.Load(change.PrefabPath); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { return false; }
        if (!prefab.SetCarFrame(change.Slot, change.Index, hash)) return false;

        AtomicFile.WriteAllBytes(change.PrefabPath, prefab.ToBytes());
        return true;
    }

    /// <summary>A part gained or lost. The bytes are the part itself, so an undo puts back exactly what was
    /// there rather than a look-alike built from its neighbour.</summary>
    public sealed record ItemChange(
        string PrefabPath, CarItemKind Kind, int Index, byte[] Item, string Label);

    /// <summary>
    /// Adds one more of a kind, pointed at a frame the archive has. The new part is a copy of the last one
    /// there — a seat is more than a name, and inventing where the occupant sits would make a part that
    /// exists and does nothing.
    /// </summary>
    public static ItemChange? AddItem(
        FileInfo archive, CarItemKind kind, ulong frameHash, string label) =>
        AddItemIn(MafiaEnvironment.ExtractedDir(archive ?? throw new ArgumentNullException(nameof(archive))),
            kind, frameHash, label);

    /// <inheritdoc cref="AddItem"/>
    public static ItemChange? AddItemIn(string extracted, CarItemKind kind, ulong frameHash, string label)
    {
        foreach (string path in Files(extracted))
        {
            if (Open(path) is not { } prefab || prefab.Car == null) continue;
            int index = prefab.CarItemCount(kind);
            if (!prefab.AddCarItem(kind, frameHash)) continue;

            // The bytes of what was just added — that is what a redo puts back after an undo takes it away.
            byte[] added = prefab.TakeCarItem(kind, index) ?? [];
            prefab.PutCarItem(kind, index, added);

            AtomicFile.WriteAllBytes(path, prefab.ToBytes());
            return new ItemChange(path, kind, index, added, label);
        }
        return null;
    }

    /// <summary>Drops one, keeping it so the undo is exact.</summary>
    public static ItemChange? RemoveItem(FileInfo archive, CarItemKind kind, int index, string label) =>
        RemoveItemIn(MafiaEnvironment.ExtractedDir(archive ?? throw new ArgumentNullException(nameof(archive))),
            kind, index, label);

    /// <inheritdoc cref="RemoveItem"/>
    public static ItemChange? RemoveItemIn(string extracted, CarItemKind kind, int index, string label)
    {
        foreach (string path in Files(extracted))
        {
            if (Open(path) is not { } prefab || prefab.Car == null) continue;
            if (prefab.TakeCarItem(kind, index) is not { } taken) continue;

            AtomicFile.WriteAllBytes(path, prefab.ToBytes());
            return new ItemChange(path, kind, index, taken, label);
        }
        return null;
    }

    /// <summary>Puts a dropped part back exactly where it was.</summary>
    public static bool PutBack(ItemChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Open(change.PrefabPath) is not { } prefab) return false;
        if (!prefab.PutCarItem(change.Kind, change.Index, change.Item)) return false;

        AtomicFile.WriteAllBytes(change.PrefabPath, prefab.ToBytes());
        return true;
    }

    /// <summary>Takes a part away again — the redo of <see cref="PutBack"/>.</summary>
    public static bool TakeAway(ItemChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Open(change.PrefabPath) is not { } prefab) return false;
        if (prefab.TakeCarItem(change.Kind, change.Index) == null) return false;

        AtomicFile.WriteAllBytes(change.PrefabPath, prefab.ToBytes());
        return true;
    }

    /// <summary>A number or a flag that changed, with what it was — an undo is the same call reversed.</summary>
    public sealed record ValueChange(
        string PrefabPath, CarValueSlot Slot, int Index, int Axis, float Before, float After, string Label);

    /// <summary>Writes one of the numbers the game reads — a depth, a mass, one axis of a position.</summary>
    public static ValueChange? SetValue(
        FileInfo archive, CarValueSlot slot, int index, int axis, float value, string label) =>
        SetValueIn(MafiaEnvironment.ExtractedDir(archive ?? throw new ArgumentNullException(nameof(archive))),
            slot, index, axis, value, label);

    /// <inheritdoc cref="SetValue"/>
    public static ValueChange? SetValueIn(
        string extracted, CarValueSlot slot, int index, int axis, float value, string label)
    {
        foreach (string path in Files(extracted))
        {
            if (Open(path) is not { } prefab || prefab.Car == null) continue;

            float before = prefab.GetCarValue(slot, index, axis);
            if (float.IsNaN(before) || !prefab.SetCarValue(slot, index, axis, value)) continue;

            AtomicFile.WriteAllBytes(path, prefab.ToBytes());
            return new ValueChange(path, slot, index, axis, before, value, label);
        }
        return null;
    }

    /// <summary>Puts a number back — the undo of <see cref="SetValue"/>; the redo is the same with After.</summary>
    public static bool RestoreValue(ValueChange change, float value)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Open(change.PrefabPath) is not { } prefab) return false;
        if (!prefab.SetCarValue(change.Slot, change.Index, change.Axis, value)) return false;

        AtomicFile.WriteAllBytes(change.PrefabPath, prefab.ToBytes());
        return true;
    }

    private static PrefabFile? Open(string path)
    {
        try { return PrefabFile.Load(path); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { return null; }
    }

    private static IEnumerable<string> Files(string extracted)
    {
        IReadOnlyList<string> files;
        try { files = SdsManifest.Load(extracted).GetFiles("PREFAB"); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { yield break; }
        foreach (string file in files) yield return file;
    }

    // What the slot holds right now, read through the public view rather than the wire model — the same
    // numbers the panel is showing, so an undo restores what the user actually saw.
}
