using Illusion.Formats.IO;

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
/// C_ActorsPack, which is itself read-only. This port types the outer structure — the string buffer and
/// the scene-reference records (frame hash → resolved name + frame index) — and unpacks the inner binary
/// into its three offset-delimited regions plus the entity offset table (<see cref="Binary"/>), so the file
/// round-trips byte-exact. Per-entity field typing over the item blob is the gradual next slice.
/// </summary>
public sealed class ActorsFile
{
    public const ushort SupportedVersion = 6;
    private const ushort CompressedFlag = 2;

    /// <summary>The name string buffer, kept verbatim (scene-ref names index into it).</summary>
    public byte[] StringBuffer { get; set; } = Array.Empty<byte>();
    public List<ActorSceneReference> SceneReferences { get; } = new();

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
