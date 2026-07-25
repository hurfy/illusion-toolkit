using System.IO;
using System.Text;
using Illusion.Assets;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Twin-free verification of the native small-format codecs: item descriptions,
/// actor packs, the NAV pair, the translokator and the city streaming tables.</summary>
internal static class NativeMiscProbes
{
    /// <summary>
    /// Every .ids/.act/.nav/.nov of the extracted install re-saved byte-identically to disk;
    /// every .tra/cityareas.bin/StreamMap read without errors and with plausible content.
    /// Output: %TEMP%\illusion_native_misc.txt
    /// </summary>
    internal static void RunMiscParityProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_native_misc.txt");
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
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("INIT FAIL: " + err);
                return;
            }
            string root = MafiaEnvironment.ResourcesFolder!;
            if (!Directory.Exists(root))
            {
                sb.AppendLine("resources not unpacked: " + root);
                return;
            }
            var details = new StringBuilder();

            RunFixpointFamily("ids", Directory.GetFiles(root, "*.ids", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.ItemDesc.ItemDescFile parsed = Formats.ItemDesc.ItemDescFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("act", Directory.GetFiles(root, "*.act", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Actors.ActorsFile parsed = Formats.Actors.ActorsFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("nav", Directory.GetFiles(root, "*.nav", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Navigation.AiWorldFile parsed = Formats.Navigation.AiWorldFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("nov", Directory.GetFiles(root, "*.nov", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Navigation.ObjDataFile parsed = Formats.Navigation.ObjDataFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("spe", Directory.GetFiles(root, "*.spe", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Speech.SpeechFile parsed = Formats.Speech.SpeechFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("an2", Directory.GetFiles(root, "*.an2", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Animations.Animation2File parsed = Formats.Animations.Animation2File.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("ifl", Directory.GetFiles(root, "*.ifl", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Textures.AnimatedTextureFile parsed = Formats.Textures.AnimatedTextureFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("eds", Directory.GetFiles(root, "*.eds", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.EntityData.EntityDataStorageFile parsed = Formats.EntityData.EntityDataStorageFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("cut", Directory.GetFiles(root, "*.cut", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Cutscene.CutsceneFile parsed = Formats.Cutscene.CutsceneFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("prf", Directory.GetFiles(root, "*.prf", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Prefab.PrefabFile parsed = Formats.Prefab.PrefabFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("entityactivator", Directory.GetFiles(root, "EntityActivator.bin", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.EntityActivator.EntityActivatorFile parsed = Formats.EntityActivator.EntityActivatorFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("tapindices", Directory.GetFiles(root, "TAPIndices.bin", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Navigation.TapIndicesFile parsed = Formats.Navigation.TapIndicesFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("fas", Directory.GetFiles(root, "*.fas", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.FaceFx.FaceFxAnimSetFile parsed = Formats.FaceFx.FaceFxAnimSetFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("fxa", Directory.GetFiles(root, "*.fxa", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.FaceFx.FaceFxActorFile parsed = Formats.FaceFx.FaceFxActorFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("atp", Directory.GetFiles(root, "*.atp", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Navigation.AnimalTrafficPathsFile parsed = Formats.Navigation.AnimalTrafficPathsFile.Read(stream);
                    return parsed.ToBytes();
                });

            {
                string[] datFiles = Directory.GetFiles(root, "*.dat", SearchOption.AllDirectories);
                int datTyped = 0;
                RunFixpointFamily("dat", datFiles, Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Text.TextDatabaseFile parsed = Formats.Text.TextDatabaseFile.Read(stream);
                    if (parsed.IsTyped) datTyped++;
                    return parsed.ToBytes();
                });
                if (datFiles.Length > 0)
                    Check("dat mostly typed (not opaque fallback)", datTyped > datFiles.Length / 2,
                        $"{datTyped}/{datFiles.Length} typed");
            }

            RunFixpointFamily("eff", Directory.GetFiles(root, "*.eff", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Effects.EffectsFile parsed = Formats.Effects.EffectsFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("tyres", Directory.GetFiles(root, "Tyres.bin", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Tyres.TyresFile parsed = Formats.Tyres.TyresFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("cityshops", Directory.GetFiles(root, "CityShops.bin", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.City.CityShopsFile parsed = Formats.City.CityShopsFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("shopmenu2", Directory.GetFiles(root, "ShopMenu2.bin", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.City.ShopMenu2File parsed = Formats.City.ShopMenu2File.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("gsd", Directory.GetFiles(root, "*.gsd", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Navigation.RoadmapFile parsed = Formats.Navigation.RoadmapFile.Read(stream);
                    return parsed.ToBytes();
                });

            RunFixpointFamily("nhv", Directory.GetFiles(root, "*.nhv", SearchOption.AllDirectories),
                Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Navigation.NavHpdFile parsed = Formats.Navigation.NavHpdFile.Read(stream);
                    return parsed.ToBytes();
                });

            {
                string[] stblFiles = Directory.GetFiles(root, "*.stbl", SearchOption.AllDirectories);
                int stblTyped = 0;
                RunFixpointFamily("stbl", stblFiles, Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Sound.SoundTableFile parsed = Formats.Sound.SoundTableFile.Read(stream);
                    if (parsed.IsTyped) stblTyped++;
                    return parsed.ToBytes();
                });
                if (stblFiles.Length > 0)
                    Check("stbl mostly typed (not opaque fallback)", stblTyped > stblFiles.Length / 2,
                        $"{stblTyped}/{stblFiles.Length} typed");
            }

            {
                string[] ssFiles = Directory.GetFiles(root, "soundsectors_*.bin", SearchOption.AllDirectories);
                int ssTyped = 0;
                RunFixpointFamily("soundsectors", ssFiles, Check, details, file =>
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                    Formats.Sound.SoundSectorFile parsed = Formats.Sound.SoundSectorFile.Read(stream);
                    if (parsed.IsTyped) ssTyped++;
                    return parsed.ToBytes();
                });
                Check("soundsectors mostly typed (not opaque fallback)", ssTyped > ssFiles.Length / 2,
                    $"{ssTyped}/{ssFiles.Length} typed");
            }

            // .tra — read-only: every file must parse and carry instances.
            {
                string[] files = Directory.GetFiles(root, "*.tra", SearchOption.AllDirectories);
                int parsed = 0, errors = 0;
                long instances = 0;
                foreach (string file in files)
                {
                    try
                    {
                        var loader = new Formats.Translokator.TranslokatorLoader();
                        using var stream = new MemoryStream(File.ReadAllBytes(file), writable: false);
                        using var reader = new BinaryReader(stream);
                        loader.ReadFromFile(reader);
                        foreach (Formats.Translokator.ObjectGroup group in loader.ObjectGroups)
                        {
                            foreach (Formats.Translokator.Object obj in group.Objects)
                            {
                                instances += obj.Instances.Length;
                            }
                        }
                        parsed++;
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        details.AppendLine($"TRA ERROR {Path.GetFileName(file)}: {ex.Message}");
                    }
                }
                Check("tra reads", errors == 0 && parsed == files.Length && instances > 0,
                    $"{parsed}/{files.Length} ({instances} instances)");
            }

            // cityareas.bin — read-only.
            {
                string[] files = Directory.GetFiles(root, "cityareas.bin", SearchOption.AllDirectories);
                int parsed = 0, errors = 0;
                long areas = 0;
                foreach (string file in files)
                {
                    try
                    {
                        Formats.CityAreas.CityAreasFile city = Formats.CityAreas.CityAreasFile.Load(file);
                        areas += city.Areas.Count;
                        parsed++;
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        details.AppendLine($"CITYAREAS ERROR {file}: {ex.Message}");
                    }
                }
                Check("cityareas reads", errors == 0 && parsed == files.Length && areas > 0,
                    $"{parsed}/{files.Length} ({areas} areas)");
            }

            // StreamMap*.bin — read-only.
            {
                string tables = Path.Combine(MafiaEnvironment.GameRoot, "edit", "tables");
                string[] files = Directory.Exists(tables)
                    ? Directory.GetFiles(tables, "StreamMap*.bin", SearchOption.TopDirectoryOnly)
                    : [];
                int parsed = 0, errors = 0;
                long loaders = 0;
                foreach (string file in files)
                {
                    try
                    {
                        Formats.StreamMap.StreamMapFile map = Formats.StreamMap.StreamMapFile.Load(file);
                        loaders += map.Loaders.Length;
                        parsed++;
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        details.AppendLine($"STREAMMAP ERROR {Path.GetFileName(file)}: {ex.Message}");
                    }
                }
                Check("streammap reads", errors == 0 && parsed == files.Length && loaders > 0,
                    $"{parsed}/{files.Length} ({loaders} loaders)");
            }

            if (details.Length > 0)
            {
                sb.AppendLine();
                sb.Append(details);
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
        sb.Insert(0, $"NATIVE MISC PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    // Shared: parse + re-save every file of one family; the output must equal the disk bytes.
    private static void RunFixpointFamily(string label, string[] files,
        Action<string, bool, string> check, StringBuilder details, Func<string, byte[]> roundtrip)
    {
        int fixpoint = 0, errors = 0;
        foreach (string file in files)
        {
            try
            {
                byte[] original = File.ReadAllBytes(file);
                byte[] rewritten = roundtrip(file);
                if (rewritten.AsSpan().SequenceEqual(original)) fixpoint++;
                else if (details.Length < 8000)
                {
                    details.AppendLine($"{label.ToUpperInvariant()} FIXPOINT DIFF {Path.GetFileName(file)} at {FirstDiff(original, rewritten)}");
                }
            }
            catch (Exception ex)
            {
                errors++;
                if (details.Length < 8000)
                {
                    details.AppendLine($"{label.ToUpperInvariant()} ERROR {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        check($"{label} re-save is byte-identical to disk", errors == 0 && fixpoint == files.Length,
            $"{fixpoint}/{files.Length}");
    }
}
