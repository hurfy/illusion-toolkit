using Illusion.Formats.Archive;

namespace Illusion.Assets.Sds;

/// <summary>
/// Adding, replacing and dropping resources inside an archive's extracted working copy — what the content
/// browser's Delete / Copy / Paste / Import do underneath.
///
/// <para>
/// Everything here works on the MANIFEST plus files in the extracted folder, because that is the only pair
/// packing reads (<c>SdsArchive.Pack</c> never enumerates the folder). Two consequences shape the whole
/// design:
/// </para>
/// <list type="bullet">
/// <item><b>A delete only unsays the entry.</b> The payload is left on disk, unlisted. Packing ignores a
/// file the manifest does not name, so an orphan costs nothing but space in a working copy that can be
/// thrown away and re-extracted at any time — and it makes the undo exact, with no bytes to keep in
/// memory and nothing to lose if the process dies between the two halves.</item>
/// <item><b>Replacing keeps the entry and swaps the bytes.</b> Dropping a texture onto an archive that
/// already has one of that name is the commonest thing a modder does, and it must not produce a second
/// entry — for a texture the manifest name IS the identity the game resolves by, so a renamed copy would
/// simply never be found.</item>
/// </list>
/// <para>
/// Every operation returns what it did in a form the caller can hand straight back to undo it. Nothing here
/// touches the .sds: the archive still has to be built, exactly like a frame edit.
/// </para>
/// </summary>
public static class ArchiveEditing
{
    /// <summary>Where a payload goes when it is about to be overwritten, so undo can put it back. Inside the
    /// working copy, and named so it cannot collide with a resource: the manifest never lists a dotted
    /// folder, and packing never looks at one.</summary>
    private const string ShadowFolder = ".illusion-undo";

    /// <summary>Something that could not be done, and the sentence explaining it.</summary>
    public sealed record Refusal(string Name, string Reason);

    /// <summary>
    /// One resource on the toolkit's own clipboard. It carries the source entry's manifest fields VERBATIM
    /// rather than a type and a version: a packing handler reads its fields positionally, so copying them as
    /// they stand is the only transfer that is correct for every type at once.
    /// </summary>
    public sealed record Clip(
        string Type,
        SdsResourceKind Kind,
        string ManifestName,
        IReadOnlyList<(string Name, string Value)> Fields,
        string PayloadPath);

    /// <summary>A resource about to be written into an archive, with every question already answered.</summary>
    private sealed record Pending(
        string Type,
        SdsResourceKind Kind,
        string ManifestName,
        IReadOnlyList<(string Name, string Value)> Extras,
        int Version,
        string SourcePayload,
        string Label);

    /// <summary>An entry that was dropped, and everything needed to say it again.</summary>
    public sealed record RemovedEntry(string ManifestName, IReadOnlyList<(string Name, string Value)> Fields);

    /// <summary>A payload that was overwritten, and where its previous bytes were parked.</summary>
    public sealed record ReplacedPayload(string ManifestName, string ShadowPath, string PayloadPath);

    /// <summary>What a delete did. Hand it to <see cref="RestoreDeleted"/> to undo it.</summary>
    public sealed record DeleteResult(
        IReadOnlyList<RemovedEntry> Removed, IReadOnlyList<Refusal> Refused);

    /// <summary>What an import or a paste did. Hand it to <see cref="UndoWrite"/> to undo it.</summary>
    public sealed record WriteResult(
        IReadOnlyList<string> Added,
        IReadOnlyList<ReplacedPayload> Replaced,
        IReadOnlyList<Refusal> Refused);

    // ── Copying out ──

    /// <summary>
    /// Puts resources on the toolkit's clipboard. A texture drags its MIP chain along without being asked:
    /// the two are one thing wearing two manifest entries, and a texture pasted without its chain streams a
    /// tail that is not there.
    /// </summary>
    public static IReadOnlyList<Clip> Copy(ArchiveContents archive, IEnumerable<SdsResource> resources)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(resources);

