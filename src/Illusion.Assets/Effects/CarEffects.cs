using Illusion.Assets.EntityData;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Effects;

namespace Illusion.Assets.Effects;

/// <summary>
/// The effects ONE archive carries, as rows a panel can show and write back.
///
/// <para>
/// A car ships exactly two of its own — its fire and its rain — and its tuning is what says which is which
/// (<c>FireID</c> and <c>RainID</c> hold the ids the file defines). Everything else a car names — the
/// exhaust smoke, the explosion, the crash, the sparks, the breaking glass — resolves in the shared
/// particle library instead and is deliberately out of reach here: this is the archive's own data, and
/// editing the library would change every car in the game at once.
/// </para>
/// <para>
/// Values are addressed by byte offset into the file's own buffer, so a write changes four bytes and
/// nothing else — every part of the format this walk does not understand survives untouched.
/// </para>
/// </summary>
public sealed class CarEffects
{
    // The schema, as far as the walk goes. Tags are local to their parent, so each of these is only
    // meaningful at the depth it is used.
    private const uint FramesTag = 1;
    private const uint GenerationsTag = 2;
    private const uint GenerationTag = 200;
    private const uint OperatorTag = 300;
    private const uint ParamsTag = 350;
    private const uint CurveTag = 123;
    private const uint KeyTag = 124;
    private const uint NameTag = 0;
    private const uint SoundTag = 400;

    private CarEffects(string path, EffectsTree tree, IReadOnlyList<EffectView> effects)
    {
        Path = path;
        Tree = tree;
        Effects = effects;
    }

    /// <summary>The .eff in the archive's extracted folder — what a save writes.</summary>
    public string Path { get; }

    /// <summary>The parsed file. Edits go through it.</summary>
    public EffectsTree Tree { get; }

    /// <summary>The archive's own effects, in file order.</summary>
    public IReadOnlyList<EffectView> Effects { get; }

    /// <summary>How many values the whole tab can edit — the headline.</summary>
    public int ValueCount => Effects.Sum(e => e.ValueCount);

    /// <summary>Reads an archive's effects, or null when it carries none. Opens files: background thread.</summary>
    public static CarEffects? Read(FileInfo archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return ReadFrom(MafiaEnvironment.ExtractedDir(archive), archive);
    }

