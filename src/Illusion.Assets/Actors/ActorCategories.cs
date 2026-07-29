using System.Numerics;
using Illusion.Formats.Actors;

namespace Illusion.Assets.Actors;

/// <summary>What an actor is, coarsely — the grouping the scene tree lists by and the viewport colours its
/// glyphs by. Mafia II ships 44 entity types; forty-odd tree sections would be unreadable, and the types fall
/// into a handful of jobs anyway.</summary>
public enum ActorCategory
{
    Sound,
    Light,
    Particle,
    Script,
    Cutscene,
    Item,
    Trigger,
    Character,
    Vehicle,
    Traffic,
    Prop,
    Other,
}

/// <summary>Maps an actor's entity type to its category, display label and colour.</summary>
public static class ActorCategories
{
    public static ActorCategory Of(EntityType type) => type switch
    {
        EntityType.Sound or EntityType.SoundMixer or EntityType.Radio or EntityType.Jukebox => ActorCategory.Sound,
        EntityType.LightEntity => ActorCategory.Light,
        EntityType.StaticParticle => ActorCategory.Particle,
        EntityType.ScriptEntity or EntityType.ActionPoint or EntityType.ActionPointScript
            or EntityType.ActionPointSearch or EntityType.FramesController => ActorCategory.Script,
        EntityType.Cutscene => ActorCategory.Cutscene,
        EntityType.Item or EntityType.StaticWeapon or EntityType.Pinup => ActorCategory.Item,
        EntityType.ActorDetector or EntityType.Blocker or EntityType.DamageZone or EntityType.CleanEntity
            or EntityType.PhysicsScene or EntityType.FireTarget or EntityType.SpikeStrip => ActorCategory.Trigger,
        EntityType.Human or EntityType.Player2 => ActorCategory.Character,
        EntityType.Car or EntityType.Boat or EntityType.Train or EntityType.Airplane
            or EntityType.TranslocatedCar => ActorCategory.Vehicle,
        EntityType.TrafficCar or EntityType.TrafficHuman or EntityType.TrafficTrain => ActorCategory.Traffic,
        EntityType.Door or EntityType.DummyDoor or EntityType.Lift or EntityType.Garage or EntityType.Tree
            or EntityType.Telephone or EntityType.Wardrobe or EntityType.CrashObject
            or EntityType.StaticEntity or EntityType.FrameWrapper => ActorCategory.Prop,
        _ => ActorCategory.Other,
    };

    public static string Label(ActorCategory category) => category switch
    {
        ActorCategory.Sound => "Sound",
        ActorCategory.Light => "Light",
        ActorCategory.Particle => "Particles",
        ActorCategory.Script => "Script hooks",
        ActorCategory.Cutscene => "Cutscenes",
        ActorCategory.Item => "Items",
        ActorCategory.Trigger => "Triggers / zones",
        ActorCategory.Character => "Characters",
        ActorCategory.Vehicle => "Vehicles",
        ActorCategory.Traffic => "Traffic settings",
        ActorCategory.Prop => "Props",
        _ => "Other",
    };

    /// <summary>Glyph colour (rgb + alpha). Chosen to read against both the lit scene and the wireframe modes,
    /// and to stay distinct from the collision (surface palette) and navigation (green/amber/blue) overlays.</summary>
    public static Vector4 Color(ActorCategory category) => category switch
    {
        ActorCategory.Sound => new Vector4(0.34f, 0.78f, 0.84f, 0.95f),   // teal
        ActorCategory.Light => new Vector4(1.00f, 0.85f, 0.35f, 0.95f),   // warm yellow
        ActorCategory.Particle => new Vector4(0.72f, 0.55f, 0.95f, 0.95f),// violet
        ActorCategory.Script => new Vector4(0.55f, 0.85f, 0.45f, 0.95f),  // green
        ActorCategory.Cutscene => new Vector4(0.95f, 0.45f, 0.80f, 0.95f),// magenta
        ActorCategory.Item => new Vector4(0.95f, 0.60f, 0.25f, 0.95f),    // orange
        ActorCategory.Trigger => new Vector4(0.95f, 0.35f, 0.35f, 0.95f), // red
        ActorCategory.Character => new Vector4(0.98f, 0.78f, 0.65f, 0.95f),
        ActorCategory.Vehicle => new Vector4(0.45f, 0.65f, 0.95f, 0.95f),
        ActorCategory.Traffic => new Vector4(0.60f, 0.60f, 0.65f, 0.95f),
        ActorCategory.Prop => new Vector4(0.80f, 0.80f, 0.80f, 0.95f),
        _ => new Vector4(1.00f, 1.00f, 1.00f, 0.95f),
    };
}
