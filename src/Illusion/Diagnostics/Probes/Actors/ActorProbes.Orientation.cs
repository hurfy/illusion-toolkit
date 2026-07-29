using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Actors;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames.ObjectTypes;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Which way an actor turns what it places: the pinned vanilla orientations that catch a flipped
/// convention, and the collision oracle that was tried for it. Part of <see cref="ActorProbes"/>.</summary>
internal static partial class ActorProbes
{
    internal static void RunActorOrientationProbe(string district, string? nameFilter)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_actor_orient.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? colPath = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            sb.AppendLine($"ACTOR ORIENTATION ORACLE — district={district}" +
                          (nameFilter == null ? "" : $", filter='{nameFilter}'"));
            if (colPath == null) { sb.AppendLine("district ships no .col — nothing to measure against"); return; }

            Formats.Collisions.CollisionFile collision = Formats.Collisions.CollisionFile.Load(colPath);
            var byHash = new Dictionary<ulong, List<Formats.Collisions.CollisionInstance>>();
            foreach (Formats.Collisions.CollisionInstance inst in collision.Instances)
            {
                if (!byHash.TryGetValue(inst.Hash, out List<Formats.Collisions.CollisionInstance>? list))
                {
                    byHash[inst.Hash] = list = new List<Formats.Collisions.CollisionInstance>();
                }
                list.Add(inst);
            }

            (_, _, ISceneDocument? loaded) = SdsMeshLoader.LoadHierarchy(new FileInfo(sds));
            if (loaded is not SceneDocumentAdapter document) { sb.AppendLine("district did not load"); return; }
            ActorPlacements placements = document.Placements;
            sb.AppendLine($".col: {collision.Instances.Count} instances, {byHash.Count} hulls; " +
                          $"actors: {placements.All.Count}, placed: {placements.PlacedCount}");

            int paired = 0, asIs = 0, inverted = 0, either = 0, neither = 0;
            int withTarget = 0, withHullChild = 0, hullInCol = 0;
            var samples = new List<string>();
            foreach (ActorEntry actor in placements.All)
            {
                if (nameFilter != null && !actor.EntityName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (placements.TargetOf(actor) is not { } target) continue;
                withTarget++;

                var hulls = new List<FrameObjectCollision>();
                CollectCollisions(target, hulls, new HashSet<FrameObjectBase>());
                if (hulls.Count > 0) withHullChild++;
                foreach (FrameObjectCollision hull in hulls)
                {
                    if (byHash.ContainsKey(hull.Hash)) hullInCol++;
                    if (!byHash.TryGetValue(hull.Hash, out List<Formats.Collisions.CollisionInstance>? instances)) continue;

                    System.Numerics.Matrix4x4 asStored = hull.WorldTransform * actor.Transform;
                    System.Numerics.Matrix4x4 asFlipped = hull.WorldTransform *
                        TransformMath.Compose(System.Numerics.Quaternion.Conjugate(actor.Rotation),
                            actor.Scale, actor.Position);

                    // The nearest hull copy — several identical hulls can share one hash across the district.
                    Formats.Collisions.CollisionInstance? best = null;
                    float bestD = float.MaxValue;
                    foreach (Formats.Collisions.CollisionInstance inst in instances)
                    {
                        float d = (inst.Position - actor.Position).Length();
                        if (d < bestD) { bestD = d; best = inst; }
                    }
                    if (best == null || bestD > 3f) continue;

                    paired++;
                    System.Numerics.Matrix4x4 truth = TransformMath.Compose(
                        TransformMath.CollisionEulerToQuaternion(best.Rotation),
                        System.Numerics.Vector3.One, best.Position);

                    float errStored = PoseError(asStored, truth);
                    float errFlipped = PoseError(asFlipped, truth);
                    bool storedFits = errStored < 0.05f;
                    bool flippedFits = errFlipped < 0.05f;

                    if (storedFits && flippedFits) either++;        // a half turn or no turn at all — says nothing
                    else if (storedFits) asIs++;
                    else if (flippedFits) inverted++;
                    else neither++;

                    if (samples.Count < 12 && !(storedFits && flippedFits))
                    {
                        samples.Add($"    {actor.EntityName} → {hull.Name}: as stored {errStored:F3}, " +
                                    $"inverted {errFlipped:F3} → {(storedFits ? "AS STORED" : flippedFits ? "INVERTED" : "neither")}");
                    }
                }
            }

            sb.AppendLine($"actors placing a frame: {withTarget}; of those, carrying a collision child: " +
                          $"{withHullChild}; those children found in the .col: {hullInCol}");

            // Which actors place an object that carries collision, by type — the shape a copy has not been
            // tried on in the game yet, and the shape most of the world's props have.
            var byType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (ActorEntry actor in placements.All)
            {
                if (placements.TargetOf(actor) is not { } target) continue;
                var hulls = new List<FrameObjectCollision>();
                CollectCollisions(target, hulls, new HashSet<FrameObjectBase>());
                if (hulls.Count == 0) continue;

                string type = actor.TypeName.Length > 0 ? actor.TypeName : actor.Type.ToString();
                if (!byType.TryGetValue(type, out List<string>? names)) byType[type] = names = new List<string>();
                if (names.Count < 4) names.Add($"{actor.EntityName} ({hulls.Count} hull(s))");
            }
            foreach (KeyValuePair<string, List<string>> pair in byType.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"    {pair.Key,-20} {string.Join(", ", pair.Value)}");
            }
            sb.AppendLine($"paired with a hull: {paired}");
            sb.AppendLine($"    the actor's quaternion as stored fits the game's hull : {asIs}");
            sb.AppendLine($"    only its INVERSE fits                                 : {inverted}");
            sb.AppendLine($"    both fit (half turn / no turn — no evidence either way): {either}");
            sb.AppendLine($"    neither fits (hull is not this object's, or scaled)    : {neither}");
            if (samples.Count > 0)
            {
                sb.AppendLine("samples (error in metres, over the hull's own corners):");
                foreach (string s in samples) sb.AppendLine(s);
            }