    /// <summary>The same, from a working copy that is not the archive's own — what the probes read.</summary>
    public static CarEffects? ReadFrom(string extracted, FileInfo? archive = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(extracted);

        IReadOnlyList<string> files;
        try { files = SdsManifest.Load(extracted).GetFiles("Effects"); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { return null; }
        if (files.Count == 0) return null;

        string path = files[0];
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (IOException) { return null; }

        EffectsTree? tree = EffectsTree.Read(bytes);
        if (tree == null) return null;

        return new CarEffects(path, tree, Build(tree, Roles(archive)));
    }

    /// <summary>Writes the edited bytes back to the extracted folder, where the packer picks them up.</summary>
    public void Save() => File.WriteAllBytes(Path, Tree.Bytes);

    /// <summary>
    /// The lowest id no effect in this archive uses, at or above the ids it already has — what a new effect
    /// should be numbered. Measured across the shipped game: a car's ids never appear outside the cars, and
    /// inside the cars only on a variant of the same car, so a number free among THESE is the requirement.
    /// </summary>
    public uint NextFreeId()
    {
        var used = Effects.Select(e => e.Id).ToHashSet();
        uint next = used.Count == 0 ? 1u : used.Max() + 1;
        while (used.Contains(next)) next++;
        return next;
    }

    /// <summary>
    /// Adds a copy of one of the archive's effects under a fresh id, and returns the file that results.
    /// Copying rather than minting: an effect is a tree of generations, operators and curves that has to be
    /// coherent to render at all, and the only coherent one available is one that already works.
    /// </summary>
    public byte[] BytesWithCopyOf(uint id)
    {
        EffectView source = Effects.FirstOrDefault(e => e.Id == id)
            ?? throw new ArgumentException($"this archive has no effect {id}", nameof(id));
        return Tree.WithEffectCopied(source.Chunk, NextFreeId());
    }

    // ── the walk ──

    private static IReadOnlyList<EffectView> Build(EffectsTree tree, IReadOnlyDictionary<uint, string> roles)
    {
        var built = new List<EffectView>();
        foreach (EffectChunk effect in tree.Effects)
        {
            if (effect.Length < 8) continue;
            uint id = tree.IdOf(effect);

            var generations = new List<EffectGenerationView>();
            int frames = 0, sounds = 0;

            // An effect's payload opens with two words — version and id — and only then its chunks, so the
            // walk starts eight bytes in rather than at the payload's head.
            foreach (EffectChunk part in EffectsTree.Tile(tree.Bytes, effect.Start + 8, effect.Length - 8) ?? [])
            {
                if (part.Tag == FramesTag) { frames += part.Where(100).Count(); continue; }
                if (part.Tag == GenerationsTag)
                {
                    int index = 0;
                    foreach (EffectChunk generation in part.Where(GenerationTag))
                    {
                        generations.Add(Generation(tree, generation, index++));
                    }
                    continue;
                }
                sounds += part.Where(SoundTag).Count();
            }

            string role = roles.TryGetValue(id, out string? named) ? named : "";
            built.Add(new EffectView(id, role, effect, frames, sounds, generations));
        }
        return built;
    }

    private static EffectGenerationView Generation(EffectsTree tree, EffectChunk generation, int index)
    {
        string? name = null;
        var operators = new List<EffectOperatorView>();

        foreach (EffectChunk part in generation.Children ?? [])
        {
            if (part.Tag == NameTag) { name ??= Text(tree, part); continue; }
            foreach (EffectChunk op in part.Where(OperatorTag))
            {
                if (op.Length >= 5) operators.Add(Operator(tree, op));
            }
        }

        return new EffectGenerationView(
            string.IsNullOrEmpty(name) ? $"Generation {index + 1}" : name, operators);
    }

    private static EffectOperatorView Operator(EffectsTree tree, EffectChunk op)
    {
        uint type = (uint)tree.ReadInt(op.Start);
        bool enabled = tree.ReadByte(op.Start + 4) != 0;
        var parameters = new List<EffectParamView>();

        // The operator's payload opens with its type word and the enabled byte; its chunks follow.
        List<EffectChunk>? kids = EffectsTree.Tile(tree.Bytes, op.Start + 5, op.Length - 5);
        foreach (EffectChunk block in (kids ?? []).Where(k => k.Tag == ParamsTag))
        {
            foreach (EffectChunk param in block.Children ?? [])
            {
                parameters.Add(Param(tree, type, param));
            }
        }

        return new EffectOperatorView(EffectNames.Operator(type), type, enabled, op.Start + 4, parameters);
    }

    private static EffectParamView Param(EffectsTree tree, uint operatorType, EffectChunk param)
    {
        string title = EffectNames.Parameter(operatorType, param.Tag);

        // An animated parameter carries a curve; the curve sits behind a short prefix this walk does not
        // read, so it is looked for at the two places the shipped files put it.
        EffectChunk? curve = Curve(tree, param);
        if (curve != null)
        {
            var keys = new List<EffectValueRow>();
            int index = 0;
            foreach (EffectChunk key in Keys(tree, curve))
            {
                int floats = Math.Min(key.Length / 4, 4);
                for (int i = 0; i < floats; i++)
                {
                    // The first float of a key is when it happens; the rest are what it is worth then.
                    string label = i == 0 ? $"key {index + 1} at" : $"key {index + 1} value{Suffix(i, floats)}";
                    keys.Add(new EffectValueRow(label, key.Start + (i * 4), tree.ReadFloat(key.Start + (i * 4))));
                }
                index++;
            }
            return new EffectParamView(title, index == 0 ? "curve (empty)" : $"curve, {index} key(s)", keys);
        }

        var rows = new List<EffectValueRow>();
        int count = Math.Min(param.Length / 4, 8);
        for (int i = 0; i < count; i++)
        {
            rows.Add(new EffectValueRow(
                count == 1 ? title : $"{title}{Suffix(i, count)}",
                param.Start + (i * 4),
                tree.ReadFloat(param.Start + (i * 4))));
        }
        return new EffectParamView(title, rows.Count == 0 ? "(empty)" : "", rows);
    }

    private static EffectChunk? Curve(EffectsTree tree, EffectChunk param)
    {
        foreach (int prefix in (int[])[0, 8, 4])
        {
            if (prefix > param.Length) continue;
            List<EffectChunk>? kids = EffectsTree.Tile(tree.Bytes, param.Start + prefix, param.Length - prefix);
            EffectChunk? curve = kids?.FirstOrDefault(k => k.Tag == CurveTag);
            if (curve != null) return curve;
        }
        return null;
    }

    /// <summary>The keys of a curve. Its payload opens with their count, and nested curves are flattened —
    /// a colour is three curves under one parameter, and they read as one list of keys.</summary>
    private static IEnumerable<EffectChunk> Keys(EffectsTree tree, EffectChunk curve)
    {
        foreach (int prefix in (int[])[4, 0])
        {
            List<EffectChunk>? kids = EffectsTree.Tile(tree.Bytes, curve.Start + prefix, curve.Length - prefix);
            if (kids == null) continue;
            foreach (EffectChunk kid in kids)
            {
                if (kid.Tag == KeyTag) yield return kid;
                else if (kid.Tag == CurveTag)
                {
                    foreach (EffectChunk nested in Keys(tree, kid)) yield return nested;
                }
            }
            yield break;
        }
    }

    private static string Suffix(int index, int count) =>
        count is 2 or 3 or 4 ? " " + "xyzw"[index] : $" {index + 1}";

    private static string? Text(EffectsTree tree, EffectChunk chunk)
    {
        if (chunk.Length < 4) return null;
        int count = tree.ReadInt(chunk.Start);
        if (count <= 0 || count > 128 || 4 + count > chunk.Length) return null;
        return System.Text.Encoding.ASCII.GetString(tree.Bytes, chunk.Start + 4, count).TrimEnd('\0');
    }

    /// <summary>
    /// What each of the archive's effects IS, read off its own tuning: the file says only "effect 384", and
    /// the field holding 384 is the only thing that says the 384 is the car burning. Empty for an archive
    /// with no tuning — a district's effects are named by nothing we can read.
    /// </summary>
    private static IReadOnlyDictionary<uint, string> Roles(FileInfo? archive)
    {
        var roles = new Dictionary<uint, string>();
        if (archive == null) return roles;

        CarTuning? tuning;
        try { tuning = CarTuning.Read(archive); }
        catch (Exception) { return roles; }
        if (tuning == null) return roles;

        foreach (TuningTableView table in tuning.Tables)
        {
            foreach (TuningBandView band in table.Bands)
            {
                foreach (TuningElementView element in band.Elements)
                {
                    foreach (TuningFieldView row in element.Rows)
                    {
                        if (row.Number is <= 0 or > uint.MaxValue) continue;
                        if (!row.Name.EndsWith("ID", StringComparison.Ordinal)) continue;
                        if (row.Name.Contains("Snd", StringComparison.Ordinal)
                            || row.Name.Contains("Material", StringComparison.Ordinal)) continue;
                        roles.TryAdd((uint)row.Number, Friendly(row.Name));
                    }
                }
            }
        }
        return roles;
    }

    /// <summary>"FireID" reads as "Fire" — the tab says what the effect does, not what the field is called.</summary>
    private static string Friendly(string field) =>
        field.EndsWith("ID", StringComparison.Ordinal) ? field[..^2] : field;
}
