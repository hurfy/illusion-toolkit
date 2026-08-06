using Illusion.Formats.Effects;

namespace Illusion.Assets.Effects;

/// <summary>One editable number: what it is called, where it lives in the file, and what it says now.</summary>
public sealed record EffectValueRow(string Label, int Offset, float Value);

/// <summary>
/// One parameter of an operator — a scalar, a vector, or a curve read out as its keys. The rows are what
/// the panel edits; <see cref="Summary"/> is what it shows when they are folded away.
/// </summary>
public sealed class EffectParamView
{
    public EffectParamView(string title, string summary, IReadOnlyList<EffectValueRow> rows)
    {
        Title = title;
        Summary = summary;
        Rows = rows;
    }

    public string Title { get; }

    /// <summary>"curve, 4 key(s)" — or empty when the rows say everything.</summary>
    public string Summary { get; }

    public IReadOnlyList<EffectValueRow> Rows { get; }
}

/// <summary>
/// One operator of a generation: the thing that gives particles their birth rate, their speed, their
/// colour. Its enabled flag is a single byte in the file and is editable in its own right — switching one
/// off is the cheapest way to see what it was doing.
/// </summary>
public sealed class EffectOperatorView
{
    public EffectOperatorView(
        string title, uint type, bool enabled, int enabledOffset, IReadOnlyList<EffectParamView> parameters)
    {
        Title = title;
        Type = type;
        Enabled = enabled;
        EnabledOffset = enabledOffset;
        Parameters = parameters;
    }

    public string Title { get; }

    /// <summary>The operator kind as the file stores it.</summary>
    public uint Type { get; }

    public bool Enabled { get; }

    /// <summary>Where the enabled byte lives, so the panel can turn it over.</summary>
    public int EnabledOffset { get; }

    public IReadOnlyList<EffectParamView> Parameters { get; }

    public int ValueCount => Parameters.Sum(p => p.Rows.Count);
}

/// <summary>One generation — an emitter: a stream of particles with its own operators.</summary>
public sealed class EffectGenerationView
{
    public EffectGenerationView(string title, IReadOnlyList<EffectOperatorView> operators)
    {
        Title = title;
        Operators = operators;
    }

    public string Title { get; }

    public IReadOnlyList<EffectOperatorView> Operators { get; }

    /// <summary>"Birth, Position, Speed, …" — what this emitter is made of, at a glance.</summary>
    public string OperatorList => string.Join(", ", Operators.Select(o => o.Enabled ? o.Title : o.Title + " (off)"));

    public int ValueCount => Operators.Sum(o => o.ValueCount);
}

/// <summary>
/// One effect of the archive. The file names it only by a number; <see cref="Role"/> is what the car's own
/// tuning calls that number, which is the only place the meaning is written down.
/// </summary>
public sealed class EffectView
{
    public EffectView(
        uint id, string role, EffectChunk chunk, int frames, int sounds,
        IReadOnlyList<EffectGenerationView> generations)
    {
        Id = id;
        Role = role;
        Chunk = chunk;
        Frames = frames;
        Sounds = sounds;
        Generations = generations;
    }

    public uint Id { get; }

    /// <summary>"Fire", "Rain" — or empty when nothing in the archive names this id.</summary>
    public string Role { get; }

    /// <summary>The chunk this was built from, for a copy.</summary>
    public EffectChunk Chunk { get; }

    public int Frames { get; }

    public int Sounds { get; }

    public IReadOnlyList<EffectGenerationView> Generations { get; }

    /// <summary>What the panel puts on the header row.</summary>
    public string Title => Role.Length > 0 ? $"{Role} (effect {Id})" : $"Effect {Id}";

    public string Summary =>
        $"{Generations.Count} generation(s), {Frames} emitter(s)" + (Sounds > 0 ? $", {Sounds} sound(s)" : "");

    public int ValueCount => Generations.Sum(g => g.ValueCount);
}

/// <summary>
/// The names behind the numbers. The operator kinds are the game's own <c>E_OperatorType</c> order, as the
/// reference toolkit recorded it from the loader; the parameter slots are only known for the operators
/// whose loaders were read, and every other slot keeps its number rather than being given an invented name.
/// </summary>
public static class EffectNames
{
    private static readonly string[] Operators =
    [
        "Birth", "Position", "Speed", "Texture", "Rotation", "Scale", "Color", "Shape", "Emitting particle",
        "Acceleration", "Physics", "LOD", "Light", "Scale (non-uniform)", "Motion inheritance", "Color (HSV)",
        "Emissivity", "Shifted texture",
    ];

    private static readonly Dictionary<uint, Dictionary<uint, string>> Parameters = new()
    {
        [0] = new() { [0] = "Rate" },
        [1] = new() { [0] = "World space", [1] = "Space" },
        [2] = new() { [0] = "Speed", [5] = "Direction" },
        [3] = new() { [8] = "UV rect" },
        [4] = new() { [1] = "Axis", [6] = "Angle" },
        [5] = new() { [0] = "Size", [1] = "Size 2" },
        [6] = new() { [0] = "Color", [1] = "Alpha" },
        [9] = new() { [0] = "Acceleration", [1] = "Magnitude" },
        [13] = new() { [0] = "Size X", [1] = "Size Y" },
        [15] = new() { [0] = "Alpha", [1] = "Color" },
    };

    /// <summary>What an operator kind is called, or its number when it is one nobody has named.</summary>
    public static string Operator(uint type) =>
        type < Operators.Length ? Operators[type] : $"Operator {type}";

    /// <summary>What a parameter slot is called, or "parameter N" when its loader was never read.</summary>
    public static string Parameter(uint operatorType, uint slot) =>
        Parameters.TryGetValue(operatorType, out Dictionary<uint, string>? slots)
        && slots.TryGetValue(slot, out string? name)
            ? name
            : $"Parameter {slot}";
}
