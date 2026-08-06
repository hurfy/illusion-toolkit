using Illusion.Formats.Effects;

namespace Illusion.Assets.Effects;

/// <summary>
/// Writing one number of an effect — a birth rate, a colour key, a size — into the extracted working copy.
///
/// <para>
/// Like a tuning field or a prefab pick, the write lands on disk immediately and the archive still has to
/// be built. A value is addressed by its OFFSET in the file, never by its place in a rebuilt row list, so
/// an undo comes back to the same four bytes even after the panel has been rebuilt around it.
/// </para>
/// </summary>
public static class EffectEditing
{
    /// <summary>What one edit did, and everything needed to take it back.</summary>
    public sealed record Change(string Path, int Offset, bool IsFlag, float Before, float After, string Label);

    /// <summary>
    /// Writes one value. Returns null — writing nothing — when the file cannot be opened or is not an
    /// effects file after all.
    /// </summary>
    public static Change? Set(string path, int offset, float value, bool isFlag, string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (Open(path) is not { } tree) return null;
        if (offset < 0 || offset + (isFlag ? 1 : 4) > tree.Bytes.Length) return null;

        float before = isFlag ? tree.ReadByte(offset) : tree.ReadFloat(offset);
        Apply(tree, offset, value, isFlag);
        AtomicFile.WriteAllBytes(path, tree.Bytes);
        return new Change(path, offset, isFlag, before, value, label);
    }

    /// <summary>Puts a value back to what it held — the undo of <see cref="Set"/>; the redo is the same call
    /// with the change's After.</summary>
    public static bool Restore(Change change, float value)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (Open(change.Path) is not { } tree) return false;
        if (change.Offset < 0 || change.Offset + (change.IsFlag ? 1 : 4) > tree.Bytes.Length) return false;

        Apply(tree, change.Offset, value, change.IsFlag);
        AtomicFile.WriteAllBytes(change.Path, tree.Bytes);
        return true;
    }

    /// <summary>
    /// Adds a copy of one of the file's effects under a free id, and returns the id it got — or null when
    /// there was nothing to copy. Structural, so it is not an offset edit: undoing it puts the whole file
    /// back, which is why the caller keeps the bytes rather than a description of the change.
    /// </summary>
    public static uint? AddCopy(string path, uint id, out byte[]? before)
    {
        before = null;
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (Open(path) is not { } tree) return null;
        EffectChunk? source = tree.Effects.FirstOrDefault(e => tree.IdOf(e) == id);
        if (source == null) return null;

        var used = tree.Effects.Select(tree.IdOf).ToHashSet();
        uint fresh = used.Count == 0 ? 1u : used.Max() + 1;
        while (used.Contains(fresh)) fresh++;

        before = tree.Bytes;
        AtomicFile.WriteAllBytes(path, tree.WithEffectCopied(source, fresh));
        return fresh;
    }

    /// <summary>Puts a whole file back — how a copy is undone.</summary>
    public static void RestoreFile(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(bytes);
        AtomicFile.WriteAllBytes(path, bytes);
    }

    private static void Apply(EffectsTree tree, int offset, float value, bool isFlag)
    {
        if (isFlag) tree.WriteByte(offset, (byte)(value != 0f ? 1 : 0));
        else tree.WriteFloat(offset, value);
    }

    private static EffectsTree? Open(string path)
    {
        try { return EffectsTree.Read(File.ReadAllBytes(path)); }
        catch (IOException) { return null; }
    }
}
