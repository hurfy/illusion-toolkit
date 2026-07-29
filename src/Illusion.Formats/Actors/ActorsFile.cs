using System.Numerics;
using Illusion.Formats.IO;
using Illusion.Formats.Mathematics;

namespace Illusion.Formats.Actors;

/// <summary>Entity type ids used by Mafia II actors (from MafiaToolkitV2's E_EntityType).</summary>
public enum EntityType : uint
{
    None = 0,
    Human = 14,
    Player2 = 16,
    Car = 18,
    Train = 19,
    CrashObject = 20,
    TrafficCar = 21,
    TrafficHuman = 22,
    TrafficTrain = 23,
    ActionPoint = 25,
    ActionPointScript = 30,
    ActionPointSearch = 32,
    Item = 36,
    Door = 38,
    Tree = 39,
    Lift = 40,
    Sound = 41,
    SoundMixer = 43,
    Boat = 47,
    Radio = 48,
    Jukebox = 49,
    StaticEntity = 52,
    TranslocatedCar = 53,
    Garage = 54,
    FrameWrapper = 55,
    ActorDetector = 56,
    Blocker = 63,
    StaticWeapon = 65,
    StaticParticle = 66,
    FireTarget = 70,
    LightEntity = 71,
    Cutscene = 73,
    Telephone = 95,
    ScriptEntity = 98,
    DamageZone = 103,
    Airplane = 104,
    Pinup = 106,
    SpikeStrip = 107,
    DummyDoor = 109,
    FramesController = 110,
    Wardrobe = 112,
    PhysicsScene = 113,
    CleanEntity = 114,
}

/// <summary>
/// An actor pack (.act / Actors resource): the scene-reference table that links actors to frame objects,
/// plus the inner actor binary (v6, compressed or uncompressed). Ported from MafiaToolkitV2's
/// C_ActorsPack, which is itself read-only. The outer structure (string buffer, scene-reference records),
/// the inner binary's three offset-delimited regions and the placed actors themselves (<see cref="Actors"/>)
/// are typed; the entity-init property blobs and the cutscene lookup ride as capsules. The core verifies
/// every actor by re-encoding it at read time, so the file round-trips byte-exact.
/// </summary>
public sealed class ActorsFile
{
    public const ushort SupportedVersion = 6;
    private const ushort CompressedFlag = 2;

    /// <summary>The name string buffer, kept verbatim (scene-ref names index into it).</summary>
    public byte[] StringBuffer { get; set; } = Array.Empty<byte>();
    public List<ActorSceneReference> SceneReferences { get; } = new();

    /// <summary>The placed actors, in item-table order.</summary>
    public IReadOnlyList<ActorEntry> Actors => ActorList;

    internal List<ActorEntry> ActorList { get; } = new();

    /// <summary>The unpacked inner actor binary (header + props/items/cutscenes regions + entity offset
    /// table). Internal until per-entity fields are typed.</summary>
    internal Native.Model.ActorBinaryW Binary { get; set; } = new();

    /// <summary>Inner actor binary version (expected 6; 0 for a binary that lacked a v6/16 header).</summary>
    public ushort ActorFileVersion => (ushort)Binary.Version;
    /// <summary>Whether the inner actor binary uses the compressed layout.</summary>
    public bool IsCompressed => (Binary.Flags & CompressedFlag) != 0;
    /// <summary>Number of entities in the inner binary's item table.</summary>
    public int EntityCount => Binary.ItemOffsets.Count;

    public static ActorsFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static ActorsFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadActors(bytes);
    }


    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        Write(stream);
        return stream.ToArray();
    }

    public void Write(Stream output)
    {
        output.WriteBytes(Native.Misc.NativeMiscFiles.ActorsToBytes(this));
    }

}

/// <summary>
/// One placed actor: what it is, the names that tie it to a scene object and to its definition, and the
/// transform the game spawns it with. The transform is the editable part — its wire size is fixed, so a
/// change cannot shift the pack's offset tables. Everything else is read-only: the strings are
/// length-coupled to those offsets, and moving them is a separate slice of work.
/// </summary>
public sealed class ActorEntry
{
    /// <summary>Row in the pack's item table.</summary>
    public required int Index { get; init; }

    /// <summary>False for an item the core could not type exactly (unknown field shape) — it rides raw
    /// and its fields below are unset. Such an actor cannot be placed or edited.</summary>
    public required bool IsTyped { get; init; }

    public required uint TypeId { get; init; }
    /// <summary><see cref="TypeId"/> as the known entity enum (an unmapped id keeps its numeric value).</summary>
    public EntityType Type => (EntityType)TypeId;
    /// <summary>The engine class name ("C_Door"). Empty in a compressed pack, which stores only the id.</summary>
    public required string TypeName { get; init; }

    public required string EntityName { get; init; }
    public required string Name1 { get; init; }
    public required string SceneSector { get; init; }
    /// <summary>The definition (prototype) this actor was instanced from.</summary>
    public required string LinkedDefinition { get; init; }
    /// <summary>Name of the frame object this actor places — resolve it through <see cref="ActorsFile.SceneReferences"/>
    /// by <see cref="FrameHash"/>, not by name: many actors name a frame that lives in another archive.</summary>
    public required string LinkedFrame { get; init; }

    public required ulong EntityHash { get; init; }
    /// <summary>Hash of <see cref="LinkedFrame"/> — the key a scene reference is found by. Zero in an
    /// uncompressed pack, which stores no hashes.</summary>
    public required ulong FrameHash { get; init; }

    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 Scale { get; set; }

    public required ushort Flags { get; init; }
    /// <summary>Whether the game activates this actor as soon as the pack loads.</summary>
    public bool ActivateOnInit => (Flags & 1) != 0;
    /// <summary>Row in the entity-init property table, or -1 when the actor has no property blob.
    /// Several actors may share one row.</summary>
    public required short InitPropId { get; init; }

    /// <summary>The spawn transform in the same rotation·scale convention frame matrices use.</summary>
    public Matrix4x4 Transform => MatrixExtensions.SetMatrix(Rotation, Scale, Position);
}

/// <summary>Links an actor to a frame object: the frame's name hash, the name's position in the string
/// buffer (with the resolved name), and the frame index.</summary>
public sealed class ActorSceneReference
{
    public ulong FrameHash { get; set; }
    public ushort Unk0 { get; set; }
    public ushort NamePos { get; set; }
    public uint FrameIndex { get; set; }
    /// <summary>The name resolved from <see cref="NamePos"/> in the string buffer (read-only convenience).</summary>
    public string Name { get; set; } = string.Empty;
}