        SdsManifest manifest = SdsManifest.Load(archive.Folder);
        var wanted = new List<SdsResource>(resources);

        foreach (SdsResource resource in wanted.ToList())
        {
            if (resource.Kind != SdsResourceKind.Texture) continue;
            string mip = SdsImportTypes.MipNameFor(resource.Name);
            if (wanted.Exists(r => string.Equals(r.Name, mip, StringComparison.OrdinalIgnoreCase))) continue;
            if (archive.Resources.FirstOrDefault(r =>
                    string.Equals(r.Name, mip, StringComparison.OrdinalIgnoreCase)) is { } companion)
            {
                wanted.Add(companion);
            }
        }

        var clips = new List<Clip>();
        foreach (SdsResource resource in wanted)
        {
            if (!SdsImportTypes.CanPaste(resource.Type)) continue;
            if (manifest.EntryFields(resource.Name) is not { } fields) continue;
            clips.Add(new Clip(resource.Type, resource.Kind, resource.Name, fields, resource.File.FullName));
        }
        return clips;
    }

    // ── Dropping ──

    /// <summary>
    /// Unsays the given resources. The payloads stay on disk, unlisted — see the note on this class.
    /// Refuses what the manifest cannot represent (a Script entry's shape is its own) and anything the
    /// manifest does not actually list.
    /// </summary>
    public static DeleteResult Delete(ArchiveContents archive, IEnumerable<SdsResource> resources)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(resources);

        SdsManifest manifest = SdsManifest.Load(archive.Folder);
        var removed = new List<RemovedEntry>();
        var refused = new List<Refusal>();

        foreach (SdsResource resource in resources)
        {
            if (manifest.EntryFields(resource.Name) is not { } fields)
            {
                refused.Add(new Refusal(resource.Name, "the manifest does not list it."));
                continue;
            }
            if (!manifest.RemoveEntry(resource.Name))
            {
                refused.Add(new Refusal(resource.Name, "the manifest entry could not be rewritten."));
                continue;
            }
            removed.Add(new RemovedEntry(resource.Name, fields));
        }
        return new DeleteResult(removed, refused);
    }

    /// <summary>Says the dropped entries again, fields and all — the undo of <see cref="Delete"/>. Takes the
    /// working folder rather than a snapshot: an undo runs long after the snapshot it came from went
    /// stale.</summary>
    public static void RestoreDeleted(string folder, IEnumerable<RemovedEntry> removed)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(removed);

        SdsManifest manifest = SdsManifest.Load(folder);
        foreach (RemovedEntry entry in removed) Announce(manifest, entry.Fields);
    }

    /// <summary>Unsays entries by name — the redo of <see cref="Delete"/>.</summary>
    public static void DropEntries(string folder, IEnumerable<string> names)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(names);

        SdsManifest manifest = SdsManifest.Load(folder);
        foreach (string name in names) manifest.RemoveEntry(name);
    }

    // ── Bringing in ──

    /// <summary>
    /// Brings files from disk into the archive. A name the manifest already lists is REPLACED rather than
    /// added; a name it does not is announced as a new entry. What cannot be typed by its extension, and
    /// what carries a manifest field the toolkit cannot derive, is refused with a reason rather than guessed
    /// at — see <see cref="SdsImportTypes"/>.
    /// </summary>
    public static WriteResult Import(ArchiveContents archive, IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(files);

        SdsManifest manifest = SdsManifest.Load(archive.Folder);
        var pending = new List<Pending>();
        var refused = new List<Refusal>();

        foreach (string path in files)
        {
            string shown = Path.GetFileName(path);
            if (!File.Exists(path))
            {
                refused.Add(new Refusal(shown, "there is no such file."));
                continue;
            }
            if (SdsImportTypes.Classify(path) is not { } type)
            {
                refused.Add(new Refusal(shown, SdsImportTypes.RefusalFor(path)));
                continue;
            }

            string manifestName = SdsImportTypes.ManifestNameFor(type, path);
            bool known = manifest.HasFile(manifestName);
            if (!known && !SdsImportTypes.CanAddNew(type))
            {
                refused.Add(new Refusal(shown,
                    "an XML resource is addressed by a system tag (RadioConfig, AI_BRAIN_CFG) that the file "
                    + "itself does not carry, so a new one cannot be announced. Replacing one the archive "
                    + "already has works — copy an existing entry and edit that."));
                continue;
            }
            if (!known && type.Kind == SdsResourceKind.Mipmap
                && !manifest.HasFile(SdsImportTypes.TextureNameFor(manifestName)))
            {
                refused.Add(new Refusal(shown,
                    "a MIP chain belongs to a texture, and this archive has no "
                    + SdsImportTypes.TextureNameFor(manifestName) + " to hang it on."));
                continue;
            }

            pending.Add(new Pending(
                type.Type, type.Kind, manifestName,
                SdsImportTypes.ExtrasFor(type, manifestName, manifest),
                type.Version, path, type.Label));
        }

        WriteResult written = Commit(archive, manifest, pending);
        return new WriteResult(written.Added, written.Replaced, [.. refused, .. written.Refused]);
    }

    /// <summary>
    /// Writes clipboard resources into an archive, keeping each one's manifest fields exactly as its source
    /// archive spelled them. Same replace-or-add rule as <see cref="Import"/>.
    /// </summary>
    public static WriteResult Paste(ArchiveContents archive, IEnumerable<Clip> clips)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(clips);

        SdsManifest manifest = SdsManifest.Load(archive.Folder);
        var pending = new List<Pending>();
        var refused = new List<Refusal>();

        foreach (Clip clip in clips)
        {
            if (!SdsImportTypes.CanPaste(clip.Type))
            {
                refused.Add(new Refusal(clip.ManifestName,
                    $"a {clip.Type} resource is wired into the archive that owns it, so it cannot be moved "
                    + "on its own."));
                continue;
            }
            if (!File.Exists(clip.PayloadPath))
            {
                refused.Add(new Refusal(clip.ManifestName,
                    "the copied payload is no longer in the working copy it came from."));
                continue;
            }

            // Fields come over verbatim, minus the two the writer supplies itself.
            var extras = new List<(string, string)>();
            int version = 0;
            foreach ((string name, string value) in clip.Fields)
            {
                switch (name)
                {
                    case "Type" or "File": break;
                    case "Version": version = int.TryParse(value, out int parsed) ? parsed : 0; break;
                    default: extras.Add((name, value)); break;
                }
            }

            pending.Add(new Pending(
                clip.Type, clip.Kind, clip.ManifestName, extras, version, clip.PayloadPath, clip.Type));
        }

        WriteResult written = Commit(archive, manifest, pending);
        return new WriteResult(written.Added, written.Replaced, [.. refused, .. written.Refused]);
    }

    /// <summary>
    /// Takes back an import or a paste: new entries are unsaid and their payloads deleted, replaced payloads
    /// are moved back over the copies that displaced them.
    /// <para>Returns the entries it unsaid, so the redo can say them again — the payload bytes are kept, so
    /// redoing costs a manifest line and nothing else.</para>
    /// </summary>
    public static IReadOnlyList<RemovedEntry> UndoWrite(string folder, WriteResult result)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(result);

        SdsManifest manifest = SdsManifest.Load(folder);
        var unsaid = new List<RemovedEntry>();
        foreach (string name in result.Added)
        {
            if (manifest.EntryFields(name) is not { } fields) continue;
            unsaid.Add(new RemovedEntry(name, fields));
            // Only the entry goes. The payload stays exactly where it was written, which makes a redo a
            // manifest line and nothing more — and a file the manifest does not name is invisible to packing,
            // so an import left undone forever costs space in a working copy and nothing else.
            manifest.RemoveEntry(name);
        }
        foreach (ReplacedPayload replaced in result.Replaced)
        {
            if (File.Exists(replaced.ShadowPath)) File.Move(replaced.ShadowPath, replaced.PayloadPath, true);
        }
        return unsaid;
    }

    /// <summary>Puts an undone import back — the redo of <see cref="UndoWrite"/>.</summary>
    public static void RedoWrite(
        string folder, IReadOnlyList<RemovedEntry> added, IReadOnlyList<ReplacedPayload> replaced)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(replaced);

        SdsManifest manifest = SdsManifest.Load(folder);
        foreach (RemovedEntry entry in added) Announce(manifest, entry.Fields);
        foreach (ReplacedPayload payload in replaced)
        {
            if (File.Exists(payload.PayloadPath)) File.Move(payload.PayloadPath, payload.ShadowPath, true);
        }
    }

    // ── The one write path both Import and Paste go through ──

    private static WriteResult Commit(ArchiveContents archive, SdsManifest manifest, List<Pending> pending)
    {
        var added = new List<string>();
        var replaced = new List<ReplacedPayload>();
        var refused = new List<Refusal>();

        foreach (Pending item in pending)
        {
            string relative = ArchiveContents.PayloadPath(item.ManifestName, item.Kind);
            string payload = Path.Combine(archive.Folder, relative);
            bool known = manifest.HasFile(item.ManifestName);

            try
            {
                if (known)
                {
                    // Park the bytes that are about to go, then swap the new ones in. The park happens first:
                    // a failure after it costs an orphan in a scratch folder, one before it would cost the
                    // only copy of a resource the archive still announces.
                    string shadow = Park(archive.Folder, relative);
                    File.Copy(item.SourcePayload, payload, overwrite: true);
                    replaced.Add(new ReplacedPayload(item.ManifestName, shadow, payload));
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
                File.Copy(item.SourcePayload, payload, overwrite: true);
                if (!manifest.AddEntry(item.Type, item.ManifestName, item.Version, item.Extras))
                {
                    // Only reachable if two pending items name the same resource; the second is the loser.
                    refused.Add(new Refusal(item.ManifestName, "another file in this batch already took the name."));
                    continue;
                }
                added.Add(item.ManifestName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                refused.Add(new Refusal(item.ManifestName, ex.Message));
            }
        }
        return new WriteResult(added, replaced, refused);
    }

    // Moves a payload out of the way into the working copy's undo shadow, under a name nothing will reuse.
    private static string Park(string folder, string relative)
    {
        string shadow = Path.Combine(folder, ShadowFolder);
        Directory.CreateDirectory(shadow);

        string stem = relative.Replace(Path.DirectorySeparatorChar, '_').Replace('/', '_');
        string path = Path.Combine(shadow, stem);
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(shadow, $"{stem}.{n}");

        string live = Path.Combine(folder, relative);
        if (File.Exists(live)) File.Move(live, path);
        return path;
    }

    // Re-announces an entry from the fields a delete kept, in the order they were read.
    private static void Announce(SdsManifest manifest, IReadOnlyList<(string Name, string Value)> fields)
    {
        string type = TypeOf(fields);
        string file = ValueOf(fields, "File");
        if (type.Length == 0 || file.Length == 0) return;

        var extras = new List<(string, string)>();
        int version = 0;
        foreach ((string name, string value) in fields)
        {
            switch (name)
            {
                case "Type" or "File": break;
                case "Version": version = int.TryParse(value, out int parsed) ? parsed : 0; break;
                default: extras.Add((name, value)); break;
            }
        }
        manifest.AddEntry(type, file, version, extras);
    }

    private static string TypeOf(IReadOnlyList<(string Name, string Value)> fields) => ValueOf(fields, "Type");

    private static string ValueOf(IReadOnlyList<(string Name, string Value)> fields, string name)
    {
        foreach ((string field, string value) in fields)
        {
            if (string.Equals(field, name, StringComparison.Ordinal)) return value;
        }
        return "";
    }
}
