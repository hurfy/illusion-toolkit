using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Cars;

/// <summary>
/// What one <see cref="Car.Save"/> did, resource by resource.
///
/// <para>
/// <see cref="Unchanged"/> is not a failure — it is the ordinary outcome of saving a car nobody edited, and
/// the measurable form of "everything I did not touch is left byte for byte as it was". A resource is
/// rewritten only when the bytes it would get differ from the bytes it already has.
/// </para>
/// </summary>
/// <param name="Written">The files this save rewrote.</param>
/// <param name="Unchanged">The files whose bytes were already exactly right, so nothing was written.</param>
/// <param name="Lost">What the save could not deliver, each named: a field that did not survive being
/// written and read back, or a car with no working copy to write into. Non-empty means the save was REFUSED
/// and nothing at all was written — a refusal that writes half an edit is worse than no feature.</param>
public sealed record CarSave(
    IReadOnlyList<string> Written, IReadOnlyList<string> Unchanged, IReadOnlyList<string> Lost)
{
    /// <summary>Whether everything the car holds reached the file it was meant to.</summary>
    public bool Ok => Lost.Count == 0;
}

public sealed partial class Car
{
    private bool _framesChanged;
    private bool _nameTableChanged;

    /// <summary>Whether the frame graph has been changed since the car was read, and so has to be written.
    /// A car nobody edited says false, and its save leaves the frame resource alone entirely.</summary>
    public bool FramesChanged => _framesChanged;

    /// <summary>
    /// Says the frame graph has been changed and the next save must write it.
    ///
    /// <para>
    /// The frame graph is written ONLY when this has been called. Re-serializing it runs
    /// <c>UpdateFrameData</c>/<c>SanitizeFrameData</c> over the live resource, which renumbers indices and
    /// prunes unreferenced blocks — so writing one nobody edited is not a no-op, it is a rewrite of a
    /// resource this car had no business touching.
    /// </para>
    /// </summary>
    /// <param name="nameTable">Also rebuild the frame name table. Needed when a frame was ADDED or REMOVED,
    /// not when one was moved: a frame missing from the name table loads and is invisible in game.</param>
    public void TouchFrames(bool nameTable = false)
    {
        _framesChanged = true;
        _nameTableChanged |= nameTable;
    }

