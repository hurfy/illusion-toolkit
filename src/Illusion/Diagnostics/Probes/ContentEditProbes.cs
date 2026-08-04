using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Probes of the content browser's archive editing: dropping a resource, bringing one in from disk, copying
/// one into another archive, and taking each of those back.
/// <para>
/// Everything runs on a SCRATCH COPY of a real extracted archive. Editing an archive's contents writes into
/// the folder <see cref="MafiaEnvironment.ExtractedDir"/> names, which is the player's own install — so the
/// working copy is duplicated into %TEMP% first and every assertion is made against that. The game's files
/// are opened for reading and nothing else.
/// </para>
/// <para>
/// The assertion that matters most is the last one in each block: after the edit, the folder still PACKS.
/// A manifest that names a file nothing carries does not fail loudly at the point of the mistake, it fails
/// the whole Build afterwards, so the only honest check is to build it.
/// </para>
/// </summary>
internal static class ContentEditProbes
{
    // Output: %TEMP%\illusion_content_edit.txt
    internal static void RunContentEditProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_content_edit.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " — " + detail : "")}");
        }

        string scratch = Path.Combine(Path.GetTempPath(), "illusion_content_edit");
        try
        {
            if (!ProbeAssert.InitEnv(out string? envError))
            {
                sb.AppendLine("ENV ERROR: " + envError);
                File.WriteAllText(outFile, sb.ToString());
                return;
            }

            // Two cars, whichever two this install actually ships: the archive names differ between the
            // retail, Steam and Definitive releases, and the probe is about the editing rather than about
            // any particular car.
            string cars = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
            FileInfo[] fleet = Directory.Exists(cars)
                ? [.. new DirectoryInfo(cars).GetFiles("*.sds").OrderBy(f => f.Length)]
                : [];
            Check("the install ships cars to work on", fleet.Length >= 2, $"{fleet.Length} archives in sds\\cars");
            if (fleet.Length < 2)
            {
                File.WriteAllText(outFile, sb.ToString());
                return;
            }

            FileInfo sds = fleet[0];
            FileInfo other = fleet[1];
            sb.AppendLine($"working on {sds.Name} and {other.Name}");
            sb.AppendLine();

            // A scratch duplicate of each working copy. From here on the game install is read-only.
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            string home = CopyWorkingCopy(SdsMeshLoader.EnsureExtracted(sds), Path.Combine(scratch, "home"));
            string away = CopyWorkingCopy(SdsMeshLoader.EnsureExtracted(other), Path.Combine(scratch, "away"));

            ArchiveContents archive = ArchiveContents.ReadFrom(sds, home);
            int startCount = archive.Resources.Count;
            Check("the scratch copy reads like the archive it came from", startCount > 0,
                $"{startCount} resources");
            Check("an untouched working copy packs", Packs(home, out string? packError), packError ?? "");

            // ── 1. Import: a loose .dds that the archive does not have ──
            var texture = archive.Resources.FirstOrDefault(r => r.Kind == SdsResourceKind.Texture);
            Check("the archive carries a texture to work from", texture != null, texture?.Name ?? "(none)");
            if (texture == null)
            {
                File.WriteAllText(outFile, sb.ToString());
                return;
            }

            string loose = Path.Combine(scratch, "illusion_probe_import.dds");
            File.Copy(texture.File.FullName, loose, overwrite: true);

            ArchiveEditing.WriteResult imported = ArchiveEditing.Import(archive, [loose]);
            Check("an unknown texture is ADDED, not replaced",
                imported.Added.Count == 1 && imported.Replaced.Count == 0 && imported.Refused.Count == 0,
                Describe(imported));
            Check("the payload landed beside the others",
                File.Exists(Path.Combine(home, "illusion_probe_import.dds")));
            Check("the manifest gained exactly one entry",
                ArchiveContents.ReadFrom(sds, home).Resources.Count == startCount + 1);
            Check("HasMIP is 0 for a texture with no MIP chain in the archive",
                FieldOf(home, "illusion_probe_import.dds", "HasMIP") == "0",
                FieldOf(home, "illusion_probe_import.dds", "HasMIP") ?? "(missing)");
            Check("the entry carries the version every shipped Texture carries",
                FieldOf(home, "illusion_probe_import.dds", "Version") == "2");
            Check("an archive with an imported texture still packs", Packs(home, out packError), packError ?? "");

            // ── 2. Import again over the same name: a REPLACE, and the entry count must not move ──
            ArchiveEditing.WriteResult again = ArchiveEditing.Import(
                ArchiveContents.ReadFrom(sds, home), [loose]);
            Check("importing a name the archive already has replaces it",
                again.Added.Count == 0 && again.Replaced.Count == 1, Describe(again));
            Check("a replace does not add a second entry",
                ArchiveContents.ReadFrom(sds, home).Resources.Count == startCount + 1);
            Check("the displaced bytes were parked for the undo",
                File.Exists(again.Replaced[0].ShadowPath));

            ArchiveEditing.UndoWrite(home, again);
            Check("undoing a replace leaves the entry alone",
                ArchiveContents.ReadFrom(sds, home).Resources.Count == startCount + 1);

            // ── 3. Undo / redo of the import ──
            IReadOnlyList<ArchiveEditing.RemovedEntry> unsaid = ArchiveEditing.UndoWrite(home, imported);
            Check("undoing an import unsays its entry",
                ArchiveContents.ReadFrom(sds, home).Resources.Count == startCount, Describe(imported));
            Check("...and keeps the payload, so the redo is a manifest line",
                File.Exists(Path.Combine(home, "illusion_probe_import.dds")));
            Check("an archive with the import undone still packs", Packs(home, out packError), packError ?? "");

            ArchiveEditing.RedoWrite(home, unsaid, imported.Replaced);
            Check("redoing says it again",
                ArchiveContents.ReadFrom(sds, home).Resources.Count == startCount + 1);
            Check("a redone import packs", Packs(home, out packError), packError ?? "");

            // ── 4. Delete, and take it back ──
            archive = ArchiveContents.ReadFrom(sds, home);
            SdsResource victim = archive.Resources.First(r =>
                string.Equals(r.Name, "illusion_probe_import.dds", StringComparison.OrdinalIgnoreCase));
            ArchiveEditing.DeleteResult deleted = ArchiveEditing.Delete(archive, [victim]);
            Check("a delete unsays exactly one entry",
                deleted.Removed.Count == 1 && deleted.Refused.Count == 0);
            Check("the payload survives the delete — that is what makes undo exact",
                File.Exists(victim.File.FullName));
            Check("an archive with a deleted resource packs", Packs(home, out packError), packError ?? "");

            ArchiveEditing.RestoreDeleted(home, deleted.Removed);
            Check("undoing a delete restores the entry, fields and all",
                FieldOf(home, "illusion_probe_import.dds", "HasMIP") == "0"
                && ArchiveContents.ReadFrom(sds, home).Resources.Count == startCount + 1);

            ArchiveEditing.DropEntries(home, ["illusion_probe_import.dds"]);
            Check("redoing a delete unsays it again",
                ArchiveContents.ReadFrom(sds, home).Resources.Count == startCount);

            // ── 5. What must be refused ──
            string bogus = Path.Combine(scratch, "illusion_probe.frame");
            File.WriteAllBytes(bogus, [1, 2, 3, 4]);
            ArchiveEditing.WriteResult refusedType = ArchiveEditing.Import(
                ArchiveContents.ReadFrom(sds, home), [bogus]);
            Check("an extension the toolkit cannot type is refused",
                refusedType.Added.Count == 0 && refusedType.Refused.Count == 1, Describe(refusedType));

            string strayMip = Path.Combine(scratch, "MIP_illusion_probe_nothing.dds");
            File.Copy(texture.File.FullName, strayMip, overwrite: true);
            ArchiveEditing.WriteResult refusedMip = ArchiveEditing.Import(
                ArchiveContents.ReadFrom(sds, home), [strayMip]);
            Check("a MIP chain with no texture to hang on is refused",
                refusedMip.Added.Count == 0 && refusedMip.Refused.Count == 1, Describe(refusedMip));

            string newXml = Path.Combine(scratch, "illusion_probe_config.xml");
            File.WriteAllText(newXml, "<Config />");
            ArchiveEditing.WriteResult refusedXml = ArchiveEditing.Import(
                ArchiveContents.ReadFrom(sds, home), [newXml]);
            Check("a brand-new XML is refused — its system tag cannot be derived",
                refusedXml.Added.Count == 0 && refusedXml.Refused.Count == 1, Describe(refusedXml));

            Check("nothing refused left a trace in the manifest",
                ArchiveContents.ReadFrom(sds, home).Resources.Count == startCount);

            // ── 6. Copy a texture that HAS a MIP chain: the pair must travel together ──
            archive = ArchiveContents.ReadFrom(sds, home);
            SdsResource? paired = archive.Resources.FirstOrDefault(r =>
                r.Kind == SdsResourceKind.Texture
                && archive.Resources.Any(m => string.Equals(m.Name, "MIP_" + r.Name, StringComparison.OrdinalIgnoreCase)));
            if (paired == null)
            {
                Check("an archive with a MIP-backed texture was found", false, "no Texture+Mipmap pair here");
            }
            else
            {
                IReadOnlyList<ArchiveEditing.Clip> clips = ArchiveEditing.Copy(archive, [paired]);
                Check("copying a texture takes its MIP chain with it", clips.Count == 2,
                    string.Join(", ", clips.Select(c => c.ManifestName)));

                ArchiveContents target = ArchiveContents.ReadFrom(other, away);
                int awayCount = target.Resources.Count;
                ArchiveEditing.WriteResult pasted = ArchiveEditing.Paste(target, clips);
                Check("both halves land in the other archive",
                    pasted.Added.Count + pasted.Replaced.Count == 2 && pasted.Refused.Count == 0,
                    Describe(pasted));
                Check("the pasted texture kept the source's own HasMIP",
                    FieldOf(away, paired.Name, "HasMIP") == FieldOf(home, paired.Name, "HasMIP"),
                    $"{FieldOf(away, paired.Name, "HasMIP")} vs {FieldOf(home, paired.Name, "HasMIP")}");
                Check("the archive pasted into packs", Packs(away, out packError), packError ?? "");

                ArchiveEditing.UndoWrite(away, pasted);
                Check("undoing a paste puts the count back",
                    ArchiveContents.ReadFrom(other, away).Resources.Count == awayCount);
                Check("the archive pasted into still packs after the undo", Packs(away, out packError),
                    packError ?? "");
            }

            // ── 7. What may not be pasted at all ──
            archive = ArchiveContents.ReadFrom(sds, home);
            SdsResource? frame = archive.Resources.FirstOrDefault(r => r.Kind == SdsResourceKind.Mesh);
            if (frame != null)
            {
                Check("a frame resource cannot be put on the clipboard",
                    ArchiveEditing.Copy(archive, [frame]).Count == 0, frame.Name);
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            sb.Insert(0, $"CONTENT EDIT PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* a scratch folder left behind is not worth failing the probe over */ }
        }
    }

    // A working copy is a flat-ish tree of payloads plus SDSContent.xml; copying it whole is what keeps the
    // game's own folder out of every assertion below.
    private static string CopyWorkingCopy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, target, StringComparison.Ordinal));
        }
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, target, StringComparison.Ordinal), overwrite: true);
        }
        return target;
    }

    // The assertion that actually matters: does this folder still build into an archive?
    private static bool Packs(string folder, out string? error)
    {
        try
        {
            SdsArchive archive = SdsArchive.Pack(folder, GameProfile.MafiaII);
            using var sink = new MemoryStream();
            archive.Save(sink, new SdsWriteOptions());
            error = null;
            return sink.Length > 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? FieldOf(string folder, string file, string field)
    {
        if (SdsManifest.Load(folder).EntryFields(file) is not { } fields) return null;
        foreach ((string name, string value) in fields)
        {
            if (string.Equals(name, field, StringComparison.Ordinal)) return value;
        }
        return null;
    }

    private static string Describe(ArchiveEditing.WriteResult result) =>
        $"added={result.Added.Count} replaced={result.Replaced.Count} refused={result.Refused.Count}"
        + (result.Refused.Count > 0 ? " (" + string.Join("; ", result.Refused.Select(r => r.Reason)) + ")" : "");
}
