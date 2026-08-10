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
        // A shape record is only real once the manifest names it — packing builds the archive from the
        // manifest and never from the folder — so a manifest that cannot be opened refuses the save here,
        // while nothing has been written, rather than leaving a record the archive carries and never uses.
        if (_pendingShapes.Count > 0 && Announceable(redirect) is { } why)
        {
            return new CarSave([], [], [why]);
        }

        // ── the prefab: has anyone else written it since this car read it? ──
        //
        // The aggregate writes the WHOLE prefab from the copy it holds, so a file that moved underneath it
        // would be overwritten rather than merged — and four other modules still write this same file
        // directly. Refusing is the only honest answer: the edit is still in memory, and reopening the
        // archive picks up both.
        if (_prefabOnDisk != null && Current(PrefabPath) is { } now
            && !now.AsSpan().SequenceEqual(_prefabOnDisk))
        {
            return new CarSave([], [], [$"{Path.GetFileName(PrefabPath)}: it changed on disk after this car "
                + "was read, and saving would write over that change — reopen the archive and make the edit "
                + "again"]);
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
        // What the file holds NOW, so the guard above measures the next save against this one rather than
        // against the state the car was read in. A redirected save leaves PrefabPath alone, and then so does
        // this.
        if (redirect == null) _prefabOnDisk = bytes;
        List<string> lostShapes = SaveShapes(redirect, written, unchanged);
        if (manifest != null) SaveFrames(manifest, redirect, written, unchanged);
        return new CarSave(written, unchanged, lostShapes);
    }

    /// <summary>The prefab's bytes as this car last read or wrote them — what says whether somebody else has
    /// written the file since. Null for a car stitched in memory, which has no file to be overtaken on.</summary>
    private byte[]? _prefabOnDisk;

    /// <summary>Remembers what the prefab file held when this car was read.</summary>
    private void RememberPrefabOnDisk()
    {
        if (PrefabPath != null) _prefabOnDisk = Current(PrefabPath);
    }

    private static byte[]? Current(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Why the manifest cannot be written, or null when it can. Asked BEFORE anything is written,
    /// because a shape record the manifest never names is a resource the packer silently drops.</summary>
    private string? Announceable(Func<string, string>? redirect)
    {
        if (Extracted == null) return "this car has no working copy to write its shape records into";
        try
        {
            SdsManifest.Load(Path.GetDirectoryName(Mirror(redirect))!);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SdsFormatException)
        {
            return "the archive's manifest cannot be read, so a shape record could not be announced in it "
                + $"— and one the manifest does not name is dropped when the archive is packed ({ex.Message})";
        }
    }

    /// <summary>
    /// Writes the ItemDesc records a collision edit minted or changed, and unwrites the ones it took away.
    ///
    /// <para>
    /// The MANIFEST goes with each of them, and it is not optional in either direction. Packing builds the
    /// archive from the manifest and never from the folder, so a record written and not announced is silently
    /// dropped — and an entry left naming a file that is gone does not get skipped either, it fails the whole
    /// Build with "Could not find file …ItemDesc_0.ids" until the entry goes.
    /// </para>
    /// </summary>
    /// <returns>What could not be delivered, each named. Non-empty makes the whole save a failure, because a
    /// record the manifest does not name is one the game never sees.</returns>
    private List<string> SaveShapes(
        Func<string, string>? redirect, List<string> written, List<string> unchanged)
    {
        var lost = new List<string>();
        if (_pendingShapes.Count == 0) return lost;

        foreach ((string path, byte[]? bytes) in _pendingShapes.ToList())
        {
            string target = redirect?.Invoke(path) ?? path;
            string name = Path.GetFileName(path);
            if (bytes == null)
            {
                if (!Announce(redirect, name, add: false))
                {
                    // The entry is still there and the file is about to go, which is the one combination that
                    // fails a Build outright — so the file stays and the loss is reported instead.
                    lost.Add($"{name}: the manifest still names it and could not be rewritten, so the record "
                        + "was left in place rather than leaving the archive unpackable");
                    continue;
                }
                try { if (File.Exists(target)) File.Delete(target); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The manifest no longer names it, so the archive still packs; the file is orphaned, not
                    // fatal, and re-extracting the archive clears it.
                }
                written.Add(target);
                _pendingShapes.Remove(path);
                continue;
            }
            Put(target, bytes, redirect: null, written, unchanged);
            if (Announce(redirect, name, add: true)) { _pendingShapes.Remove(path); continue; }

            // Written and unannounced is the worst of the three outcomes: the packer builds from the
            // manifest, so the record would be dropped and the volume naming it would resolve to nothing —
            // a collision that is in every file and in no game. The file goes back and the save fails.
            try { if (File.Exists(target)) File.Delete(target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* reported below */ }
            written.Remove(target);
            lost.Add($"{name}: it could not be announced in the archive's manifest, and a record the manifest "
                + "does not name is dropped when the archive is packed");
        }
        return lost;
    }

    /// <summary>
    /// Says (or unsays) a shape record in the archive's manifest.
    ///
    /// <para>
    /// A REDIRECTED save says it in the mirror's own manifest, copying the original there first — which is
    /// what keeps a probe measuring the corpus from writing into the player's install, and is also the only
    /// way the mirror is a complete archive that could actually be packed.
    /// </para>
    /// </summary>
    /// <returns>False when the manifest could not be changed — which the caller turns into a failed save,
    /// because the pre-existing shape writer treats exactly this as a hard refusal for the same reason.</returns>
    private bool Announce(Func<string, string>? redirect, string file, bool add)
    {
        if (Extracted == null) return false;
        try
        {
            SdsManifest manifest = SdsManifest.Load(Path.GetDirectoryName(Mirror(redirect))!);
            if (add) manifest.AddEntry("ItemDesc", file, ItemDescVersion); else manifest.RemoveEntry(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SdsFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The manifest this save writes to: the archive's own, or — under a redirect — a copy of it inside the
    /// mirror, made on first use.
    ///
    /// <para>
    /// The copy is what keeps a probe measuring the corpus from writing into the player's install, and it is
    /// also the only way the mirror is an archive that could actually be packed: a shape record is real only
    /// once a manifest names it.
    /// </para>
    /// </summary>
    private string Mirror(Func<string, string>? redirect)
    {
        string source = Path.Combine(Extracted!, "SDSContent.xml");
        string target = redirect?.Invoke(source) ?? source;
        if (File.Exists(target)) return target;

        string? folder = Path.GetDirectoryName(target);
        if (folder != null) Directory.CreateDirectory(folder);
        File.Copy(source, target);
        return target;
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
