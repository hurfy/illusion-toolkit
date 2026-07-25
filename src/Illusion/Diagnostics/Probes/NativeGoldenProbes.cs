using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Illusion.Assets;
using Illusion.Formats.Native;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The golden snapshot (P6 gate 1): a per-archive/per-folder hash of the DECODED neutral model,
/// taken while the dual-path is still alive and committed to the repo. After the C# reference
/// decoders are deleted, <c>check</c> catches any behavior drift of the native codecs — including
/// mirrored read/write bugs a roundtrip cannot see.
/// </summary>
internal static class NativeGoldenProbes
{
    private const string SnapshotRelPath = @"docs\golden-snapshot.txt";

    /// <summary>
    /// snap: recompute every hash and (re)write the snapshot file in the repo.
    /// check: recompute and diff against the committed snapshot.
    /// Output: %TEMP%\illusion_golden.txt
    /// </summary>
    internal static void RunGoldenProbe(string mode, string? repoRoot)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_golden.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
            {
                sb.AppendLine("USAGE: --probe-golden snap|check <repoRoot>");
                return;
            }
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("INIT FAIL: " + err);
                return;
            }

            SortedDictionary<string, string> lines = ComputeLines(sb);
            string snapshotPath = Path.Combine(repoRoot, SnapshotRelPath);

            if (mode == "snap")
            {
                var text = new StringBuilder();
                text.AppendLine("# Golden snapshot of the decoded neutral model (see --probe-golden).");
                text.AppendLine("# Regenerate ONLY on an intentional schema/codec change, never to silence a diff.");
                foreach ((string key, string value) in lines)
                {
                    text.AppendLine($"{key} {value}");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
                File.WriteAllText(snapshotPath, text.ToString());
                Check("snapshot written", true, $"{lines.Count} entries → {snapshotPath}");
            }
            else
            {
                Check("the committed snapshot exists", File.Exists(snapshotPath), snapshotPath);
                var stored = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string line in File.ReadAllLines(snapshotPath))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    int at = line.LastIndexOf(' ');
                    if (at > 0) stored[line[..at]] = line[(at + 1)..];
                }

                int matched = 0;
                var diffs = new StringBuilder();
                foreach ((string key, string value) in lines)
                {
                    if (!stored.TryGetValue(key, out string? want))
                    {
                        diffs.AppendLine($"NEW {key}");
                    }
                    else if (!string.Equals(want, value, StringComparison.Ordinal))
                    {
                        diffs.AppendLine($"DRIFT {key}: {want} -> {value}");
                    }
                    else matched++;
                }
                foreach (string key in stored.Keys)
                {
                    if (!lines.ContainsKey(key)) diffs.AppendLine($"MISSING {key}");
                }

                Check("every entry matches the snapshot",
                    matched == lines.Count && stored.Count == lines.Count,
                    $"{matched}/{lines.Count} (stored {stored.Count})");
                if (diffs.Length > 0)
                {
                    sb.AppendLine();
                    sb.Append(diffs);
                }
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sw.Stop();
        sb.AppendLine();
        sb.AppendLine($"elapsed: {sw.Elapsed.TotalSeconds:F1} s");
        sb.Insert(0, $"GOLDEN PROBE ({mode}): {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    // Every hashable unit, keyed for stable sorting:
    //   sds <rel>  — hash of the archive's decoded container wire
    //   dir <top>  — combined hash of every decoded format file under that extracted folder
    //   mtl <name> — hash of the decoded material library wire
    //   tab <name> — hash of the decoded StreamMap wire
    private static SortedDictionary<string, string> ComputeLines(StringBuilder sb)
    {
        var result = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        string gameRoot = MafiaEnvironment.GameRoot;
        string root = MafiaEnvironment.ResourcesFolder!;

        // 1) Archives: the container decode — the P3 corpus (the whole install, not just pc\sds).
        List<string> archives = [.. Illusion.Assets.Sds.ResourceUnpacker.EnumerateGameSds()
            .Where(f => !f.FullName.Contains(@"\.mafiahub\", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.FullName)];
        Parallel.ForEach(archives, file =>
        {
            string rel = Path.GetRelativePath(gameRoot, file).Replace('\\', '/').ToLowerInvariant();
            result["sds " + rel] = HashWire(File.ReadAllBytes(file), "sds");
        });
        sb.AppendLine($"       archives: {archives.Count}");

        // 2) Extracted format files, combined per containing folder (one line per extracted
        // archive folder keeps a drift localized).
        var byDir = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(root))
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (KindOf(file) == null) continue;
                string dir = Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
                if (!byDir.TryGetValue(dir, out List<string>? list))
                {
                    byDir[dir] = list = [];
                }
                list.Add(file);
            }
        }
        long formatFiles = byDir.Values.Sum(l => l.Count);
        Parallel.ForEach(byDir, pair =>
        {
            var combined = new StringBuilder();
            foreach (string file in pair.Value.OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                combined.Append(Path.GetFileName(file)).Append(':')
                    .Append(HashWire(File.ReadAllBytes(file), KindOf(file)!)).Append('\n');
            }
            result["dir " + pair.Key.Replace('\\', '/').ToLowerInvariant()] =
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(combined.ToString())));
        });
        sb.AppendLine($"       format files: {formatFiles} in {byDir.Count} folders");

        // 3) The loose material libraries and the streaming timeline.
        string materials = Path.Combine(MafiaEnvironment.GameRoot, "edit", "materials");
        if (Directory.Exists(materials))
        {
            foreach (string file in Directory.GetFiles(materials, "*.mtl"))
            {
                result["mtl " + Path.GetFileName(file).ToLowerInvariant()] =
                    HashWire(File.ReadAllBytes(file), "mtl");
            }
        }
        string tables = Path.Combine(MafiaEnvironment.GameRoot, "edit", "tables");
        if (Directory.Exists(tables))
        {
            foreach (string file in Directory.GetFiles(tables, "StreamMap*.bin"))
            {
                result["tab " + Path.GetFileName(file).ToLowerInvariant()] =
                    HashWire(File.ReadAllBytes(file), "tab");
            }
        }

        return new SortedDictionary<string, string>(
            result.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);
    }

    private static string? KindOf(string file)
    {
        string name = Path.GetFileName(file);
        if (name.Equals("cityareas.bin", StringComparison.OrdinalIgnoreCase)) return "city";
        if (name.Equals("entityactivator.bin", StringComparison.OrdinalIgnoreCase)) return "entityactivator";
        if (name.Equals("tapindices.bin", StringComparison.OrdinalIgnoreCase)) return "tapindices";
        if (name.Equals("tyres.bin", StringComparison.OrdinalIgnoreCase)) return "tyres";
        if (name.Equals("cityshops.bin", StringComparison.OrdinalIgnoreCase)) return "cityshops";
        if (name.Equals("shopmenu2.bin", StringComparison.OrdinalIgnoreCase)) return "shopmenu2";
        if (name.StartsWith("soundsectors_", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) return "soundsectors";
        return Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".fnt" => "fnt",
            ".fr" => "fr",
            ".ibp" => "ibp",
            ".vbp" => "vbp",
            ".col" => "col",
            ".ids" => "ids",
            ".act" => "act",
            ".nav" => "nav",
            ".nov" => "nov",
            ".spe" => "speech",
            ".an2" => "anim2",
            ".ifl" => "animtex",
            ".eds" => "eds",
            ".cut" => "cutscene",
            ".prf" => "prefab",
            ".fas" => "fas",
            ".fxa" => "fxa",
            ".atp" => "atp",
            ".dat" => "dat",
            ".eff" => "eff",
            ".gsd" => "gsd",
            ".nhv" => "nhv",
            ".stbl" => "stbl",
            ".tra" => "tra",
            ".mtl" => "mtl",
            _ => null,
        };
    }

    // Decodes the file through the native codec and hashes the resulting model wire; a refusal
    // is recorded as a deterministic ERR marker (behavior is part of the contract too).
    private static unsafe string HashWire(byte[] bytes, string kind)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = bytes)
        {
            ulong len = (ulong)bytes.Length;
            status = kind switch
            {
                "sds" => Formats.Native.Archive.SdsNativeMethods.Load(p, len, out raw),
                "fnt" => Formats.Native.Frames.FramesNativeMethods.NameTableLoad(p, len, out raw),
                "fr" => Formats.Native.Frames.FramesNativeMethods.FrameResourceLoad(p, len, out raw),
                "ibp" => Formats.Native.Frames.FramesNativeMethods.IndexPoolLoad(p, len, out raw),
                "vbp" => Formats.Native.Frames.FramesNativeMethods.VertexPoolLoad(p, len, out raw),
                "col" => Formats.Native.Collisions.ColNativeMethods.Load(p, len, out raw),
                "ids" => Formats.Native.Misc.MiscNativeMethods.IdsLoad(p, len, out raw),
                "act" => Formats.Native.Misc.MiscNativeMethods.ActLoad(p, len, out raw),
                "nav" => Formats.Native.Misc.MiscNativeMethods.NavAiWorldLoad(p, len, out raw),
                "nov" => Formats.Native.Misc.MiscNativeMethods.NavObjDataLoad(p, len, out raw),
                "speech" => Formats.Native.Misc.MiscNativeMethods.SpeechLoad(p, len, out raw),
                "anim2" => Formats.Native.Misc.MiscNativeMethods.Anim2Load(p, len, out raw),
                "animtex" => Formats.Native.Misc.MiscNativeMethods.AnimTexLoad(p, len, out raw),
                "eds" => Formats.Native.Misc.MiscNativeMethods.EdsLoad(p, len, out raw),
                "cutscene" => Formats.Native.Misc.MiscNativeMethods.CutsceneLoad(p, len, out raw),
                "prefab" => Formats.Native.Misc.MiscNativeMethods.PrefabLoad(p, len, out raw),
                "entityactivator" => Formats.Native.Misc.MiscNativeMethods.EntityActivatorLoad(p, len, out raw),
                "tapindices" => Formats.Native.Misc.MiscNativeMethods.TapIndicesLoad(p, len, out raw),
                "soundsectors" => Formats.Native.Misc.MiscNativeMethods.SoundSectorsLoad(p, len, out raw),
                "fas" => Formats.Native.Misc.MiscNativeMethods.FasLoad(p, len, out raw),
                "fxa" => Formats.Native.Misc.MiscNativeMethods.FxaLoad(p, len, out raw),
                "atp" => Formats.Native.Misc.MiscNativeMethods.AtpLoad(p, len, out raw),
                "dat" => Formats.Native.Misc.MiscNativeMethods.DatLoad(p, len, out raw),
                "eff" => Formats.Native.Misc.MiscNativeMethods.EffLoad(p, len, out raw),
                "gsd" => Formats.Native.Misc.MiscNativeMethods.GsdLoad(p, len, out raw),
                "nhv" => Formats.Native.Misc.MiscNativeMethods.NhvLoad(p, len, out raw),
                "stbl" => Formats.Native.Misc.MiscNativeMethods.StblLoad(p, len, out raw),
                "tyres" => Formats.Native.Misc.MiscNativeMethods.TyresLoad(p, len, out raw),
                "cityshops" => Formats.Native.Misc.MiscNativeMethods.CityShopsLoad(p, len, out raw),
                "shopmenu2" => Formats.Native.Misc.MiscNativeMethods.ShopMenu2Load(p, len, out raw),
                "tra" => Formats.Native.Misc.MiscNativeMethods.TraLoad(p, len, out raw),
                "city" => Formats.Native.Misc.MiscNativeMethods.CityAreasLoad(p, len, out raw),
                "tab" => Formats.Native.Misc.MiscNativeMethods.StreamMapLoad(p, len, out raw),
                "mtl" => Formats.Native.Materials.MtlNativeMethods.MtlLoad(p, len, out raw),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };
        }
        using var buffer = new MfBuffer(raw);
        if (status != NativeMethods.Ok)
        {
            return "ERR:" + status;
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }
}
