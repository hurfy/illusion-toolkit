using System.Globalization;
using Illusion.Assets.Library;
using Illusion.Assets.Sds;
using Illusion.Formats;
using Illusion.Formats.Actors;
using Illusion.Formats.Archive;
using Illusion.Formats.EntityData;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Assets.EntityData;

/// <summary>
/// An archive's entity-data storages, read for SHOWING — and for a car that means its tuning: mass, torque
/// curve, gearbox, differential, the ten-slot wheel table, brakes, aerodynamics and the sound tail.
///
/// <para>
/// A storage is a fixed-size behavior blob per entity CLASS, not per placed entity. The car a district
/// spawns carries none of this: its <c>.act</c> record is a position and nothing else, and every number
/// that decides how the car drives lives here, in the car's own archive.
/// </para>
/// <para>
/// The layout comes from the core, which types the blob as named field VIEWS over the original bytes and
/// pokes back only what moved. Nothing here re-encodes a table, which is why an archive whose tuning was
/// never touched saves back byte for byte.
/// </para>
/// </summary>
public sealed class CarTuning
{
    private CarTuning(IReadOnlyList<TuningTableView> tables) => Tables = tables;

    /// <summary>Every table of every storage in the archive, in file order.</summary>
    public IReadOnlyList<TuningTableView> Tables { get; }

    /// <summary>How many fields the whole archive exposes — the tab's headline.</summary>
    public int FieldCount => Tables.Sum(t => t.FieldCount);

    /// <summary>Reads an archive's storages. Opens files, so it belongs on a background thread. Returns null
    /// when the archive carries no storage the core has a layout for — which is most archives.</summary>
    public static CarTuning? Read(FileInfo archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return ReadFrom(MafiaEnvironment.ExtractedDir(archive), WheelPrototypes(archive));
    }