    /// <summary>
    /// Writes the car back to the working copy it was read from — the ONE path from a component-level intent
    /// to bytes, and the half of the seam that makes the aggregate more than a reader.
    ///
    /// <para>
    /// It writes the structures that were READ, with the fields that changed replaced inside them. It never
    /// rebuilds a deform part from its component: a large part of that struct is undecoded — <c>unk2</c>,
    /// <c>unk4</c>, <c>unk5</c>, <c>unk6</c>, <c>unk14</c>, <c>unk18</c>, <c>unk20</c>, <c>unk21_data</c>,
    /// <c>unk22_rel_data</c>, <c>unk23</c>, <c>unk24</c> and seven more inside its common block — and every
    /// one of them has to reach the game exactly as it arrived.
    /// </para>
    /// <para>
    /// The bytes are read back and compared FIELD BY FIELD before anything is written. A field that did not
    /// survive is named in <see cref="CarSave.Lost"/> and the save is refused whole, so a car is never left
    /// holding half an edit and a lost <c>unk14</c>.
    /// </para>
    /// </summary>
    /// <param name="redirect">Maps a file's real path to where this save should put it. Probes point it at a
    /// scratch folder so the corpus is measured without the player's install ever being written to;
    /// production passes null.</param>
    public CarSave Save(Func<string, string>? redirect = null)
    {
        // Everything that could refuse the save is settled BEFORE the first byte is written, so a refusal is
        // always a car left exactly as it was rather than one holding half an edit.
        if (PrefabPath == null)
        {
            return new CarSave([], [], ["this car was stitched in memory and has no working copy to save into"]);
        }
        SdsManifest? manifest = null;
        if (_framesChanged)
        {
            if (Frames == null || Extracted == null)
            {
                return new CarSave([], [],
                    ["the frame graph changed and this car has no working copy to write it into"]);
            }
            try { manifest = SdsManifest.Load(Extracted); }
            catch (Exception ex) when (ex is IOException or SdsFormatException)
            {
                return new CarSave([], [], ["the frame graph changed and the archive's manifest "
                    + $"cannot be read to find out where it goes — {ex.Message}"]);
            }
            if (manifest.GetFiles("FrameResource").Count == 0)
            {
                return new CarSave([], [],
                    ["the frame graph changed and this archive lists no frame resource to write it into"]);
            }
        }

        // ── the prefab: serialized, read back, and compared before it is allowed anywhere near a file ──
        string prefab = Path.GetFileName(PrefabPath);
        byte[] bytes;
        PrefabFile echo;
        try
        {
            bytes = Prefab.ToBytes();
            using var buffer = new MemoryStream(bytes, writable: false);
            echo = PrefabFile.Read(buffer);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SdsFormatException)
        {
            return new CarSave([], [],
                [$"{prefab}: it does not survive being written and read back — {ex.Message}"]);
        }

        IReadOnlyList<string> lost = Prefab.Diff(echo);
        if (lost.Count > 0)
        {
            return new CarSave([], [], [.. lost.Select(field => $"{prefab}: {field}")]);
        }

        var written = new List<string>();
        var unchanged = new List<string>();
        Put(PrefabPath, bytes, redirect, written, unchanged);
        if (manifest != null) SaveFrames(manifest, redirect, written, unchanged);
        return new CarSave(written, unchanged, []);
    }

    /// <summary>
    /// Writes the frame resource, and the name table after it when a frame was added or removed.
    ///
    /// <para>
    /// The order matters: the rebuild reads the object order and scene indices that serializing the frame
    /// resource finalises, so a name table built first is a name table built against the previous numbering.
    /// </para>
    /// <para>
    /// Unlike the prefab this is not verified field by field — the frame resource has no field-level
    /// comparison, and it is not the structure the carry-verbatim rule is about. It is the prefab that holds
    /// the half-decoded deform part.
    /// </para>
    /// </summary>
    private void SaveFrames(
        SdsManifest manifest, Func<string, string>? redirect, List<string> written, List<string> unchanged)
    {
        Put(manifest.GetFiles("FrameResource")[0], Frames!.WriteToStream(), redirect, written, unchanged);
        _framesChanged = false;

        if (!_nameTableChanged) return;
        IReadOnlyList<string> tables = manifest.GetFiles("FrameNameTable");
        // An archive with no name table has nowhere for one to go, and that is not a loss: the table names
        // the frames that are visible, and an archive that ships without one never had that opinion.
        if (tables.Count == 0) { _nameTableChanged = false; return; }

        var table = new FrameNameTable();
        table.BuildDataFromResource(Frames);
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            table.WriteToFile(writer);
        }
        Put(tables[0], buffer.ToArray(), redirect, written, unchanged);
        _nameTableChanged = false;
    }

    /// <summary>
    /// Puts bytes where they belong, and only when they are not already there.
    ///
    /// <para>
    /// The comparison is against the TARGET, which is what makes a redirected save a real write rather than
    /// a skipped one: a scratch folder holds nothing yet, so the bytes land there and can be compared with
    /// the original by whoever asked for the redirect.
    /// </para>
    /// </summary>
    private static void Put(
        string path, byte[] bytes, Func<string, string>? redirect,
        List<string> written, List<string> unchanged)
    {
        string target = redirect?.Invoke(path) ?? path;
        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
        {
            unchanged.Add(target);
            return;
        }

        string? folder = Path.GetDirectoryName(target);
        if (folder != null) Directory.CreateDirectory(folder);
        AtomicFile.WriteAllBytes(target, bytes);
        written.Add(target);
    }
}