            string verdict = asIs > 0 && inverted == 0 ? "the pack stores the orientation the game uses"
                : inverted > 0 && asIs == 0 ? "the pack stores the INVERSE of the game's orientation"
                : asIs == 0 && inverted == 0 ? "no decisive pair — nothing measured"
                : "MIXED — the pairs disagree with each other, so neither reading is safe";
            sb.AppendLine($"VERDICT: {verdict}");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // How far the two poses put the same body, in metres: the worst corner of a 1 m box carried by each.
    private static float PoseError(System.Numerics.Matrix4x4 a, System.Numerics.Matrix4x4 b)
    {
        float worst = 0f;
        for (int i = 0; i < 8; i++)
        {
            var corner = new System.Numerics.Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f,
                (i & 4) == 0 ? -1f : 1f);
            float d = (TransformMath.TransformCoordinate(corner, a) - TransformMath.TransformCoordinate(corner, b))
                .Length();
            if (d > worst) worst = d;
        }
        return worst;
    }

    private static void CollectCollisions(FrameObjectBase frame, List<FrameObjectCollision> into,
        HashSet<FrameObjectBase> seen)
    {
        if (!seen.Add(frame)) return;
        if (frame is FrameObjectCollision hull) into.Add(hull);
        foreach (FrameObjectBase child in frame.Children) CollectCollisions(child, into, seen);
    }

    /// <summary>
    /// Vanilla orientations, pinned.
    ///
    /// A rotation convention flip is invisible to every other check in this file: no translation moves, the
    /// pack still re-saves byte for byte (an inversion applied on both read and write is byte-neutral), and the
    /// round-trip check compares the flipped value against itself. It shows up only as objects standing turned
    /// the wrong way in the GAME — and the convention flipped twice in one day before anyone looked there.
    /// These lines are that comparison, frozen: they are the state in which an untouched gate in uppertown
    /// faces the same way in the viewport as it does in the game.
    ///
    /// Regenerate deliberately — never to turn a red check green. The probe prints "PIN" lines for a district
    /// it has none for; those belong here only once the viewport has been compared against the game again.
    /// </summary>
    private static readonly Dictionary<string, string[]> PinnedOrientations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["distillery"] =
        [
            "DE_lahev33|-0.0000,-0.0000,-0.5792,0.8152|-1560.28,-113.48,2.63",
            "2D_box38|-0.0000,-0.0000,0.1147,0.9934|-1560.29,-114.47,4.98",
            "2D_zidle14|-0.0000,-0.0000,0.5927,0.8054|-1563.07,-107.86,-9.65",
            "2D_zidle04|-0.0000,-0.0000,-0.8115,0.5844|-1566.38,-90.56,-14.58",
            "DE_lahev44|-0.0000,-0.0000,0.0860,0.9963|-1560.46,-114.61,4.26",
            "2D_ELECTR_38|-0.0000,-0.0000,-0.7071,0.7071|-1561.03,-100.79,-12.84",
            "X2D_box71|-0.0000,-0.0000,0.7071,0.7071|-1558.59,-116.30,-13.66",
            "X2D_box69|-0.0000,-0.0000,-0.0872,0.9962|-1553.70,-115.55,-13.66",
            "DE_bedna01>box_dI|-1561.82,-113.89,-0.17",
            "DE_bedna10>box_dI|-1557.92,-120.68,-8.38",
            "DE_bedna12>box_dI|-1561.91,-118.87,-11.58",
            "DE_bedna16>box_dI|-1568.29,-108.67,-4.28",
        ],
        ["eastside"] =
        [
            "AmbiRV_city10_train_whistle100|-0.0000,-0.0000,0.7076,0.7066|150.85,145.35,-8.88",
            "AmbiRV_city10_car_horn05|-0.0000,-0.0000,0.6921,0.7218|-474.03,188.99,42.00",
            "wanted57|-0.0000,-0.0000,0.4617,0.8870|-18.90,82.09,-8.89",
            "wanted58|-0.0000,-0.0000,0.7065,0.7077|-51.21,420.02,-9.37",
            "wanted61|0.0267,-0.0267,0.7066,0.7066|91.35,262.83,-16.62",
            "wanted62|-0.0204,0.0204,0.7068,0.7068|-11.10,148.30,-9.71",
            "wanted63|-0.0000,-0.0000,-0.7066,0.7077|-116.18,410.67,-9.24",
            "wanted64|0.0158,0.0158,-0.7069,0.7069|-339.44,253.09,0.16",
        ],
        ["port"] =
        [
            "jachta01|-0.0000,-0.0000,-0.1634,0.9866|-613.27,-856.79,-24.15",
            "jachta02|-0.0000,-0.0000,0.5884,0.8086|-470.61,-982.85,-24.15",
            "jachta05|-0.0000,-0.0000,0.7136,0.7006|-519.73,-821.65,-24.15",
            "jachta06|-0.0000,-0.0000,0.9872,0.1595|-634.01,-833.82,-24.15",
            "jachta04|-0.0000,-0.0000,-0.6903,0.7235|-487.03,-787.63,-24.15",
            "jachta09|-0.0000,-0.0000,-0.1634,0.9866|-655.35,-842.23,-24.15",
            "boatXXL01|-0.0000,-0.0000,0.7012,0.7130|-469.47,-788.49,-24.50",
            "jachta10|-0.0000,-0.0000,-0.7215,0.6924|-465.47,-918.70,-24.15",
            "jachta00>teziste|-516.80,-921.68,-26.67",
            "jachta01>teziste|-614.90,-858.42,-26.67",
            "jachta04>teziste|-489.15,-786.71,-26.67",
            "jachta05>teziste|-517.64,-822.63,-26.67",
        ],
        ["prisone"] =
        [
            "CDi_light__02|-0.7071,0.0000,-0.0000,0.7071|63.12,22.21,292.06",
            "CDi_light__01|-0.7071,0.0000,-0.0000,0.7071|63.12,32.65,292.04",
            "bedna2|-0.0000,-0.0000,0.7071,0.7071|-16.70,-40.21,303.02",
            "basketBall|-0.0000,-0.0000,-0.6635,0.7481|23.67,-2.98,303.11",
            "bedna1|-0.0000,-0.0000,0.7071,0.7071|-16.66,-42.01,303.02",
            "playBallPickUpPos|-0.0000,-0.0000,0.7476,0.6642|3.47,-1.22,303.00",
            "playBallThrowPos|-0.0000,-0.0000,0.7388,0.6739|6.78,-1.27,303.00",
            "playerBallPickUpPos|-0.0000,-0.0000,-0.6635,0.7481|23.13,-3.01,303.00",
            "bush09>C_bush03_Collision|15.47,-95.21,301.68",
            "celtis08>celtis01 trunk|41.60,-55.61,306.48",
            "celtis06>celtis01 trunk|32.14,-181.27,301.37",
            "celtis03>celtis01 trunk|25.48,-103.96,307.41",
        ],
    };

    // A rotated actor as a comparable line: the quaternion it stores, and where its turn puts a point of the
    // prototype it places. The point is what makes this catch a composition-order or scale error too — an
    // inverted, differently-ordered or unscaled transform lands it somewhere else.
    private static string PinOf(ActorEntry actor)
    {
        System.Numerics.Vector3 probe =
            TransformMath.TransformCoordinate(new System.Numerics.Vector3(1f, 2f, 3f), actor.Transform);
        return $"{actor.EntityName}|{actor.Rotation.X:F4},{actor.Rotation.Y:F4},{actor.Rotation.Z:F4}," +
               $"{actor.Rotation.W:F4}|{probe.X:F2},{probe.Y:F2},{probe.Z:F2}";
    }

    // A turn a convention flip would actually MOVE something with. Half turns are excluded on purpose: the
    // conjugate of a 180° rotation is the same rotation negated, which is the same orientation — pinning those
    // would compare a number that changes against geometry that does not, and prove nothing about the viewport.
    private static bool IsSensitiveTurn(ActorEntry actor) =>
        actor.IsTyped && MathF.Abs(actor.Rotation.W) is > 0.05f and < 0.999f;

    private static List<ActorEntry> RotatedActors(ActorPlacements placements, int count)
    {
        var picked = new List<ActorEntry>(count);
        foreach (ActorEntry actor in placements.All)
        {
            if (!IsSensitiveTurn(actor)) continue;
            picked.Add(actor);
            if (picked.Count == count) break;
        }
        return picked;
    }

    private static void CheckPinnedOrientations(string district, ActorPlacements placements,
        List<FrameNodeAdapter> nodes, StringBuilder sb, Action<string, bool, string> check)
    {
        List<ActorEntry> rotated = RotatedActors(placements, 8);
        var lines = new List<string>(rotated.Count + 4);
        foreach (ActorEntry actor in rotated) lines.Add(PinOf(actor));

        // Plus a few real placed children: their world transform runs through the scene adapter, so these pin
        // the whole path the renderer reads, not just the arithmetic on the actor's own record.
        var pinnedActors = new HashSet<string>(StringComparer.Ordinal);
        foreach (FrameNodeAdapter node in nodes)
        {
            if (pinnedActors.Count == 4) break;
            if (node.Frame.WorldTransform.Translation.LengthSquared() < 1e-4f) continue;
            if (placements.ActorCovering(node.Frame) is not { } covering) continue;
            if (!IsSensitiveTurn(covering) || !pinnedActors.Add(covering.EntityName)) continue;

            System.Numerics.Vector3 w = node.WorldTransform.Translation;
            lines.Add($"{covering.EntityName}>{node.Frame.Name}|{w.X:F2},{w.Y:F2},{w.Z:F2}");
        }

        if (!PinnedOrientations.TryGetValue(district, out string[]? expected))
        {
            sb.AppendLine($"(no pinned orientations for '{district}' — add these to PinnedOrientations)");
            foreach (string line in lines) sb.AppendLine($"    PIN  \"{line}\",");
            return;
        }

        int matched = 0;
        string firstOff = "";
        for (int i = 0; i < expected.Length; i++)
        {
            if (i < lines.Count && string.Equals(lines[i], expected[i], StringComparison.Ordinal)) { matched++; continue; }
            if (firstOff.Length == 0)
            {
                firstOff = $"expected \"{expected[i]}\", got \"{(i < lines.Count ? lines[i] : "(nothing)")}\"";
            }
        }
        check("vanilla actors are turned the way they were pinned", matched == expected.Length && lines.Count == expected.Length,
            $"{matched}/{expected.Length} {firstOff}");
    }

    // The glyphs and the click targets of a district, which used to be snapshots taken once at load: a deleted
    // actor stayed clickable (and an actor pick beats the geometry behind it, so it swallowed clicks meant for
}