    /// <summary>The same, from a working copy that is not the archive's own — what the regression harness
    /// reads, so it never writes into the player's install.</summary>
    public static CarTuning? ReadFrom(string extracted, IReadOnlyList<string>? wheelChoices = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(extracted);

        IReadOnlyList<string> files;
        try { files = SdsManifest.Load(extracted).GetFiles("EntityDataStorage"); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { return null; }
        if (files.Count == 0) return null;

        var tables = new List<TuningTableView>();
        foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            EntityDataStorageFile storage;
            try { storage = EntityDataStorageFile.Load(file); }
            catch (Exception ex) when (ex is IOException or SdsFormatException) { continue; }

            // What the tables of ONE storage look like beside each other. A car ships several — the same car
            // tuned three ways (stock, then more power, then less weight and more again) — and they are told
            // apart by their numbers, not by their names: the hashes name actor definitions the archive does
            // not carry. Saying "identical to table 1" is the only honest label for the ones that are.
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < storage.Tables.Count; i++)
            {
                EntityDataTable table = storage.Tables[i];
                if (table.Fields.Count == 0) continue;

                string key = Fingerprint(table);
                string? twin = seen.TryGetValue(key, out int first)
                    ? $"identical to table {first + 1}"
                    : null;
                seen.TryAdd(key, i);

                tables.Add(new TuningTableView(
                    file, i, table.Hash, storage.EntityType, storage.Type.ToString(), table.PayloadSize,
                    Bands(storage.EntityType, table, wheelChoices), twin));
            }
        }
        return tables.Count == 0 ? null : new CarTuning(tables);
    }

    // Two tables are the same tuning when every field the core names holds the same value. Compared through
    // the FIELDS rather than the bytes, because the bytes include the 112 the core does not name and a
    // difference in those is not a difference anyone can see or edit.
    private static string Fingerprint(EntityDataTable table) =>
        string.Join('|', table.Fields.Select(f => f.Display));

    /// <summary>
    /// The wheel prototypes a car can name: the holder frames of <c>cars_universal</c> / <c>cars_universal2</c>,
    /// which is where the wheels, hubcaps and tyres actually live. The car's own archive names none of them —
    /// the only link is the 32-byte string in this very table, so an offered list is the difference between
    /// picking a wheel and guessing its name.
    /// </summary>
    private static IReadOnlyList<string> WheelPrototypes(FileInfo archive)
    {
        var names = new List<string>();
        foreach (FileInfo companion in StageCompanions.For(archive))
        {
            string extracted = MafiaEnvironment.ExtractedDir(companion);
            if (!Directory.Exists(extracted)) continue;   // not opened yet — no list, not an error
            try
            {
                if (SdsMeshLoader.OpenScene(extracted).FrameResource is not { FrameObjects: not null } frame)
                {
                    continue;
                }
                foreach (object o in frame.FrameObjects.Values)
                {
                    if (o is FrameObjectBase f && f.Name.String is { Length: > 0 } name
                        && name.StartsWith("wheel", StringComparison.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                }
            }
            catch (Exception)
            {
                // A suggestion list is a nicety. Never let it stop the tab from opening.
            }
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── how a table's fields are grouped ──
    //
    // The struct is COLUMN-major: all ten wheels' models, then all ten tyres, then all ten scales. Read in
    // file order it is unreadable, so the fields are put back together here — by band, and inside a band by
    // the thing they belong to (Wheel 3, Gear 2, EngineF 5).
    //
    // A field lands in the band whose LONGEST listed prefix it starts with, so "GearboxShiftSndId1" goes to
    // the sound band rather than to the gearbox, and "HandBrakeTorque" to the brakes rather than to the
    // handbrake sound. Case matters: the file spells the sound one "Handbrake".
    private static readonly (string Title, string[] Prefixes)[] CarBands =
    [
        ("Body", ["Mass", "CenterOfMass", "InteriaMax", "Inertia", "MaterialID", "StaticFriction",
                  "DynamicFriction", "Restitution"]),
        ("Engine", ["Power", "Rotations", "Torque", "MotorBrakeTorque", "MotorInertia", "MotorOrientation"]),
        ("Fuel", ["Fuel"]),
        ("Gearbox", ["FinalGear", "GearCount", "GearReverseCount", "Gear", "MinClutch"]),
        ("Steering", ["MaxAccelSlowMode", "SteerAngle", "AngleChange"]),
        ("Differential", ["MotorDifferentialIndex", "Diff", "CDRatio", "CDViscousClutch", "CDDiffLock",
                          "IndexAxleDifferential", "TyreLateralStiffnessCoeff", "TypeLateralDamperCoeff"]),
        ("Wheels", ["Wheel"]),
        ("Brakes", ["BrakeTorque", "BrakeReaction", "HandBrakeTorque"]),
        ("Aerodynamics", ["Aerodynamic", "FrontSpoiler", "BackSpoiler"]),
        ("Driving assists", ["ESM", "ASP", "Arcade", "AMFake", "MinSpeed", "MaxSpeedAdd", "ESPCoeff",
                             "SpeedMaxEffectivity", "RotVelSpeedLimit", "Coeff", "RightWheelForcePos",
                             "FFRideMagnitudeCoeff"]),
        ("Effects", ["SmokeMotor", "SmokeExhaustID", "ExplosionID", "FireID", "SlideEffectID", "CrashEffectID",
                     "RimSparksID", "BurnOutID", "BreakTireID", "BreakGlass", "HedgeIDs", "RainID",
                     "TimeFireMax"]),
        ("Engine sound", ["EngineSwitch", "EngineMinRot", "EngineNPC", "EngineVolEnvelope", "EngineCrossFade",
                          "EngineF", "EngineB", "EngineFizz", "EngineCooling", "EngineFan"]),
        ("Car sound", ["Environmental", "AirPump", "DoorOpen", "DoorClose", "CoverOpen", "CoverClose",
                       "KnockingBonnet", "DropHood", "Crash", "GearboxShift", "Handbrake", "WheelShank",
                       "Horn", "Siren", "TyreBreak", "TyreCrash", "RimRide", "GlassBreak", "Explosion",
                       "Fire", "SqueakingBrakes", "Rain", "Hedge"]),
        ("Surface sound", ["Slide", "Roll", "Snd", "Particle", "ExplodeID"]),
        ("Unknown", ["UnkInts0"]),
    ];

    private static IReadOnlyList<TuningBandView> Bands(
        int entityType, EntityDataTable table, IReadOnlyList<string>? wheelChoices)
    {
        // Only a car has a band layout. Every other storage the core types (the player, a train, an action
        // point script) is a short flat struct — one band is the honest shape for it, not a set of empty
        // headers named after a car's parts.
        if (entityType != 18)
        {
            return [new TuningBandView("Fields", [new TuningElementView(null, Rows(table.Fields, null))])];
        }

        var bands = new List<TuningBandView>();
        foreach (string title in CarBands.Select(b => b.Title).Append("Other"))
        {
            List<ActorPropertyField> mine = table.Fields.Where(f => BandOf(f.Name) == title).ToList();
            if (mine.Count == 0) continue;

            // Inside a band, one plate per thing: "Wheel3.Camber" belongs with "Wheel3.ToeIn", ten fields
            // away in the file. A field with no dot in its name is a thing of its own.
            var elements = new List<TuningElementView>();
            var loose = new List<ActorPropertyField>();
            var byPart = new Dictionary<string, List<ActorPropertyField>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (ActorPropertyField field in mine)
            {
                int dot = field.Name.IndexOf('.', StringComparison.Ordinal);
                if (dot <= 0) { loose.Add(field); continue; }
                string part = field.Name[..dot];
                if (!byPart.TryGetValue(part, out List<ActorPropertyField>? rows))
                {
                    byPart[part] = rows = [];
                    order.Add(part);
                }
                rows.Add(field);
            }

            if (loose.Count > 0) elements.Add(new TuningElementView(null, Rows(loose, wheelChoices)));
            foreach (string part in order)
            {
                elements.Add(new TuningElementView(Pretty(part), Rows(byPart[part], wheelChoices)));
            }
            bands.Add(new TuningBandView(title, elements));
        }
        return bands;
    }

    private static string BandOf(string name)
    {
        string best = "Other";
        int longest = 0;
        foreach ((string title, string[] prefixes) in CarBands)
        {
            foreach (string prefix in prefixes)
            {
                if (prefix.Length > longest && name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    best = title;
                    longest = prefix.Length;
                }
            }
        }
        return best;
    }

    // "Wheel3" → "Wheel 3". The number is the SLOT, not a count: the differential table points at wheels by
    // it, so it stays zero-based and is never re-numbered for display.
    private static string Pretty(string part)
    {
        int digits = part.Length;
        while (digits > 0 && char.IsAsciiDigit(part[digits - 1])) digits--;
        return digits == part.Length || digits == 0 ? part : part[..digits] + " " + part[digits..];
    }

    private static IReadOnlyList<TuningFieldView> Rows(
        IReadOnlyList<ActorPropertyField> fields, IReadOnlyList<string>? wheelChoices)
    {
        var rows = new List<TuningFieldView>(fields.Count);
        foreach (ActorPropertyField field in fields)
        {
            int dot = field.Name.IndexOf('.', StringComparison.Ordinal);
            string label = dot > 0 ? field.Name[(dot + 1)..] : field.Name;
            rows.Add(new TuningFieldView(
                label, field.Name, field.Offset, KindOf(field.Kind), field.Display,
                X: field.Kind == ActorPropertyKind.Vector3 ? field.Vector.X : field.Single,
                Y: field.Kind == ActorPropertyKind.Vector3 ? field.Vector.Y : 0,
                Z: field.Kind == ActorPropertyKind.Vector3 ? field.Vector.Z : 0,
                Number: field.Number,
                Text: field.Kind == ActorPropertyKind.Text ? field.Text : "",
                Capacity: field.Capacity,
                Choices: field.Name.EndsWith(".WheelModel", StringComparison.Ordinal)
                    ? Offered(wheelChoices, field.Text)
                    : null));
        }
        return rows;
    }

    // The list a wheel slot offers. Always carries what the slot HOLDS, even when the companion archive does
    // not: a picker that cannot show the current value would read as the slot being empty, and picking
    // anything would then be the only way out of a state that was fine. With no companion read there is no
    // list at all and the row falls back to a plain box — one name is not a choice.
    private static IReadOnlyList<string>? Offered(IReadOnlyList<string>? known, string current)
    {
        if (known is not { Count: > 0 }) return null;
        return current.Length > 0 && !known.Contains(current, StringComparer.OrdinalIgnoreCase)
            ? known.Append(current).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : known;
    }

    private static TuningFieldKind KindOf(ActorPropertyKind kind) => kind switch
    {
        ActorPropertyKind.Bool => TuningFieldKind.Flag,
        ActorPropertyKind.Float => TuningFieldKind.Number,
        ActorPropertyKind.Vector3 => TuningFieldKind.Vector,
        ActorPropertyKind.Text => TuningFieldKind.Text,
        _ => TuningFieldKind.Integer,
    };
}

/// <summary>What a row IS — which decides the widget it is drawn with and how an edit is written.</summary>
public enum TuningFieldKind
{
    /// <summary>A float: a mass, a ratio, a torque.</summary>
    Number,

    /// <summary>A whole number: a count, an index into a side table, a sound id.</summary>
    Integer,

    /// <summary>A yes/no.</summary>
    Flag,

    /// <summary>Three floats — a centre of mass, an inertia tensor's diagonal.</summary>
    Vector,

    /// <summary>A fixed-width name buffer. The only resource NAME in the whole car struct is one of these.</summary>
    Text,
}

/// <summary>One named value of a table, and everything a write needs to find it again.</summary>
public sealed record TuningFieldView(
    string Label, string Name, uint Offset, TuningFieldKind Kind, string Value,
    float X = 0, float Y = 0, float Z = 0, long Number = 0, string Text = "", uint Capacity = 0,
    IReadOnlyList<string>? Choices = null)
{
    /// <summary>Whether the row offers a list of known names beside its box — the wheel prototypes.</summary>
    public bool HasChoices => Choices is { Count: > 0 };
}

/// <summary>One thing inside a band: a wheel and its twenty-three fields, a gear and its six.</summary>
public sealed class TuningElementView
{
    public TuningElementView(string? title, IReadOnlyList<TuningFieldView> rows)
    {
        Title = title;
        Rows = rows;
    }

    /// <summary>What the thing is called, or null when the rows stand for the band itself.</summary>
    public string? Title { get; }

    public IReadOnlyList<TuningFieldView> Rows { get; }
}

/// <summary>One band of a table — the gearbox, the wheels, the sound tail.</summary>
public sealed class TuningBandView
{
    public TuningBandView(string title, IReadOnlyList<TuningElementView> elements)
    {
        Title = title;
        Elements = elements;
    }

    public string Title { get; }

    public IReadOnlyList<TuningElementView> Elements { get; }

    public int FieldCount => Elements.Sum(e => e.Rows.Count);
}

/// <summary>One entity-data table: which file and row it is, what it describes, and its bands.</summary>
public sealed class TuningTableView
{
    public TuningTableView(
        string path, int index, ulong hash, int entityType, string typeName, int size,
        IReadOnlyList<TuningBandView> bands, string? twinOf)
    {
        Path = path;
        Index = index;
        Hash = hash;
        EntityType = entityType;
        TypeName = typeName;
        Size = size;
        Bands = bands;
        TwinOf = twinOf;
    }

    /// <summary>The storage file this table lives in — where a write goes.</summary>
    public string Path { get; }

    /// <summary>Which table of that storage, in file order. Its identity for an edit and an undo.</summary>
    public int Index { get; }

    /// <summary>The name hash the storage lists it under. Shown raw: it names an actor definition that lives
    /// somewhere else entirely, and inventing a name for it would be a guess.</summary>
    public ulong Hash { get; }

    public int EntityType { get; }

    public string TypeName { get; }

    public int Size { get; }

    public IReadOnlyList<TuningBandView> Bands { get; }

    /// <summary>Set when an earlier table in the same storage holds the very same values.</summary>
    public string? TwinOf { get; }

    public int FieldCount => Bands.Sum(b => b.FieldCount);

    /// <summary>What the header says: which table this is and how big.</summary>
    public string Title => string.Create(CultureInfo.InvariantCulture, $"Table {Index + 1}");

    /// <summary>
    /// What tells this table apart from its neighbours, for the picker at the top of the tab. A car ships
    /// several tables and their names are hashes of definitions that live elsewhere, so the only honest
    /// label is what is actually IN them — the two numbers that differ between a stock car and a tuned one.
    /// Falls back to the bare title for a storage that has neither.
    /// </summary>
    public string Label
    {
        get
        {
            var wanted = new[] { "Mass", "Power" };
            List<string> parts = Bands
                .SelectMany(b => b.Elements).SelectMany(e => e.Rows)
                .Where(r => wanted.Contains(r.Name, StringComparer.Ordinal))
                .OrderBy(r => Array.IndexOf(wanted, r.Name))
                .Select(r => $"{r.Name} {r.Value}")
                .ToList();
            return parts.Count == 0 ? Title : Title + " · " + string.Join(" · ", parts);
        }
    }

    public string HashText => "0x" + Hash.ToString("X16", CultureInfo.InvariantCulture);

    public string SizeText => Size.ToString("N0", CultureInfo.InvariantCulture) + " B";
}
