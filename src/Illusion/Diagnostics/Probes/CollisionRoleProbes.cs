using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.ItemDesc;
using Illusion.Formats.Prefab;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// A collision authored by ROLE and SHAPE, travelling the whole path: a component, a role, a size and a
/// position in that component's own space — out to the file and back.
///
/// <para>
/// Two halves, and the second is the one that cannot be faked. The first asks whether what was authored comes
/// back: role, shape, size and position, read again from the written archive. The second asks whether what
/// was WRITTEN looks like a shipped car — the stored type, the bone space the matrix went into, the extents,
/// the ItemDesc record and the mirror stub — with the corpus as the oracle rather than this file's own idea
/// of itself.
/// </para>
/// <para>
/// NOTHING IS WRITTEN INTO THE GAME'S FOLDERS. The focus car's working copy is mirrored into the temp
/// directory and every edit lands in the mirror, which is also what makes the comparison possible: the
/// original is still there to compare against.
/// </para>
/// <para>Output: %TEMP%\illusion_collision_role.txt</para>
/// </summary>
internal static class CollisionRoleProbes
{
    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "illusion_collision_role");

    internal static void RunCollisionRoleProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_role.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");

            Corpus(sb, folder, Check);
            string? mirror = Mirror(focus, folder, sb, Check);
            if (mirror != null)
            {
                RoundTrip(sb, mirror, Check);
                Encoded(sb, mirror, Check);
                Removal(sb, mirror, Check);
                Undo(sb, mirror, Check);
                Scoped(sb, mirror, Check);
                Cycles(sb, mirror, Check);
                Overtaken(sb, mirror, Check);
                Refusals(sb, mirror, Check);
                Redirected(sb, mirror, Check);
            }
            sb.Insert(0, $"COLLISION BY ROLE AND SHAPE ({focus}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "COLLISION BY ROLE AND SHAPE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // ── the corpus is the oracle: how a shipped car writes each role ──

    private static void Corpus(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("════ how the shipped cars write each role ════");

        int cars = 0, volumes = 0, collisions = 0;
        int placedNameRecord = 0, placedPlaceholder = 0, placedWithStub = 0, placed = 0;
        int selfNamesNothing = 0, self = 0;
        var byKindAndType = new Dictionary<string, int>(StringComparer.Ordinal);
        var byShape = new Dictionary<CarCollisionShape, int>();
        var byRole = new Dictionary<CarCollisionRole, int>();
        int restatedInOwnSpace = 0, selfInParentSpace = 0;

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            Car? car;
            try { car = Car.ReadFrom(extracted); }
            catch (Exception) { continue; }
            if (car?.Prefab.Car is not { DeformPartCount: > 0 }) continue;
            cars++;

            IReadOnlyList<CarDeformPart> parts = car.Prefab.CarDeformParts;
            var stubs = new HashSet<ulong>();
            foreach (FrameObjectCollision stub in car.Frames?.FrameObjects?.Values
                         .OfType<FrameObjectCollision>() ?? [])
            {
                stubs.Add(stub.Hash);
            }

            foreach (CarDeformPart part in parts)
            {
                foreach (CarPhysicsVolume volume in part.Volumes)
                {
                    volumes++;
                    string key = $"{part.Kind}/{volume.VolumeType}";
                    byKindAndType[key] = byKindAndType.GetValueOrDefault(key) + 1;

                    if (volume.NamesShape)
                    {
                        placed++;
                        if (volume.ShapeHash != 0) placedNameRecord++;
                        if (Approx(volume.Size, new Vector3(0.01f), 1e-4f)) placedPlaceholder++;
                        if (car.Shape(volume.ShapeHash) is { } record && stubs.Contains(record.Hash))
                        {
                            placedWithStub++;
                        }
                    }
                    else
                    {
                        self++;
                        if (volume.ShapeHash == 0) selfNamesNothing++;
                        if (part.ParentFrame != 0 && part.ParentFrame != part.Frame) selfInParentSpace++;
                    }
                }
            }

            // …and the same population as the component view assembles it, so the two cannot drift apart.
            foreach (CarComponent component in car.Components)
            {
                foreach (CarCollision collision in component.Collisions)
                {
                    collisions++;
                    byRole[collision.Role] = byRole.GetValueOrDefault(collision.Role) + 1;
                    byShape[collision.Shape] = byShape.GetValueOrDefault(collision.Shape) + 1;
                    if (collision.Role == CarCollisionRole.Body) restatedInOwnSpace++;
                }
            }
        }

        sb.AppendLine($"  {cars} cars · {volumes} collision volumes · {collisions} collisions on components");
        sb.AppendLine($"  by role:  {string.Join("  ", byRole.OrderBy(p => p.Key)
            .Select(p => $"{CarCollision.RoleName(p.Key)} ×{p.Value}"))}");
        sb.AppendLine($"  by shape: {string.Join("  ", byShape.OrderBy(p => p.Key)
            .Select(p => $"{CarCollision.ShapeName(p.Key)} ×{p.Value}"))}");
        sb.AppendLine($"  placed shapes: {placed} — naming a record {placedNameRecord}, "
            + $"carrying the 1 cm placeholder {placedPlaceholder}, with a mirror stub {placedWithStub}");
        sb.AppendLine($"  self-describing: {self} — naming nothing {selfNamesNothing}, "
            + $"on a part that hangs off another {selfInParentSpace}");
        sb.AppendLine("  part kind / stored type:");
        foreach ((string key, int count) in byKindAndType.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"    {key,-16} {count}");
        }

        check("every collision volume of every car becomes exactly one collision on a component",
            volumes > 0 && collisions == volumes, $"{collisions} of {volumes}");
        check("a placed shape always names an ItemDesc record", placed > 0 && placedNameRecord == placed,
            $"{placedNameRecord} of {placed}");
        check("a placed shape always carries the 1 cm placeholder instead of stating its own size",
            placed > 0 && placedPlaceholder == placed, $"{placedPlaceholder} of {placed}");
        check("a self-describing volume never names a record", self > 0 && selfNamesNothing == self,
            $"{selfNamesNothing} of {self}");

        // The derivation this ticket adds, checked against that census rather than against itself: the type
        // the toolkit writes for a role has to be the type the shipped cars write for it.
        check("the type derived from a role is the type the corpus writes",
            CarCollision.TypeOfRole(CarCollisionRole.Body) == 5
            && CarCollision.TypeOfRole(CarCollisionRole.Glass) == 0
            && CarCollision.TypeOfRole(CarCollisionRole.Zone) == 6,
            "5 / 0 / 6");

        // …and the role a component's kind pre-fills has to be the one that kind of part actually carries.
        foreach ((string kind, CarCollisionRole want) in
                 new[] { ("window", CarCollisionRole.Glass), ("motor", CarCollisionRole.Zone),
                     ("body", CarCollisionRole.Body), ("door", CarCollisionRole.Body) })
        {
            uint type = CarCollision.TypeOfRole(want);
            int mine = byKindAndType.GetValueOrDefault(
                $"{kind}/{type.ToString(CultureInfo.InvariantCulture)}");
            int others = byKindAndType.Where(p => p.Key.StartsWith(kind + "/", StringComparison.Ordinal))
                .Sum(p => p.Value) - mine;
            check($"the role pre-filled for a {kind} is the one that kind of part carries most",
                mine > others, $"{CarCollision.RoleName(want)} ×{mine} against ×{others} of other roles");
        }
    }

    // ── the mirror ──

    /// <summary>Copies the focus car's working copy into the temp directory, so every edit below lands there
    /// and the game's own folders are never written to.</summary>
    private static string? Mirror(
        string focus, string folder, StringBuilder sb, Action<string, bool, string> check)
    {
        string source = MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(folder, focus + ".sds")));
        if (!File.Exists(Path.Combine(source, "SDSContent.xml")))
        {
            check("the focus car is extracted", false, focus);
            return null;
        }

        string mirror = Path.Combine(Scratch, focus);
        try
        {
            if (Directory.Exists(mirror)) Directory.Delete(mirror, recursive: true);
            Directory.CreateDirectory(mirror);
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(mirror, Path.GetFileName(file)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            check("the focus car can be mirrored into the temp directory", false, ex.Message);
            return null;
        }

        sb.AppendLine($"\n\n════ the focus car, mirrored into {mirror} ════");
        return mirror;
    }

    // ── what was authored comes back ──

    /// <summary>One case: what is asked for, and on which kind of component.</summary>
    private sealed record Case(
        string Kind, CarCollisionRole Role, CarCollisionShape Shape, Vector3 Size, Vector3 Position);

    private static void RoundTrip(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ role, shape, size and position survive a round trip ════");

        Case[] cases =
        [
            new("body", CarCollisionRole.Body, CarCollisionShape.Box,
                new Vector3(0.62f, 1.30f, 0.44f), new Vector3(0.11f, -0.23f, 0.37f)),
            new("body", CarCollisionRole.Body, CarCollisionShape.Sphere,
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.4f, 0.8f, 0.2f)),
            new("body", CarCollisionRole.Body, CarCollisionShape.Capsule,
                new Vector3(0.3f, 0.3f, 1.6f), new Vector3(0.05f, 1.1f, 0.15f)),
            new("body", CarCollisionRole.Body, CarCollisionShape.Cylinder,
                new Vector3(0.24f, 0.24f, 0.9f), new Vector3(-0.2f, -1.4f, 0.1f)),
            // A door carrying its own glass: the role is OVERRIDDEN, which is what shipped cars actually do.
            new("door", CarCollisionRole.Glass, CarCollisionShape.Box,
                new Vector3(0.90f, 0.05f, 0.55f), new Vector3(0.02f, 0.13f, 0.41f)),
            new("door", CarCollisionRole.Zone, CarCollisionShape.Box,
                new Vector3(0.40f, 0.40f, 0.40f), new Vector3(0.0f, 0.0f, 0.6f)),
        ];

        foreach (Case one in cases)
        {
            Car? car = Car.ReadFrom(mirror);
            CarComponent? component = Pick(car, one.Kind);
            if (car == null || component == null)
            {
                check($"the focus car has a {one.Kind} component", false, one.Kind);
                continue;
            }

            int had = component.Collisions.Count;
            CarEdit? edit = car.AddCollision(
                component, one.Role, one.Shape, one.Size, one.Position, out string? refusal);
            if (edit == null)
            {
                check($"a {CarCollision.ShapeName(one.Shape)} of role "
                    + $"{CarCollision.RoleName(one.Role)} can be added to a {one.Kind}", false,
                    refusal ?? "refused with no reason");
                continue;
            }
            CarSave saved = car.Save();

            // Read the archive again — not the aggregate that wrote it. What comes back is what a modder
            // reopening the car would see.
            Car? again = Car.ReadFrom(mirror);
            CarComponent? back = again == null ? null : Pick(again, one.Kind, component.Name);
            CarCollision? made = back?.Collisions.Count == had + 1 ? back.Collisions[had] : null;

            string what = $"{CarCollision.RoleName(one.Role)} {CarCollision.ShapeName(one.Shape)} "
                + $"on the {one.Kind}";
            sb.AppendLine($"  {what}: asked size {Print(one.Size)} at {Print(one.Position)}"
                + (made == null ? " — NOT FOUND AFTER SAVING"
                    : $" · came back {CarCollision.RoleName(made.Role)} "
                        + $"{CarCollision.ShapeName(made.Shape)} size {Print(made.Size)} "
                        + $"at {Print(made.Position)}"));

            check($"{what} comes back as what was asked for",
                saved.Ok && made != null && made.Role == one.Role && made.Shape == one.Shape
                && Approx(made.Size, one.Size, 2e-3f) && Approx(made.Position, one.Position, 2e-3f),
                saved.Ok ? "" : string.Join("; ", saved.Lost));
        }
    }

    // ── and what was written looks like a shipped car ──

    private static void Encoded(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ what reached the file, against how shipped cars write it ════");

        // ── a solid on the body ──
        Car? car = Car.ReadFrom(mirror);
        CarComponent? body = Pick(car, "body");
        if (car == null || body == null) { check("the focus car has a body", false, ""); return; }

        var size = new Vector3(0.5f, 0.7f, 0.3f);
        var at = new Vector3(0.1f, 0.9f, 0.2f);
        int shapesBefore = ShapeCount(mirror);
        int stubsBefore = StubCount(car);
        CarEdit? edit = car.AddCollision(
            body, CarCollisionRole.Body, CarCollisionShape.Box, size, at, out string? refusal);
        if (edit == null) { check("a solid can be added to the body", false, refusal ?? ""); return; }
        car.Save();

        Car? read = Car.ReadFrom(mirror);
        CarComponent? bodyAgain = Pick(read, "body");
        CarPhysicsVolume? volume = read == null || bodyAgain == null
            ? null
            : Last(read, bodyAgain);
        int stubsNow = read == null ? -1 : StubCount(read);
        sb.AppendLine($"  solid box on the body: stored type {volume?.VolumeType}, "
            + $"extents {Print(volume?.Size ?? default)}, names a record "
            + $"{(volume?.ShapeHash != 0 ? "yes" : "no")}, ItemDesc files "
            + $"{shapesBefore} → {ShapeCount(mirror)}, stubs {stubsBefore} → {stubsNow}");

        check("a solid is written as the type a shipped body volume is", volume?.VolumeType == 5,
            $"type {volume?.VolumeType}");
        check("a solid mints the ItemDesc record it needs and announces it in the manifest",
            ShapeCount(mirror) == shapesBefore + 1 && volume?.ShapeHash != 0
            && read?.Shape(volume!.ShapeHash) != null,
            $"{shapesBefore} → {ShapeCount(mirror)}");
        check("a solid carries the 1 cm placeholder rather than stating its own size",
            volume != null && Approx(volume.Size, new Vector3(0.01f), 1e-4f), Print(volume?.Size ?? default));
        check("a solid gets the mirror stub a shipped one has, without being asked for",
            stubsNow == stubsBefore + 1, $"{stubsBefore} → {stubsNow}");

        // The stub is the SECOND copy of the placement, and the two agree to the byte on 1060 of the 1097
        // shipped pairs. A stub standing anywhere else is a handle that lies about what it holds.
        FrameObjectCollision? stub = read == null || volume == null ? null : Stub(read, volume.ShapeHash);
        check("the mirror stub stands exactly where the prefab put it",
            stub != null && volume != null
            && Approx(stub.LocalTransform.Translation, volume.Transform.Translation, 1e-4f),
            stub == null ? "no stub" : Print(stub.LocalTransform.Translation));

        // …and the collision KNOWS about that stub, which is what a viewport takes hold of to place the box by
        // eye. Without it the gizmo landed on the component's bone and dragging the new collision moved the
        // whole part instead — on the body, the whole car.
        CarCollision? placed = bodyAgain?.Collisions.LastOrDefault();
        check("a solid names the handle the viewport drags to place it",
            placed is { HasHandle: true } && stub?.Name?.String is { Length: > 0 } name
            && placed.Handle == Illusion.Formats.Hashing.Fnv64.Hash(name),
            placed == null ? "no collision" : $"handle 0x{placed.Handle:X16}");

        // ── glass on a door: the space conversion is the thing that must NOT be visible ──
        Car? glassCar = Car.ReadFrom(mirror);
        CarComponent? door = Pick(glassCar, "door");
        if (glassCar == null || door == null) { check("the focus car has a door", false, ""); return; }

        var pane = new Vector3(0.8f, 0.1f, 0.5f);
        var paneAt = new Vector3(0.03f, 0.2f, 0.45f);
        int glassShapes = ShapeCount(mirror);
        CarEdit? glass = glassCar.AddCollision(
            door, CarCollisionRole.Glass, CarCollisionShape.Box, pane, paneAt, out refusal);
        if (glass == null) { check("glass can be added to a door", false, refusal ?? ""); return; }
        glassCar.Save();

        Car? afterGlass = Car.ReadFrom(mirror);
        CarComponent? doorAgain = Pick(afterGlass, "door", door.Name);
        CarPhysicsVolume? pageVolume = afterGlass == null || doorAgain == null
            ? null
            : Last(afterGlass, doorAgain);
        CarCollision? paneBack = doorAgain?.Collisions.LastOrDefault();

        sb.AppendLine($"  glass on the door \"{door.Name}\": stored type {pageVolume?.VolumeType}, "
            + $"extents {Print(pageVolume?.Size ?? default)}, names a record "
            + $"{(pageVolume?.ShapeHash != 0 ? "yes" : "no")}, ItemDesc files {glassShapes} → "
            + $"{ShapeCount(mirror)}");
        sb.AppendLine($"    authored at {Print(paneAt)} · stored at "
            + $"{Print(pageVolume?.Transform.Translation ?? default)} · read back at "
            + $"{Print(paneBack?.Position ?? default)}");

        check("glass is written as the type all 527 shipped window volumes are", pageVolume?.VolumeType == 0,
            $"type {pageVolume?.VolumeType}");
        check("glass states its own FULL size and names no record at all",
            pageVolume != null && Approx(pageVolume.Size, pane, 1e-3f) && pageVolume.ShapeHash == 0,
            Print(pageVolume?.Size ?? default));
        check("glass mints no ItemDesc record", ShapeCount(mirror) == glassShapes,
            $"{glassShapes} → {ShapeCount(mirror)}");
        // …and no handle either, because there is no frame in the corpus for one: glass is placed by its
        // numbers, and saying so is what keeps a modder from dragging the door instead.
        check("glass has nothing to drag, and says so rather than pretending",
            paneBack is { HasHandle: false }, $"handle {paneBack?.Handle ?? 0}");
        check("the matrix went into the space of the part the door hangs off, and the modder never sees it",
            pageVolume != null && paneBack != null
            && !Approx(pageVolume.Transform.Translation, paneAt, 1e-2f)
            && Approx(paneBack.Position, paneAt, 2e-3f),
            $"stored {Print(pageVolume?.Transform.Translation ?? default)} vs authored {Print(paneAt)}");

        // ── a capsule's axis ──
        Car? capsuleCar = Car.ReadFrom(mirror);
        CarComponent? capsuleOn = Pick(capsuleCar, "body");
        if (capsuleCar == null || capsuleOn == null) return;
        CarEdit? capsule = capsuleCar.AddCollision(
            capsuleOn, CarCollisionRole.Body, CarCollisionShape.Capsule,
            new Vector3(0.4f, 0.4f, 1.8f), Vector3.Zero, out refusal);
        if (capsule == null) { check("a capsule can be added", false, refusal ?? ""); return; }
        capsuleCar.Save();

        Car? afterCapsule = Car.ReadFrom(mirror);
        CarComponent? capsuleBody = Pick(afterCapsule, "body");
        CarPhysicsVolume? capsuleVolume = afterCapsule == null || capsuleBody == null
            ? null
            : Last(afterCapsule, capsuleBody);
        RigidBodyElement? rigid = capsuleVolume == null
            ? null
            : afterCapsule!.Shape(capsuleVolume.ShapeHash)?.Element as RigidBodyElement;
        sb.AppendLine($"  capsule 0.4 across by 1.8 long: radius {rigid?.Radius:0.###}, "
            + $"height {rigid?.Height:0.###} — the straight section, with a cap of one radius at each end");
        check("a capsule's length is written along its local Z, caps included",
            rigid is { Shape: RigidBodyShape.Capsule }
            && MathF.Abs(rigid.Radius - 0.2f) < 1e-3f && MathF.Abs(rigid.Height - 1.4f) < 1e-3f,
            rigid == null ? "no record" : $"r={rigid.Radius}, h={rigid.Height}");

        // ── a scale has nowhere to live, so it is baked ──
        Car? scaleCar = Car.ReadFrom(mirror);
        CarComponent? scaleBody = Pick(scaleCar, "body");
        CarCollision? grow = scaleBody?.Collisions
            .FirstOrDefault(c => c is { Role: CarCollisionRole.Body, Shape: CarCollisionShape.Box });
        if (scaleCar == null || grow == null) { check("there is a box to scale", false, ""); return; }

        Vector3 was = grow.Size;
        CarEdit? scaled = scaleCar.SetCollision(
            grow, grow.Size,
            Matrix4x4.CreateScale(2f, 3f, 4f) * Matrix4x4.CreateTranslation(grow.Position),
            out refusal);
        if (scaled == null) { check("a scaled placement is accepted", false, refusal ?? ""); return; }
        scaleCar.Save();

        Car? afterScale = Car.ReadFrom(mirror);
        CarCollision? grown = Pick(afterScale, "body")?.Collisions
            .ElementAtOrDefault(IndexOf(scaleBody!, grow));
        Matrix4x4 storedMatrix = afterScale == null || grown == null
            ? Matrix4x4.Identity
            : Last(afterScale, Pick(afterScale, "body")!, IndexOf(scaleBody!, grow))?.Transform
                ?? Matrix4x4.Identity;
        float scaleLeft = new Vector3(storedMatrix.M11, storedMatrix.M12, storedMatrix.M13).Length();
        sb.AppendLine($"  a box scaled 2×3×4 in the viewport: {Print(was)} → "
            + $"{Print(grown?.Size ?? default)}, and the placement keeps no scale ({scaleLeft:0.####})");
        check("a scale is baked into the shape's own dimensions rather than dropped",
            grown != null && Approx(grown.Size, was * new Vector3(2f, 3f, 4f), 2e-3f),
            $"{Print(was)} → {Print(grown?.Size ?? default)}");
        check("and the placement that carried it comes out unscaled",
            MathF.Abs(scaleLeft - 1f) < 1e-3f, scaleLeft.ToString("0.####", CultureInfo.InvariantCulture));
    }

    // ── removal takes the record and the stub with it ──

    private static void Removal(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ removing a collision ════");

        Car? car = Car.ReadFrom(mirror);
        CarComponent? body = Pick(car, "body");
        CarCollision? solid = body?.Collisions
            .FirstOrDefault(c => c.Role == CarCollisionRole.Body && !c.IsReadOnly);
        if (car == null || body == null || solid == null)
        {
            check("there is a solid collision to remove", false, "");
            return;
        }

        int shapes = ShapeCount(mirror);
        int stubs = StubCount(car);
        int had = body.Collisions.Count;
        CarEdit? edit = car.RemoveCollision(solid, out string? refusal);
        if (edit == null) { check("a solid can be removed", false, refusal ?? ""); return; }
        car.Save();

        Car? read = Car.ReadFrom(mirror);
        CarComponent? bodyAgain = Pick(read, "body");
        int stubsNow = read == null ? -1 : StubCount(read);
        sb.AppendLine($"  removed a {solid.Label}: collisions {had} → {bodyAgain?.Collisions.Count}, "
            + $"ItemDesc files {shapes} → {ShapeCount(mirror)}, stubs {stubs} → {stubsNow}");

        check("the collision is gone", bodyAgain?.Collisions.Count == had - 1,
            $"{had} → {bodyAgain?.Collisions.Count}");
        check("its ItemDesc record goes with it, and the manifest stops naming it",
            ShapeCount(mirror) == shapes - 1 && !Announced(mirror, shapes - 1),
            $"{shapes} → {ShapeCount(mirror)}");
        check("so does its mirror stub", stubsNow == stubs - 1, $"{stubs} → {stubsNow}");
    }

    // ── one intent, one undo step, and the bytes come back ──

    private static void Undo(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ undo restores a snapshot rather than reversing the derivation ════");

        Car? car = Car.ReadFrom(mirror);
        CarComponent? body = Pick(car, "body");
        if (car?.PrefabPath == null || body == null) { check("the focus car reads", false, ""); return; }

        byte[] prefabWas = File.ReadAllBytes(car.PrefabPath);
        byte[]? boxesWere = HitBoxBytes(car);
        string? rig = Rig(mirror);
        byte[] rigWas = rig == null ? [] : File.ReadAllBytes(rig);
        int shapes = ShapeCount(mirror);
        int stubs = StubCount(car);

        CarEdit? edit = car.AddCollision(
            body, CarCollisionRole.Body, CarCollisionShape.Box,
            new Vector3(0.9f, 0.4f, 0.25f), new Vector3(0f, -1.2f, 0.6f), out string? refusal);
        if (edit == null) { check("a collision can be added to undo", false, refusal ?? ""); return; }
        car.Save();
        bool changed = !File.ReadAllBytes(car.PrefabPath).AsSpan().SequenceEqual(prefabWas);

        // One step. The volume, the record, the manifest entry, the stub and the hit boxes went in together
        // and come back together.
        car.Restore(edit.Before);
        car.Save();

        Car? read = Car.ReadFrom(mirror);
        bool prefabBack = File.ReadAllBytes(car.PrefabPath).AsSpan().SequenceEqual(prefabWas);
        byte[]? boxesNow = read == null ? null : HitBoxBytes(read);

        int stubsNow = read == null ? -1 : StubCount(read);
        sb.AppendLine($"  add → save → undo → save: the prefab changed ({changed}) and came back "
            + $"({prefabBack}); ItemDesc files {shapes} → {ShapeCount(mirror)}; stubs {stubs} → {stubsNow}");

        check("the edit reached the file at all", changed, "the prefab never changed");
        check("one undo puts the prefab back byte for byte", prefabBack, "");
        check("and takes the ItemDesc record, the manifest entry and the stub with it",
            ShapeCount(mirror) == shapes && !Announced(mirror, shapes) && stubsNow == stubs,
            $"{shapes} → {ShapeCount(mirror)}");
        check("the hit boxes come back as they were, which reversing the derivation would not achieve",
            boxesWere != null && boxesNow != null && boxesWere.AsSpan().SequenceEqual(boxesNow),
            boxesWere == null ? "no boxes" : "");

        // The frame graph is the other half the stub went into. Serializing it renumbers indices and prunes
        // unreferenced blocks, so this is a real question rather than a formality.
        byte[] rigNow = rig == null ? [] : File.ReadAllBytes(rig);
        sb.AppendLine($"  the frame resource: {rigWas.Length} bytes → {rigNow.Length}, "
            + $"identical {rigWas.AsSpan().SequenceEqual(rigNow)}");
        check("and the frame graph the stub went into comes back byte for byte",
            rig != null && rigWas.AsSpan().SequenceEqual(rigNow),
            rig == null ? "no frame resource" : $"first differing at {FirstDiff(rigWas, rigNow)}");
    }

    // ── everything the modder did not touch is left as it was ──

    private static void Scoped(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ what an edit is allowed to disturb ════");

        Car? car = Car.ReadFrom(mirror);
        CarComponent? door = Pick(car, "door");
        if (car?.PrefabPath == null || door == null) { check("the focus car has a door", false, ""); return; }

        PrefabFile before = PrefabFile.Load(car.PrefabPath);
        int[] boxesWere = HitBoxWords(car);

        // What a rebuild would do if it were NOT scoped, so "for nothing else" is a measured claim rather
        // than a claim about a rebuild that happened to move nothing.
        int[] wouldMove = Drift(car);
        int[] doorPieces = Pieces(car, door);

        CarEdit? edit = car.AddCollision(
            door, CarCollisionRole.Glass, CarCollisionShape.Box,
            new Vector3(0.7f, 0.06f, 0.45f), new Vector3(0f, 0.1f, 0.4f), out string? refusal);
        if (edit == null) { check("glass can be added to the door", false, refusal ?? ""); return; }
        car.Save();

        Car? read = Car.ReadFrom(mirror);
        IReadOnlyList<string> moved = read == null ? [] : before.Diff(read.Prefab);
        int[] boxesNow = read == null ? [] : HitBoxWords(read);

        // Which pieces moved, and whose they are: the door's own, and nobody else's.
        var strayed = new List<int>();
        for (int i = 0; i < Math.Min(boxesWere.Length, boxesNow.Length); i++)
        {
            if (boxesWere[i] != boxesNow[i]) strayed.Add(i / 6);
        }
        int[] pieces = read == null ? [] : Pieces(read, Pick(read, "door", door.Name));

        sb.AppendLine($"  glass added to \"{door.Name}\": the prefab's fields that moved — "
            + $"{(moved.Count == 0 ? "none" : string.Join("; ", moved.Take(4)))}");
        sb.AppendLine($"  hit boxes: {boxesWere.Length / 6} pieces on the car, {doorPieces.Length} of them "
            + $"the door's; an UNSCOPED rebuild would move {wouldMove.Length} of them. The edit moved "
            + $"{strayed.Distinct().Count()}.");

        check("the edit shows up in the prefab as the part it was made on and nothing else",
            moved.Count > 0 && moved.All(f => f.Contains(
                $"DeformParts[{door.PartIndex.ToString(CultureInfo.InvariantCulture)}]",
                StringComparison.Ordinal)),
            moved.Count == 0 ? "nothing moved at all" : string.Join("; ", moved.Take(4)));

        // A collision edit changes no GEOMETRY, so it must move no hit box at all. The rule the toolkit
        // rebuilds by reproduces 65.3 % of what a car ships with, and on this car an unscoped rebuild moves
        // 90 of 187 boxes — re-aiming a third of a car's bullet gating because somebody resized a box is an
        // edit nobody asked for.
        check("a collision edit moves no hit box, because it changes no geometry",
            strayed.Count == 0, $"{strayed.Distinct().Count()} pieces moved");

        // The scoped rebuild itself, asked for directly — this is the capability, and it has to stay inside
        // the component it is given.
        Car? again = Car.ReadFrom(mirror);
        (CarComponent? drifting, int[] itsPieces) = Drifting(again);
        if (again == null || drifting == null)
        {
            sb.AppendLine("  no component's boxes disagree with its geometry, so the scoped rebuild has "
                + "nothing to move on this car");
            return;
        }
        int[] before2 = HitBoxWords(again);
        int rebuiltNow = again.RebuildHitBoxes(drifting);
        int[] after2 = HitBoxWords(again);
        var movedPieces = new HashSet<int>();
        for (int i = 0; i < Math.Min(before2.Length, after2.Length); i++)
        {
            if (before2[i] != after2[i]) movedPieces.Add(i / 6);
        }
        sb.AppendLine($"  rebuilding \"{drifting.Name}\" on its own: {rebuiltNow} boxes moved over "
            + $"{itsPieces.Length} pieces, {movedPieces.Count(p => !itsPieces.Contains(p))} outside it");
        check("a rebuild asked for on one component moves its boxes and nobody else's",
            rebuiltNow > 0 && movedPieces.Count > 0 && movedPieces.All(itsPieces.Contains),
            $"{rebuiltNow} moved, {movedPieces.Count(p => !itsPieces.Contains(p))} outside");
    }

    /// <summary>A component whose own pieces' boxes disagree with what its geometry implies — what makes the
    /// scoped rebuild measurable rather than a call that happens to move nothing.</summary>
    private static (CarComponent? Component, int[] Pieces) Drifting(Car? car)
    {
        if (car == null) return (null, []);
        int[] drift = Drift(car);
        foreach (CarComponent component in car.Components)
        {
            int[] pieces = Pieces(car, component);
            if (pieces.Length > 0 && pieces.Any(drift.Contains)) return (component, pieces);
        }
        return (null, []);
    }

    // ── undo and redo, run until they would show ──

    /// <summary>
    /// What repeated undo/redo does to the frame graph. Both halves are things that look right once and go
    /// wrong on the fourth keystroke: an attachment list that grows by one every restore, and a stub put back
    /// without the two parent slots the removal cleared.
    /// </summary>
    private static void Cycles(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ undo and redo, five times over ════");

        Car? car = Car.ReadFrom(mirror);
        CarComponent? body = Pick(car, "body");
        if (car?.PrefabPath == null || body == null) { check("the focus car reads", false, ""); return; }

        string? rig = Rig(mirror);
        byte[] rigWas = rig == null ? [] : File.ReadAllBytes(rig);
        int attachmentsWere = Attachments(car);

        CarEdit? edit = car.AddCollision(
            body, CarCollisionRole.Body, CarCollisionShape.Box,
            new Vector3(0.3f, 0.3f, 0.3f), new Vector3(0f, 0f, 1f), out string? refusal);
        if (edit == null) { check("a collision can be added", false, refusal ?? ""); return; }
        car.Save();
        int attachmentsWithIt = Attachments(car);

        for (int i = 0; i < 5; i++)
        {
            car.Restore(edit.Before);
            car.Save();
            car.Restore(edit.After);
            car.Save();
        }
        int attachmentsAfter = Attachments(car);
        sb.AppendLine($"  the model's attachments: {attachmentsWere} → {attachmentsWithIt} with the "
            + $"collision → {attachmentsAfter} after five undo/redo cycles");
        check("undo and redo do not pile up attachments on the model",
            attachmentsAfter == attachmentsWithIt,
            $"{attachmentsWithIt} → {attachmentsAfter}");

        // …and back to nothing, five cycles later: the archive has to be the one that was there.
        car.Restore(edit.Before);
        car.Save();
        byte[] rigNow = rig == null ? [] : File.ReadAllBytes(rig);
        sb.AppendLine($"  the frame resource after all of that: {rigWas.Length} bytes → {rigNow.Length}, "
            + $"identical {rigWas.AsSpan().SequenceEqual(rigNow)}");
        check("and the frame graph is the one that was there before any of it",
            rig != null && rigWas.AsSpan().SequenceEqual(rigNow),
            rig == null ? "no frame resource" : $"first differing at {FirstDiff(rigWas, rigNow)}");

        // A REMOVAL taken back: the stub comes out of the graph, which clears the two parent slots — and it
        // has to go back in with them, or the frame is anchored to no scene and loads invisible.
        Car? second = Car.ReadFrom(mirror);
        CarComponent? bodyAgain = Pick(second, "body");
        CarCollision? solid = bodyAgain?.Collisions
            .FirstOrDefault(c => c.Role == CarCollisionRole.Body && !c.IsReadOnly);
        if (second?.PrefabPath == null || solid == null)
        {
            check("there is a solid collision to remove and put back", false, "");
            return;
        }
        byte[] rigBefore = rig == null ? [] : File.ReadAllBytes(rig);
        FrameObjectCollision? stub = Stub(second, Volume(second, solid)?.ShapeHash ?? 0);
        (string parentWas, string rootWas) = Wiring(stub);

        CarEdit? removal = second.RemoveCollision(solid, out refusal);
        if (removal == null) { check("it can be removed", false, refusal ?? ""); return; }
        second.Save();
        second.Restore(removal.Before);
        second.Save();

        (string parentNow, string rootNow) = Wiring(stub);
        byte[] rigBack = rig == null ? [] : File.ReadAllBytes(rig);
        sb.AppendLine($"  the stub's wiring across remove → undo: parent {parentWas} → {parentNow}, "
            + $"anchor {rootWas} → {rootNow}");
        check("a stub put back by an undo keeps the wiring it had, rather than becoming an orphan frame",
            string.Equals(parentWas, parentNow, StringComparison.Ordinal)
            && string.Equals(rootWas, rootNow, StringComparison.Ordinal),
            $"{parentWas}/{rootWas} → {parentNow}/{rootNow}");
        check("…and the frame resource comes back byte for byte",
            rig != null && rigBefore.AsSpan().SequenceEqual(rigBack),
            rig == null ? "no frame resource" : $"first differing at {FirstDiff(rigBefore, rigBack)}");
    }

    /// <summary>What a stub hangs off and what anchors it, as names — the two slots a removal clears.</summary>
    private static (string Parent, string Root) Wiring(FrameObjectCollision? stub) =>
        stub == null
            ? ("(no stub)", "(no stub)")
            : (stub.Parent?.Name.ToString() ?? "(none)", stub.Root?.Name.ToString() ?? "(none)");

    private static int Attachments(Car car) =>
        car.Frames?.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault()
            ?.AttachmentReferences?.Length ?? 0;

    /// <summary>The prefab volume behind one collision.</summary>
    private static CarPhysicsVolume? Volume(Car car, CarCollision collision)
    {
        IReadOnlyList<CarDeformPart> parts = car.Prefab.CarDeformParts;
        return collision.PartIndex >= 0 && collision.PartIndex < parts.Count
            ? parts[collision.PartIndex].Volumes.ElementAtOrDefault(collision.VolumeIndex)
            : null;
    }

    // ── a file somebody else wrote ──

    /// <summary>
    /// The aggregate writes the WHOLE prefab from the copy it read, and four other modules still write that
    /// same file directly. A save that would put its own copy over somebody else's write has to refuse.
    /// </summary>
    private static void Overtaken(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ a prefab somebody else wrote in the meantime ════");

        Car? car = Car.ReadFrom(mirror);
        CarComponent? body = Pick(car, "body");
        if (car?.PrefabPath == null || body == null) { check("the focus car reads", false, ""); return; }

        CarEdit? edit = car.AddCollision(
            body, CarCollisionRole.Zone, CarCollisionShape.Box,
            new Vector3(0.2f, 0.2f, 0.2f), Vector3.Zero, out string? refusal);
        if (edit == null) { check("a collision can be added", false, refusal ?? ""); return; }

        // Somebody else — a scene save, a bridge push, a second window — writes the file between the read
        // and the save. Their bytes are what has to survive.
        Car? other = Car.ReadFrom(mirror);
        CarComponent? otherBody = Pick(other, "body");
        if (other == null || otherBody == null) { check("the car reads twice", false, ""); return; }
        other.AddCollision(otherBody, CarCollisionRole.Zone, CarCollisionShape.Box,
            new Vector3(0.9f, 0.9f, 0.9f), Vector3.Zero, out _);
        other.Save();
        byte[] theirs = File.ReadAllBytes(car.PrefabPath);

        CarSave saved = car.Save();
        sb.AppendLine($"  saving over somebody else's write: {(saved.Ok ? "ALLOWED" : "refused")}"
            + (saved.Ok ? "" : $" — {string.Join("; ", saved.Lost)}"));
        check("a save that would write over a change made since the car was read refuses and says so",
            !saved.Ok && saved.Written.Count == 0 && saved.Lost.Count == 1, string.Join("; ", saved.Lost));
        check("…and the other write is still on disk",
            File.ReadAllBytes(car.PrefabPath).AsSpan().SequenceEqual(theirs), "");
    }

    // ── a refusal writes nothing ──

    private static void Refusals(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ what is refused, and with what reason ════");

        Car? car = Car.ReadFrom(mirror);
        if (car?.PrefabPath == null) { check("the focus car reads", false, ""); return; }
        byte[] was = File.ReadAllBytes(car.PrefabPath);
        int shapes = ShapeCount(mirror);

        // A hull: the vendored cooker knows one verb and it is not this one.
        CarCollision? hull = car.Components
            .SelectMany(c => c.Collisions)
            .FirstOrDefault(c => c.Shape == CarCollisionShape.Hull);
        string? hullRefusal = null;
        if (hull != null)
        {
            car.SetCollision(hull, hull.Size * 2f, hull.Placement, out hullRefusal);
        }
        sb.AppendLine($"  resizing a hull: {hullRefusal ?? "(this car carries no hull)"}");
        check("a cooked hull is read-only and says why",
            hull != null && hullRefusal is { Length: > 0 } && hullRefusal.Contains("cook", StringComparison.OrdinalIgnoreCase),
            hull == null ? "no hull on this car" : hullRefusal ?? "no reason given");

        // A bare component: nothing to hang a volume off until it is given a deform part.
        CarComponent? bare = car.Components.FirstOrDefault(c => c.IsBare);
        string? bareRefusal = null;
        CarEdit? onBare = bare == null
            ? null
            : car.AddCollision(bare, CarCollisionRole.Body, CarCollisionShape.Box, Vector3.One,
                Vector3.Zero, out bareRefusal);
        sb.AppendLine($"  a component with no deform part: {bareRefusal ?? "(none on this car)"}");
        check("a bare component refuses a collision and says what to do about it",
            bare != null && onBare == null && bareRefusal is { Length: > 0 },
            bare == null ? "no bare component" : bareRefusal ?? "");

        // Glass is always a plain box — a self-describing volume has nowhere to say it is a sphere.
        CarComponent? body = Pick(car, "body");
        string? sphereRefusal = null;
        CarEdit? sphereGlass = body == null
            ? null
            : car.AddCollision(body, CarCollisionRole.Glass, CarCollisionShape.Sphere,
                Vector3.One, Vector3.Zero, out sphereRefusal);
        sb.AppendLine($"  glass as a sphere: {sphereRefusal}");
        check("glass and zones refuse anything but a box, with the reason",
            sphereGlass == null && sphereRefusal is { Length: > 0 }, sphereRefusal ?? "");

        // A capsule shorter than it is wide has no straight section to describe.
        string? flatRefusal = null;
        CarEdit? flat = body == null
            ? null
            : car.AddCollision(body, CarCollisionRole.Body, CarCollisionShape.Capsule,
                new Vector3(1f, 1f, 0.5f), Vector3.Zero, out flatRefusal);
        sb.AppendLine($"  a capsule wider than it is long: {flatRefusal}");
        check("a capsule that cannot be described is refused rather than rounded off",
            flat == null && flatRefusal is { Length: > 0 }, flatRefusal ?? "");

        CarSave saved = car.Save();
        check("and not one of those refusals wrote anything",
            File.ReadAllBytes(car.PrefabPath).AsSpan().SequenceEqual(was) && ShapeCount(mirror) == shapes
            && saved.Ok,
            $"{shapes} → {ShapeCount(mirror)}");
    }

    // ── a save sent somewhere else takes the whole archive with it ──

    /// <summary>
    /// A redirected save writes the shape record and its manifest entry into the MIRROR, not into the folder
    /// the car was read from.
    ///
    /// <para>
    /// This is what lets the corpus be measured without the player's install ever being written to, and it is
    /// not free: a shape record is only real once the manifest names it, and the manifest is a file of its
    /// own. A redirect that carried the prefab but left the entry behind would write into the very folder the
    /// redirect exists to protect.
    /// </para>
    /// </summary>
    private static void Redirected(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ a save sent somewhere else ════");

        Car? car = Car.ReadFrom(mirror);
        CarComponent? body = Pick(car, "body");
        if (car?.PrefabPath == null || body == null) { check("the focus car reads", false, ""); return; }

        string elsewhere = Path.Combine(Scratch, "elsewhere");
        try { if (Directory.Exists(elsewhere)) Directory.Delete(elsewhere, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* overwritten below */ }

        byte[] was = File.ReadAllBytes(car.PrefabPath);
        int shapes = ShapeCount(mirror);
        CarEdit? edit = car.AddCollision(
            body, CarCollisionRole.Body, CarCollisionShape.Box,
            new Vector3(0.4f, 0.4f, 0.4f), Vector3.Zero, out string? refusal);
        if (edit == null) { check("a collision can be added", false, refusal ?? ""); return; }

        CarSave saved = car.Save(path => Path.Combine(elsewhere, Path.GetRelativePath(mirror, path)));
        int there = ShapeCount(elsewhere);
        sb.AppendLine($"  wrote {saved.Written.Count} files into the mirror: "
            + string.Join(", ", saved.Written.Select(Path.GetFileName)));
        sb.AppendLine($"  ItemDesc records — where it was read from: {shapes} → {ShapeCount(mirror)}; "
            + $"where it was sent: {there}");

        check("the redirected save leaves the folder it was read from untouched",
            saved.Ok && File.ReadAllBytes(car.PrefabPath).AsSpan().SequenceEqual(was)
            && ShapeCount(mirror) == shapes,
            $"{shapes} → {ShapeCount(mirror)}");
        // The mirror holds only what this save WROTE — the records it did not touch stayed where they were,
        // which is the whole point of writing only what changed. What has to be true is that the record it
        // did mint is there, and that the mirror's own manifest is the one naming it.
        string? minted = saved.Written.FirstOrDefault(
            f => Path.GetExtension(f).Equals(".ids", StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<string> named = Named(elsewhere);
        check("and the shape record it minted is in the mirror, named by the mirror's own manifest",
            there == shapes + 1 && minted != null && File.Exists(minted)
            && named.Any(f => string.Equals(f, minted, StringComparison.OrdinalIgnoreCase)),
            $"{there} records, minted {Path.GetFileName(minted) ?? "(none)"}");
    }

    // ── plumbing ──

    /// <summary>A component of the given kind, preferring one that resolves and has a bone in the rig.</summary>
    private static CarComponent? Pick(Car? car, string kind, string? named = null) =>
        car?.Components.FirstOrDefault(c =>
            string.Equals(c.Kind, kind, StringComparison.Ordinal) && c.BoneResolves
            && (named == null || string.Equals(c.Name, named, StringComparison.Ordinal)));

    /// <summary>The volume behind the last (or n-th) collision of a component, straight out of the prefab.</summary>
    private static CarPhysicsVolume? Last(Car car, CarComponent component, int index = -1)
    {
        IReadOnlyList<CarDeformPart> parts = car.Prefab.CarDeformParts;
        if (component.PartIndex < 0 || component.PartIndex >= parts.Count) return null;
        IReadOnlyList<CarPhysicsVolume> volumes = parts[component.PartIndex].Volumes;
        if (volumes.Count == 0) return null;
        return index < 0 ? volumes[^1] : volumes.ElementAtOrDefault(index);
    }

    private static int IndexOf(CarComponent component, CarCollision collision)
    {
        for (int i = 0; i < component.Collisions.Count; i++)
        {
            if (ReferenceEquals(component.Collisions[i], collision)) return i;
        }
        return -1;
    }

    private static FrameObjectCollision? Stub(Car car, ulong dataHash)
    {
        ItemDescFile? record = car.Shape(dataHash);
        if (record == null) return null;
        return car.Frames?.FrameObjects?.Values.OfType<FrameObjectCollision>()
            .FirstOrDefault(s => s.Hash == record.Hash);
    }

    private static int StubCount(Car car) =>
        car.Frames?.FrameObjects?.Values.OfType<FrameObjectCollision>().Count() ?? 0;

    /// <summary>The archive's frame resource file, or null when it carries none.</summary>
    private static string? Rig(string extracted)
    {
        try { return SdsManifest.Load(extracted).GetFiles("FrameResource").FirstOrDefault(); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { return null; }
    }

    private static int ShapeCount(string extracted) => Named(extracted).Count;

    /// <summary>The ItemDesc records a folder's manifest names, as full paths.</summary>
    private static IReadOnlyList<string> Named(string extracted)
    {
        try { return SdsManifest.Load(extracted).GetFiles("ItemDesc"); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { return []; }
    }

    /// <summary>Whether the manifest names a file that is not on disk — the one thing that fails a Build
    /// outright, and the reason a removal has to unsay the entry as well as delete the file.</summary>
    private static bool Announced(string extracted, int expected)
    {
        IReadOnlyList<string> files = Named(extracted);
        return files.Count != expected || files.Any(f => !File.Exists(f));
    }

    private static byte[]? HitBoxBytes(Car car)
    {
        int[] words = HitBoxWords(car);
        if (words.Length == 0) return null;
        var bytes = new byte[words.Length * 4];
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Every hit box of the car's model as six numbers — centre and size, per piece.</summary>
    private static int[] HitBoxWords(Car car)
    {
        FrameObjectModel? model = car.Frames?.FrameObjects?.Values.OfType<FrameObjectModel>()
            .FirstOrDefault();
        FrameObjectModel.HitBoxInfo[] boxes = model?.HitBoxes ?? [];
        var words = new int[boxes.Length * 6];
        for (int i = 0; i < boxes.Length; i++)
        {
            words[(i * 6) + 0] = boxes[i].Position.S1;
            words[(i * 6) + 1] = boxes[i].Position.S2;
            words[(i * 6) + 2] = boxes[i].Position.S3;
            words[(i * 6) + 3] = boxes[i].Size.S1;
            words[(i * 6) + 4] = boxes[i].Size.S2;
            words[(i * 6) + 5] = boxes[i].Size.S3;
        }
        return words;
    }

    /// <summary>The flat piece ordinals whose hit box the model's own geometry does not agree with — what an
    /// UNSCOPED rebuild would move.</summary>
    private static int[] Drift(Car car)
    {
        FrameObjectModel? model = car.Frames?.FrameObjects?.Values.OfType<FrameObjectModel>()
            .FirstOrDefault();
        FrameObjectModel.HitBoxInfo[] had = model?.HitBoxes ?? [];
        FrameObjectModel.HitBoxInfo[]? built = model == null
            ? null
            : Assets.Frames.HitBoxBuilder.Compute(model);
        if (built == null || built.Length != had.Length) return [];

        var moved = new List<int>();
        for (int i = 0; i < had.Length; i++)
        {
            if (had[i].Position.S1 != built[i].Position.S1 || had[i].Position.S2 != built[i].Position.S2
                || had[i].Position.S3 != built[i].Position.S3 || had[i].Size.S1 != built[i].Size.S1
                || had[i].Size.S2 != built[i].Size.S2 || had[i].Size.S3 != built[i].Size.S3)
            {
                moved.Add(i);
            }
        }
        return [.. moved];
    }

    /// <summary>The flat piece ordinals belonging to one component's bone — whose hit boxes an edit on it is
    /// allowed to move.</summary>
    private static int[] Pieces(Car car, CarComponent? component)
    {
        if (component == null) return [];
        FrameObjectModel? model = car.Frames?.FrameObjects?.Values.OfType<FrameObjectModel>()
            .FirstOrDefault();
        if (model == null) return [];

        byte[] remap;
        string[] bones;
        try
        {
            var levels = model.GetBlendInfoObject().BoneIndexInfos ?? [];
            remap = levels.Length > 0 ? levels[0].BoneRemapIDs ?? [] : [];
            bones = [.. (model.GetSkeletonObject().BoneNames ?? []).Select(b => b.ToString() ?? "")];
        }
        catch (Exception) { return []; }

        var mine = new List<int>();
        int at = 0;
        foreach (FrameObjectModel.WeightedByMeshSplit split in model.BlendMeshSplits ?? [])
        {
            int bone = split.BlendIndex < remap.Length ? remap[split.BlendIndex] : -1;
            bool ours = bone >= 0 && bone < bones.Length && bones[bone].Length > 0
                && Formats.Hashing.Fnv64.Hash(bones[bone]) == component.BoneHash;
            foreach (FrameObjectModel.BlendMeshSplitInfo _ in split.Data ?? [])
            {
                if (ours) mine.Add(at);
                at++;
            }
        }
        return [.. mine];
    }

    private static string Print(Vector3 v) =>
        $"({v.X.ToString("0.###", CultureInfo.InvariantCulture)}, "
        + $"{v.Y.ToString("0.###", CultureInfo.InvariantCulture)}, "
        + $"{v.Z.ToString("0.###", CultureInfo.InvariantCulture)})";
}
