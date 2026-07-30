using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Formats.Actors;
using Illusion.Formats.Archive;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What an archive's actors looked like BEFORE the edits — read straight out of the versioned backups the
/// toolkit takes on every build.
///
/// It answers the question a viewport cannot: "did I move that, or did the toolkit?". An actor whose position
/// differs from the oldest backup was moved by something, and knowing whether the something was the user or a
/// duplicate is the difference between a bug and a mis-drag.
/// </summary>
internal static class ActorHistoryProbes
{
    /// <summary>Output: %TEMP%\illusion_actor_history.txt</summary>
    internal static void RunActorHistoryProbe(string district, string? nameFilter)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_actor_history.txt");
        var sb = new StringBuilder();

        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string live = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(live)) { sb.AppendLine("no such district: " + live); return; }

            string backupDir = Path.Combine(MafiaEnvironment.CityFolder, "backups");
            List<string> backups = Directory.Exists(backupDir)
                ? [.. Directory.GetFiles(backupDir, district + "_*.sds").OrderBy(p => p, StringComparer.Ordinal)]
                : [];

            sb.AppendLine($"ACTOR HISTORY — {district}, {backups.Count} backup(s)\n");

            // The axis measurement below needs only the live archive, so it runs even with no history at all —
            // "which way is up" is worth answering for any district, not just an edited one.
            Dictionary<string, ActorEntry> oldest = backups.Count > 0 ? ActorsOf(backups[0]) : [];
            Dictionary<string, ActorEntry> now = ActorsOf(live);
            sb.AppendLine(backups.Count == 0
                ? "no backups for this district — nothing to compare against, axes only"
                : $"oldest backup: {Path.GetFileName(backups[0])} — {oldest.Count} actors");
            sb.AppendLine($"live archive:  {Path.GetFileName(live)} — {now.Count} actors\n");

            int moved = 0, added = 0, removed = 0;
            foreach ((string name, ActorEntry actor) in now)
            {
                if (!oldest.TryGetValue(name, out ActorEntry? was)) { added++; continue; }
                if ((actor.Position - was.Position).Length() <= 1e-4f) continue;
                moved++;
                if (nameFilter == null || name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"MOVED  '{name}': {Fmt(was.Position)} → {Fmt(actor.Position)} "
                        + $"(Δ {Fmt(actor.Position - was.Position)})");
                }
            }
            foreach (string name in oldest.Keys)
            {
                if (!now.ContainsKey(name)) removed++;
            }

            sb.AppendLine();
            sb.AppendLine($"{moved} actor(s) moved since the oldest backup, {added} added, {removed} gone");

            // Which component of a position is the game's VERTICAL. Nothing in the file says so, but the world
            // does: a district is roughly two kilometres across and a few dozen metres tall, so the axis whose
            // actors span the least is up. Worth stating in numbers — an axis guessed from the gizmo's labels
            // is how "I moved it along Z and it went sideways" happens.
            if (now.Count > 0)
            {
                var min = new System.Numerics.Vector3(float.MaxValue);
                var max = new System.Numerics.Vector3(float.MinValue);
                foreach (ActorEntry actor in now.Values)
                {
                    min = System.Numerics.Vector3.Min(min, actor.Position);
                    max = System.Numerics.Vector3.Max(max, actor.Position);
                }
                System.Numerics.Vector3 span = max - min;
                string[] names = ["X (first)", "Y (second)", "Z (third)"];
                float[] spans = [span.X, span.Y, span.Z];
                int shortest = 0;
                for (int i = 1; i < 3; i++) if (spans[i] < spans[shortest]) shortest = i;
                sb.AppendLine();
                sb.AppendLine($"actor position spread: X {span.X:0.0}, Y {span.Y:0.0}, Z {span.Z:0.0} "
                    + $"→ the shortest is {names[shortest]}, so THAT is the vertical axis in the file");
                sb.AppendLine($"    bounds: {Fmt(min)} .. {Fmt(max)}");
            }

            // The other half of "did it move": the FRAME objects. An actor's position is only where its
            // prototype is put; what the eye finally sees is that prototype's own transform on top. An object
            // nobody edited whose local transform differs from the backup has been moved by the toolkit, and
            // that is a bug rather than a mis-drag — so it is worth knowing before blaming the hand.
            if (backups.Count > 0) CompareFrames(sb, backups[0], live);

            if (nameFilter != null)
            {
                sb.AppendLine();
                sb.AppendLine($"every actor whose name contains '{nameFilter}', now vs each backup:");
                foreach (string name in now.Keys.Where(n => n.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(n => n, StringComparer.Ordinal))
                {
                    sb.AppendLine($"  {name}");
                    foreach (string backup in backups)
                    {
                        Dictionary<string, ActorEntry> old = ActorsOf(backup);
                        sb.AppendLine($"      {Path.GetFileNameWithoutExtension(backup)[(district.Length + 1)..]}: "
                            + (old.TryGetValue(name, out ActorEntry? e) ? Fmt(e.Position) : "(not present)"));
                    }
                    sb.AppendLine($"      LIVE: {Fmt(now[name].Position)}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // Every frame object's own transform, then and now. Objects are matched by their position in the object
    // list AND by name: an insertion shifts every row after it, so the row alone would report the whole tail
    // as changed, and the name alone cannot tell apart the 235 objects a district calls 'Dummy01'.
    private static void CompareFrames(StringBuilder sb, string backup, string live)
    {
        List<(string Name, System.Numerics.Vector3 Local)> was = FramesOf(backup);
        List<(string Name, System.Numerics.Vector3 Local)> now = FramesOf(live);
        sb.AppendLine();
        sb.AppendLine($"frame objects: {was.Count} in the backup, {now.Count} now");

        // Walk both in order, letting the newer side skip over objects the older one does not have.
        int i = 0, j = 0, compared = 0, moved = 0, skipped = 0;
        var examples = new List<string>();
        while (i < was.Count && j < now.Count)
        {
            if (!string.Equals(was[i].Name, now[j].Name, StringComparison.Ordinal))
            {
                j++;
                skipped++;
                continue;
            }
            compared++;
            if ((was[i].Local - now[j].Local).Length() > 1e-4f)
            {
                moved++;
                if (examples.Count < 8)
                {
                    examples.Add($"'{was[i].Name}' (row {i} → {j}): {Fmt(was[i].Local)} → {Fmt(now[j].Local)}");
                }
            }
            i++;
            j++;
        }
        sb.AppendLine($"{compared} matched by name in order, {moved} of them MOVED, {skipped} row(s) inserted");
        foreach (string line in examples) sb.AppendLine("    " + line);
        if (moved == 0)
        {
            sb.AppendLine("    nothing the toolkit did moved an object that was already there.");
        }
    }

    private static List<(string Name, System.Numerics.Vector3 Local)> FramesOf(string sds)
    {
        var frames = new List<(string, System.Numerics.Vector3)>();
        SdsArchive archive = SdsArchive.Open(sds);
        foreach (ResourceEntry entry in archive.Entries)
        {
            if (entry.Data == null || entry.TypeId < 0 || entry.TypeId >= archive.ResourceTypes.Count) continue;
            if (!string.Equals(archive.ResourceTypes[entry.TypeId].Name, "FrameResource", StringComparison.Ordinal))
            {
                continue;
            }
            var resource = new Formats.Frames.FrameResource();
            using var stream = new MemoryStream(entry.Data, writable: false);
            resource.ReadFromFile(stream);
            foreach (object value in resource.FrameObjects.Values)
            {
                if (value is Formats.Frames.ObjectTypes.FrameObjectBase frame)
                {
                    frames.Add((frame.Name?.String ?? "", frame.LocalTransform.Translation));
                }
            }
        }
        return frames;
    }

    // Every actor of an archive, by entity name, read straight from the packed .sds without extracting it.
    private static Dictionary<string, ActorEntry> ActorsOf(string sds)
    {
        var actors = new Dictionary<string, ActorEntry>(StringComparer.Ordinal);
        SdsArchive archive = SdsArchive.Open(sds);
        foreach (ResourceEntry entry in archive.Entries)
        {
            if (entry.Data == null || entry.TypeId < 0 || entry.TypeId >= archive.ResourceTypes.Count) continue;
            if (!string.Equals(archive.ResourceTypes[entry.TypeId].Name, "Actors", StringComparison.Ordinal)) continue;
            using var stream = new MemoryStream(entry.Data, writable: false);
            foreach (ActorEntry actor in ActorsFile.Read(stream).Actors)
            {
                actors[actor.EntityName] = actor;
            }
        }
        return actors;
    }

    private static string Fmt(System.Numerics.Vector3 v) => $"<{v.X:0.00}, {v.Y:0.00}, {v.Z:0.00}>";
}
