using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Prefab;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Markers under the component they hang off: where each one lands, what its own parallel row carries, and
/// whether an edit to that row survives a round trip through the aggregate and reaches the file alone.
///
/// <para>
/// NOTHING IS WRITTEN INTO THE GAME'S FOLDERS. The focus car's working copy is mirrored into the temp
/// directory and every edit lands in the mirror, which is also what makes the comparison possible: the
/// original is still there to compare against.
/// </para>
/// <para>Output: %TEMP%\illusion_car_markers.txt</para>
/// </summary>
internal static class CarMarkerProbes
{
    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "illusion_car_markers");

    internal static void RunCarMarkersProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_markers.txt");
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
                Placement(sb, mirror, Check);
                Dragged(sb, mirror, Check);
                AddRemove(sb, mirror, Check);
                Undo(sb, mirror, Check);
                Scoped(sb, mirror, Check);
                Refusals(sb, mirror, Check);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            sb.Insert(0, $"MARKERS UNDER THEIR COMPONENT ({focus}): {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // ── the corpus is the oracle: where a marker lands and what its row carries ──

    private static void Corpus(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("════ where a marker lands, and what its row says ════");

        int cars = 0, markers = 0, unresolved = 0, onAComponent = 0, onAPart = 0, onTheBody = 0, onOwnBone = 0;
        var byRole = new Dictionary<CarMarkerRole, int>();
        var homeless = new List<string>();
        int seats = 0, seatIndexIsPosition = 0, seatIndexUnique = 0, seatIndexInRange = 0;
        int seatPositionNearDummy = 0, seatPositionIsDummy = 0;
        var seatExamples = new List<string>();
        int boxes = 0, boxExact = 0, boxNear = 0, boxCentreHalf = 0, boxCentreHalfExact = 0, boxTurned = 0;
        float boxWorst = 0f;
        int rows = 0, rowsOnAComponent = 0;
        var byRowKind = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            Car? car;
            try { car = Car.ReadFrom(extracted); }
            catch (Exception) { continue; }
            if (car?.Prefab.Car is not { } assembly) continue;
            cars++;

            var dummies = new Dictionary<ulong, FrameObjectBase>();
            foreach (object o in car.Frames?.FrameObjects?.Values ?? Enumerable.Empty<object>())
            {
                if (o is FrameObjectBase f && f.Name?.String is { Length: > 0 } name)
                {
                    dummies.TryAdd(Formats.Hashing.Fnv64.Hash(name), f);
                }
            }

            foreach (CarMarker marker in car.Markers)
            {
                markers++;
                byRole[marker.Role] = byRole.GetValueOrDefault(marker.Role) + 1;
                if (!marker.Resolved) unresolved++;
                if (marker.OnOwnBone) onOwnBone++;
                CarComponent? owner = car.ComponentOfBone(marker.Bone);
                if (owner != null) onAComponent++;
                if (owner is { IsBare: false }) onAPart++;
                if (owner == null || ReferenceEquals(owner, car.Body)) onTheBody++;
                if (owner == null && homeless.Count < 8)
                {
                    homeless.Add($"{sds.Name} {marker.Label} on \"{marker.Name}\"");
                }
            }

            // A seat's stored index against its position in the list, and its stored position against where
            // the Dummy it names actually stands — two things the spec leaves open and an edit surface has to
            // settle before it offers either of them as a number a modder may type.
            IReadOnlyList<CarPrefab.Seat> list = assembly.Seats;
            var indices = new HashSet<uint>();
            for (int i = 0; i < list.Count; i++)
            {
                seats++;
                if (list[i].Index == (uint)i) seatIndexIsPosition++;
                if (indices.Add(list[i].Index)) seatIndexUnique++;
                if (list[i].Index < (uint)list.Count) seatIndexInRange++;
                if (dummies.TryGetValue(list[i].Frame, out FrameObjectBase? dummy)
                    && (dummy.WorldTransform.Translation - list[i].Position).Length() < 1e-4f)
                {
                    seatPositionIsDummy++;
                }
                if (dummies.TryGetValue(list[i].Frame, out FrameObjectBase? near)
                    && (near.WorldTransform.Translation - list[i].Position).Length() < 0.05f)
                {
                    seatPositionNearDummy++;
                }
            }
            if (list.Count > 0 && seatExamples.Count < 6)
            {
                seatExamples.Add($"{sds.Name}: " + string.Join(", ", list.Select(s => s.Index)));
            }

            // A climb box's row against the Dummy it names, read THREE ways — because the sync that rewrites
            // the row from the frame runs on every frame-resource save, and how nearly the two already agree
            // decides whether that sync leaves a shipped car alone or quietly rewrites most of its boxes.
            IReadOnlyList<CarPrefab.ClimbBox> climbs = assembly.ClimbBoxes;
            for (int i = 0; i < climbs.Count; i++)
            {
                boxes++;
                if (dummies.GetValueOrDefault(climbs[i].Dummy) is not FrameObjectDummy dummy) continue;
                (Vector3 min, Vector3 max) = Car.BoxOf(dummy);
                float off = MathF.Max(
                    (min - climbs[i].Min).Length(), (max - climbs[i].Max).Length());
                if (off < 1e-4f) boxExact++;
                if (off < 2e-2f) boxNear++;
                boxWorst = MathF.Max(boxWorst, off);

                Vector3 half = (dummy.Bounds.Max - dummy.Bounds.Min) * 0.5f;
                Vector3 centre = Vector3.Transform(
                    (dummy.Bounds.Min + dummy.Bounds.Max) * 0.5f, dummy.WorldTransform);
                float said = MathF.Max(
                    (centre - ((climbs[i].Min + climbs[i].Max) * 0.5f)).Length(),
                    (half - ((climbs[i].Max - climbs[i].Min) * 0.5f)).Length());
                if (said < 2e-2f) boxCentreHalf++;
                if (said < 1e-4f) boxCentreHalfExact++;
                if (!Turned(dummy.WorldTransform)) continue;
                boxTurned++;
            }

            foreach (CarComponent component in car.Components)
            {
                foreach (CarComponentRow row in component.Rows)
                {
                    rows++;
                    rowsOnAComponent++;
                    byRowKind[row.Kind] = byRowKind.GetValueOrDefault(row.Kind) + 1;
                }
            }
        }

        sb.AppendLine($"  {cars} cars · {markers} markers");
        sb.AppendLine("  by role: " + string.Join("  ", byRole.OrderBy(p => p.Key)
            .Select(p => $"{p.Key} ×{p.Value}")));
        sb.AppendLine($"  resolving to a bone {markers - unresolved} · landing on a component {onAComponent} "
            + $"· on a component with a deform part {onAPart} · on the body {onTheBody} "
            + $"· naming their own bone {onOwnBone}");
        foreach (string one in homeless) sb.AppendLine("    reaching no component: " + one);
        sb.AppendLine($"  seats {seats} — stored index equals its position {seatIndexIsPosition}, unique "
            + $"within its car {seatIndexUnique}, below the seat count {seatIndexInRange}; stored position IS "
            + $"where its Dummy stands {seatPositionIsDummy}, within 5 cm of it {seatPositionNearDummy}");
        foreach (string one in seatExamples) sb.AppendLine("    seat indices: " + one);
        sb.AppendLine($"  climb boxes {boxes} — the row is the placed Dummy's own corners exactly "
            + $"{boxExact}, within 2 cm {boxNear}; as centre-and-half-size exactly {boxCentreHalfExact}, "
            + $"within 2 cm {boxCentreHalf}; turned Dummies {boxTurned}, worst corner disagreement "
            + $"{boxWorst:F3} m");
        sb.AppendLine($"  component rows {rows} on a component {rowsOnAComponent}: "
            + string.Join("  ", byRowKind.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key} ×{p.Value}")));

        check("every marker of every shipped car reaches a bone", markers > 0 && unresolved == 0,
            $"{unresolved} of {markers} unresolved");
    }

    /// <summary>Whether a world matrix turns its box — what decides if the eight-corner box the row wants is
    /// the Dummy's own box at all.</summary>
    private static bool Turned(Matrix4x4 world) =>
        MathF.Abs(world.M12) + MathF.Abs(world.M13) + MathF.Abs(world.M21) + MathF.Abs(world.M23)
        + MathF.Abs(world.M31) + MathF.Abs(world.M32) > 1e-4f;

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

    // ── what was typed comes back ──

    /// <summary>
    /// A number typed onto a marker's row, or onto one of a component's own-bone rows, read again from the
    /// ARCHIVE that was written rather than from the aggregate that wrote it.
    /// </summary>
    private static void RoundTrip(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ a marker's own numbers survive a round trip ════");

        // A seat: the number that says which seat it is, and the kind of seat it is.
        Edit(sb, mirror, check, "Seat number on a seat",
            car => car.Markers.FirstOrDefault(m => m.Role == CarMarkerRole.Seat),
            fields => [.. fields.Select(f =>
                f.Label == "Seat number" ? f with { Number = 3f }
                : f.Label == "Seat type" ? f with { Number = 2f } : f)],
            marker => marker.Fields.Any(f => f.Label == "Seat number" && f.Number == 3f)
                && marker.Fields.Any(f => f.Label == "Seat type" && f.Number == 2f));

        // …and the same for the rows that name a component's OWN bone.
        Row(sb, mirror, check, "window", "Depth",
            fields => [.. fields.Select(f => f.Label == "Depth" ? f with { Number = 0.037f } : f)],
            row => row.Fields.Any(f => f.Label == "Depth" && MathF.Abs(f.Number - 0.037f) < 1e-6f));
        Row(sb, mirror, check, "window", "Rolls down",
            fields => [.. fields.Select(f => f.Label == "Rolls down" ? f with { Number = 1f } : f)],
            row => row.Fields.Any(f => f.Label == "Rolls down" && f.Number == 1f));
        Row(sb, mirror, check, "axle", "Brake drum mass",
            fields => [.. fields.Select(f =>
                f.Label == "Brake drum mass" ? f with { Number = 12.5f } : f)],
            row => row.Fields.Any(f => f.Label == "Brake drum mass" && MathF.Abs(f.Number - 12.5f) < 1e-4f));
        Row(sb, mirror, check, "door", "Handle",
            fields => [.. fields.Select(f =>
                f.Label == "Handle" ? f with { Point = new Vector3(0.11f, -0.22f, 0.33f) } : f)],
            row => row.Fields.Any(f => f.Label == "Handle"
                && (f.Point - new Vector3(0.11f, -0.22f, 0.33f)).Length() < 1e-5f));
    }

    /// <summary>Edits a marker's fields, saves, reads the archive again and asks the question of what came
    /// back — never of the aggregate that wrote it.</summary>
    private static void Edit(
        StringBuilder sb, string mirror, Action<string, bool, string> check, string what,
        Func<Car, CarMarker?> pick, Func<IReadOnlyList<CarField>, IReadOnlyList<CarField>> change,
        Func<CarMarker, bool> want)
    {
        Car? car = Car.ReadFrom(mirror);
        CarMarker? marker = car == null ? null : pick(car);
        if (car == null || marker == null) { check($"the focus car has {what}", false, ""); return; }

        CarEdit? edit = car.SetMarker(marker, change(marker.Fields), out string? refusal);
        if (edit == null) { check($"{what} can be written", false, refusal ?? ""); return; }
        CarSave saved = car.Save();
        if (!saved.Ok) { check($"{what} saves", false, string.Join("; ", saved.Lost)); return; }

        Car? read = Car.ReadFrom(mirror);
        CarMarker? back = read == null ? null : pick(read);
        sb.AppendLine($"  {what}: {edit.What} → {(back == null ? "gone" : string.Join(", ",
            back.Fields.Select(f => $"{f.Label} {f.Text}")))}");
        check($"{what} comes back from the archive as it was typed", back != null && want(back), "");
    }

    /// <summary>The same, for one of a component's own-bone rows.</summary>
    private static void Row(
        StringBuilder sb, string mirror, Action<string, bool, string> check, string kind, string what,
        Func<IReadOnlyList<CarField>, IReadOnlyList<CarField>> change, Func<CarComponentRow, bool> want)
    {
        Car? car = Car.ReadFrom(mirror);
        (CarComponent? component, CarComponentRow? row) = PickRow(car, kind);
        if (car == null || row == null || component == null)
        {
            check($"the focus car has a {kind} row", false, "");
            return;
        }

        CarEdit? edit = car.SetRow(row, change(row.Fields), out string? refusal);
        if (edit == null) { check($"{what} can be written", false, refusal ?? ""); return; }
        CarSave saved = car.Save();
        if (!saved.Ok) { check($"{what} saves", false, string.Join("; ", saved.Lost)); return; }

        Car? read = Car.ReadFrom(mirror);
        (_, CarComponentRow? back) = PickRow(read, kind);
        sb.AppendLine($"  a {kind}'s {what} on \"{component.Name}\": {(back == null ? "gone"
            : string.Join(", ", back.Fields.Select(f => $"{f.Label} {f.Text}")))}");
        check($"a {kind} row's {what} comes back from the archive as it was typed",
            back != null && want(back), "");
    }

    // ── the two copies of where a marker is ──

    /// <summary>
    /// Typing a climb box's corners or a seat's position moves the MARKER, because the row and the frame are
    /// one thing the modder authored written down twice.
    /// </summary>
    private static void Placement(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ typing where a marker is moves the marker ════");

        Car? car = Car.ReadFrom(mirror);
        CarMarker? box = car?.Markers.FirstOrDefault(m => m.Role == CarMarkerRole.ClimbBox);
        if (car == null || box == null) { check("the focus car has a climb box", false, ""); return; }

        // What the MARKER said before anything was typed. Not what its row said: this car is one of the eight
        // whose rows already disagree with their Dummy, and an undo owes what was there rather than what the
        // row claimed was there.
        (Vector3 hadMin, Vector3 hadMax) = Dummy(car, box.Frame) is { } stood
            ? Car.BoxOf(stood)
            : (default, default);

        var min = new Vector3(-0.7f, 1.1f, 0.2f);
        var max = new Vector3(0.1f, 1.9f, 1.4f);
        CarEdit? edit = car.SetMarker(box,
            [.. box.Fields.Select(f => f.Label == "Lowest corner" ? f with { Point = min }
                : f.Label == "Highest corner" ? f with { Point = max } : f)],
            out string? refusal);
        if (edit == null) { check("a climb box's corners can be typed", false, refusal ?? ""); return; }

        FrameObjectDummy? dummy = Dummy(car, box.Frame);
        (Vector3 wasMin, Vector3 wasMax) = dummy == null ? (default, default) : Car.BoxOf(dummy);
        sb.AppendLine($"  {box.Label}: typed {min:F2}…{max:F2}, the marker now states "
            + $"{wasMin:F2}…{wasMax:F2}");
        check("a climb box typed on its row moves its marker to the same box",
            dummy != null && (wasMin - min).Length() < 1e-3f && (wasMax - max).Length() < 1e-3f, "");

        // …and an undo has to put the marker's own SIZE back, not only the row and the placement. The size
        // lives in the Dummy's bounds, which a snapshot recording the transform alone would leave enlarged —
        // a car the editor draws one way and the game climbs another.
        car.Restore(edit.Before);
        (Vector3 backMin, Vector3 backMax) = Car.BoxOf(Dummy(car, box.Frame)!);
        sb.AppendLine($"  …taken back: the marker states {backMin:F2}…{backMax:F2}, and it was "
            + $"{hadMin:F2}…{hadMax:F2}");
        check("undoing it puts the marker's own size and place back, not just the row",
            (backMin - hadMin).Length() < 1e-3f && (backMax - hadMax).Length() < 1e-3f, "");
        car.Restore(edit.After);

        // …and the row keeps what was typed, rather than being rewritten by the move it just caused.
        car.Save();
        Car? read = Car.ReadFrom(mirror);
        CarMarker? again = read?.Markers.FirstOrDefault(m => m.Role == CarMarkerRole.ClimbBox);
        Vector3 storedMin = again?.Fields.FirstOrDefault(f => f.Label == "Lowest corner")?.Point ?? default;
        check("…and the row still says what was typed",
            again != null && (storedMin - min).Length() < 1e-3f, $"{storedMin:F3}");

        Car? seatCar = Car.ReadFrom(mirror);
        CarMarker? seat = seatCar?.Markers.FirstOrDefault(m => m.Role == CarMarkerRole.Seat);
        if (seatCar == null || seat == null) { check("the focus car has a seat", false, ""); return; }

        var at = new Vector3(0.42f, -0.31f, 0.77f);
        CarEdit? seatEdit = seatCar.SetMarker(seat,
            [.. seat.Fields.Select(f =>
                f.Label == "Where the occupant sits" ? f with { Point = at } : f)],
            out refusal);
        if (seatEdit == null) { check("a seat's position can be typed", false, refusal ?? ""); return; }

        FrameObjectDummy? seatDummy = Dummy(seatCar, seat.Frame);
        Vector3 stands = seatDummy?.WorldTransform.Translation ?? default;
        sb.AppendLine($"  {seat.Label}: typed {at:F2}, the marker now stands at {stands:F2}");
        check("a seat position typed on its row moves its marker to that point",
            seatDummy != null && (stands - at).Length() < 1e-3f, "");
    }

    // ── the other direction: dragging the marker ──

    /// <summary>
    /// Dragging a marker in the viewport rewrites its stored row — and only for the markers that were dragged,
    /// because 18 of the 280 shipped climb-box rows already disagree with their Dummy and rewriting those
    /// would change cars nobody asked about.
    /// </summary>
    private static void Dragged(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ dragging a marker rewrites the row the game reads ════");

        Car? car = Car.ReadFrom(mirror);
        CarMarker? box = car?.Markers.FirstOrDefault(m => m.Role == CarMarkerRole.ClimbBox);
        CarMarker? seat = car?.Markers.FirstOrDefault(m => m.Role == CarMarkerRole.Seat);
        FrameObjectDummy? boxDummy = car == null || box == null ? null : Dummy(car, box.Frame);
        FrameObjectDummy? seatDummy = car == null || seat == null ? null : Dummy(car, seat.Frame);
        if (car?.Frames == null || boxDummy == null || seatDummy == null || box == null || seat == null)
        {
            check("the focus car has a climb box and a seat with frames", false, "");
            return;
        }

        // What a car NOBODY has touched gets. Asked of a pristine copy, because the mirror this section runs
        // on has been edited by the sections above and their frames really did move.
        Car? pristine = Car.ReadFrom(Original(mirror));
        if (pristine?.Frames?.FrameObjects == null) { check("a pristine copy reads", false, ""); return; }

        // 18 of the 280 shipped climb-box rows and 1 of the 213 seat positions already disagree with the
        // frame beside them, on cars somebody edited by hand long before this toolkit existed. THAT is why
        // the sync is offered only the frames that moved: handed the whole car it would rewrite those too,
        // and changing rows nobody touched is exactly what a save must never do.
        int disagreeing = Disagreeing(pristine);
        check("a car nobody touched offers the sync nothing, so it writes nothing",
            pristine.SyncMarkers([]) == 0, "");
        int untouched =
            pristine.SyncMarkers([.. pristine.Frames.FrameObjects.Values.OfType<FrameObjectBase>()]);
        sb.AppendLine($"  offered every frame of an untouched car, the sync rewrites {untouched} rows — the "
            + $"{disagreeing} whose row and frame already disagree, and no others");
        check("offered every frame it rewrites the rows that already disagree, and only those",
            untouched == disagreeing, $"{untouched} rewritten, {disagreeing} already disagreeing");

        boxDummy.LocalTransform =
            Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(0.3f, 1.2f, 0.8f);
        seatDummy.LocalTransform = Matrix4x4.CreateTranslation(-0.25f, 0.4f, 0.9f);
        int moved = car.SyncMarkers([boxDummy, seatDummy]);
        (Vector3 wantMin, Vector3 wantMax) = Car.BoxOf(boxDummy);
        Vector3 wantAt = seatDummy.WorldTransform.Translation;

        CarSave saved = car.Save();
        Car? read = Car.ReadFrom(mirror);
        CarMarker? boxBack = read?.Markers.FirstOrDefault(m => m.Role == CarMarkerRole.ClimbBox);
        CarMarker? seatBack = read?.Markers.FirstOrDefault(m => m.Role == CarMarkerRole.Seat);
        Vector3 gotMin = boxBack?.Fields.FirstOrDefault(f => f.Label == "Lowest corner")?.Point ?? default;
        Vector3 gotMax = boxBack?.Fields.FirstOrDefault(f => f.Label == "Highest corner")?.Point ?? default;
        Vector3 gotAt = seatBack?.Fields.FirstOrDefault(
            f => f.Label == "Where the occupant sits")?.Point ?? default;

        sb.AppendLine($"  {moved} rows rewritten; the climb box's row now {gotMin:F2}…{gotMax:F2}, "
            + $"the seat's position {gotAt:F2}");
        check("dragging and scaling a climb box carries through to the row the game climbs",
            saved.Ok && moved == 2 && (gotMin - wantMin).Length() < 1e-3f
            && (gotMax - wantMax).Length() < 1e-3f, $"{moved} rewritten");
        check("dragging a seat carries through to where its occupant sits",
            (gotAt - wantAt).Length() < 1e-3f, $"{gotAt:F3} vs {wantAt:F3}");
        check("…and running the same sync again writes nothing",
            car.SyncMarkers([boxDummy, seatDummy]) == 0, "");
    }

    // ── adding and removing one ──

    private static void AddRemove(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ adding a marker, and taking it away again ════");

        foreach (CarMarkerRole role in Car.AddableRoles)
        {
            Car? car = Car.ReadFrom(mirror);
            CarComponent? body = car?.Components.FirstOrDefault(
                c => string.Equals(c.Kind, "body", StringComparison.Ordinal) && c.BoneResolves);
            if (car == null || body == null) { check("the focus car has a body", false, ""); continue; }

            int had = car.Markers.Count(m => m.Role == role);
            int frames = car.Frames?.FrameObjects?.Count ?? 0;
            CarEdit? edit = car.AddMarker(body, role, out string? refusal);
            if (edit == null) { check($"{Car.Words(role)} can be added", false, refusal ?? ""); continue; }
            CarSave saved = car.Save();
            if (!saved.Ok)
            {
                check($"{Car.Words(role)} saves", false, string.Join("; ", saved.Lost));
                continue;
            }

            Car? read = Car.ReadFrom(mirror);
            CarMarker? added = read?.Markers.LastOrDefault(m => m.Role == role);
            bool onTheBody = added != null
                && read?.ComponentOfBone(added.Bone)?.Id == read?.Body?.Id;
            sb.AppendLine($"  {Car.Words(role)}: {had} → {read?.Markers.Count(m => m.Role == role)}, "
                + $"named \"{added?.Name}\", on the bone of \"{read?.ComponentOfBone(added?.Bone ?? 0)?.Name}\"");
            check($"{Car.Words(role)} is added with a frame of its own, on the component it was asked for",
                read != null && read.Markers.Count(m => m.Role == role) == had + 1
                && added is { Resolved: true } && onTheBody
                && (read.Frames?.FrameObjects?.Count ?? 0) == frames + 1,
                $"{read?.Frames?.FrameObjects?.Count} frames, was {frames}");

            // …and taking it away takes both halves: the row and the helper frame nothing else names.
            Car? removing = Car.ReadFrom(mirror);
            CarMarker? mine = removing?.Markers.LastOrDefault(m => m.Role == role);
            if (removing == null || mine == null) { check($"{Car.Words(role)} can be found again", false, ""); continue; }
            CarEdit? gone = removing.RemoveMarker(mine, out refusal);
            if (gone == null) { check($"{Car.Words(role)} can be removed", false, refusal ?? ""); continue; }
            removing.Save();

            Car? after = Car.ReadFrom(mirror);
            check($"removing {Car.Words(role)} takes its row and its frame with it",
                after != null && after.Markers.Count(m => m.Role == role) == had
                && (after.Frames?.FrameObjects?.Count ?? 0) == frames,
                $"{after?.Markers.Count(m => m.Role == role)} rows, "
                + $"{after?.Frames?.FrameObjects?.Count} frames");
        }

        // A climb box states its box in the row, and the row was copied from the last one there — so a new one
        // has to say what its OWN frame says or it sits on top of an existing box and cannot be climbed.
        Car? boxCar = Car.ReadFrom(mirror);
        CarComponent? bodyPart = boxCar?.Components.FirstOrDefault(
            c => string.Equals(c.Kind, "body", StringComparison.Ordinal) && c.BoneResolves);
        if (boxCar == null || bodyPart == null) return;
        CarMarker? donor = boxCar.Markers.LastOrDefault(m => m.Role == CarMarkerRole.ClimbBox);
        Vector3 donorMin = donor?.Fields.FirstOrDefault(f => f.Label == "Lowest corner")?.Point ?? default;
        if (boxCar.AddMarker(bodyPart, CarMarkerRole.ClimbBox, out _) == null) return;
        boxCar.Save();

        Car? mintedRead = Car.ReadFrom(mirror);
        CarMarker? minted = mintedRead?.Markers.LastOrDefault(m => m.Role == CarMarkerRole.ClimbBox);
        Vector3 mintedMin = minted?.Fields.FirstOrDefault(f => f.Label == "Lowest corner")?.Point ?? default;
        Vector3 mintedMax = minted?.Fields.FirstOrDefault(f => f.Label == "Highest corner")?.Point ?? default;
        sb.AppendLine($"  a minted climb box: donor's corner {donorMin:F2}, its own {mintedMin:F2}, "
            + $"{(mintedMax - mintedMin).Length():F2} m across");
        check("a minted climb box states ITS box, not a copy of the one it was cloned from",
            minted != null && (mintedMin - donorMin).Length() > 1e-3f
            && (mintedMax - mintedMin).Length() > 0.5f, "");

        // Put the car back, so the sections after this one see the archive they expect.
        Car? tidy = Car.ReadFrom(mirror);
        CarMarker? last = tidy?.Markers.LastOrDefault(m => m.Role == CarMarkerRole.ClimbBox);
        if (tidy != null && last != null && tidy.RemoveMarker(last, out _) != null) tidy.Save();
    }

    // ── one undo step ──

    private static void Undo(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ one intent is one undo step, and it restores a snapshot ════");

        Car? car = Car.ReadFrom(mirror);
        CarComponent? body = car?.Components.FirstOrDefault(
            c => string.Equals(c.Kind, "body", StringComparison.Ordinal) && c.BoneResolves);
        if (car?.PrefabPath == null || body == null) { check("the focus car reads", false, ""); return; }

        byte[] prefabWas = File.ReadAllBytes(car.PrefabPath);
        string? rig = Rig(mirror);
        byte[] rigWas = rig == null ? [] : File.ReadAllBytes(rig);
        int frames = car.Frames?.FrameObjects?.Count ?? 0;

        CarEdit? edit = car.AddMarker(body, CarMarkerRole.Seat, out string? refusal);
        if (edit == null) { check("a seat can be added to undo", false, refusal ?? ""); return; }
        car.Save();
        bool changed = !File.ReadAllBytes(car.PrefabPath).AsSpan().SequenceEqual(prefabWas);

        // Five times over, because both halves of this look right once and go wrong on the fourth keystroke:
        // an attachment list that grows by one on every restore, and a frame put back at the end of the graph
        // rather than at its own place.
        for (int i = 0; i < 5; i++)
        {
            car.Restore(edit.Before);
            car.Save();
            car.Restore(edit.After);
            car.Save();
        }
        car.Restore(edit.Before);
        CarSave saved = car.Save();

        byte[] rigNow = rig == null ? [] : File.ReadAllBytes(rig);
        Car? read = Car.ReadFrom(mirror);
        sb.AppendLine($"  add → save → (undo, redo) ×5 → undo: the prefab changed ({changed}) and came back "
            + $"({File.ReadAllBytes(car.PrefabPath).AsSpan().SequenceEqual(prefabWas)}); frames "
            + $"{frames} → {read?.Frames?.FrameObjects?.Count}");
        check("the edit reached the file at all", changed, "the prefab never changed");
        check("one undo puts the prefab back byte for byte",
            saved.Ok && File.ReadAllBytes(car.PrefabPath).AsSpan().SequenceEqual(prefabWas), "");
        check("and takes the frame it minted with it, however many times it is taken back and put again",
            (read?.Frames?.FrameObjects?.Count ?? -1) == frames,
            $"{read?.Frames?.FrameObjects?.Count} frames, was {frames}");
        check("and the frame graph the marker went into comes back byte for byte",
            rig != null && rigWas.AsSpan().SequenceEqual(rigNow),
            rig == null ? "no frame resource" : $"{rigWas.Length} vs {rigNow.Length} bytes");
    }

    // ── everything the modder did not touch is left as it was ──

    private static void Scoped(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ what a marker edit is allowed to disturb ════");

        Car? car = Car.ReadFrom(mirror);
        CarMarker? seat = car?.Markers.FirstOrDefault(m => m.Role == CarMarkerRole.Seat);
        if (car?.PrefabPath == null || seat == null) { check("the focus car has a seat", false, ""); return; }

        PrefabFile before = PrefabFile.Load(car.PrefabPath);
        CarEdit? edit = car.SetMarker(seat,
            [.. seat.Fields.Select(f => f.Label == "Seat type" ? f with { Number = 4f } : f)],
            out string? refusal);
        if (edit == null) { check("a seat's type can be written", false, refusal ?? ""); return; }
        car.Save();

        Car? read = Car.ReadFrom(mirror);
        IReadOnlyList<string> moved = read == null ? [] : before.Diff(read.Prefab);
        sb.AppendLine($"  a seat's type changed: the prefab's fields that moved — "
            + $"{(moved.Count == 0 ? "none" : string.Join("; ", moved.Take(4)))}");
        check("the edit shows up in the prefab as the row it was made on and nothing else",
            moved.Count == 1 && moved[0].Contains("Seats", StringComparison.Ordinal)
            && moved[0].Contains("SeatType", StringComparison.Ordinal),
            moved.Count == 0 ? "nothing moved at all" : string.Join("; ", moved.Take(4)));

        // …and it must not have moved the seat's MARKER either. A seat's row says where its occupant sits and
        // its Dummy stands there too, so a naive write would place the frame on every edit — and recomposing
        // a frame's matrix from its own decomposition is not bit-exact, so changing a seat's TYPE would
        // rewrite the frame resource with a drift nobody asked for.
        string? rig = Rig(mirror);
        byte[] rigWas = rig == null ? [] : File.ReadAllBytes(rig);
        Car? again = Car.ReadFrom(mirror);
        CarMarker? unchanged = again?.Markers.FirstOrDefault(m => m.Role == CarMarkerRole.Seat);
        if (again == null || unchanged == null || rig == null) return;

        CarEdit? second = again.SetMarker(unchanged,
            [.. unchanged.Fields.Select(f => f.Label == "Seat group" ? f with { Number = 1f } : f)],
            out refusal);
        if (second == null) { check("a seat's group can be written", false, refusal ?? ""); return; }
        again.Save();

        sb.AppendLine($"  the frame resource after an edit that says nothing about where the seat is: "
            + $"{rigWas.Length} bytes → {new FileInfo(rig).Length}");
        check("an edit that says nothing about where a marker is leaves its frame alone",
            rigWas.AsSpan().SequenceEqual(File.ReadAllBytes(rig)), "the frame resource was rewritten");
    }

    // ── the refusals ──

    private static void Refusals(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ what cannot be done, and whether it says why ════");

        Car? car = Car.ReadFrom(mirror);
        CarComponent? body = car?.Components.FirstOrDefault(
            c => string.Equals(c.Kind, "body", StringComparison.Ordinal) && c.BoneResolves);
        if (car?.PrefabPath == null || body == null) { check("the focus car reads", false, ""); return; }

        byte[] was = File.ReadAllBytes(car.PrefabPath);
        foreach (CarMarkerRole role in new[] { CarMarkerRole.Wiper, CarMarkerRole.Light })
        {
            CarEdit? edit = car.AddMarker(body, role, out string? refusal);
            sb.AppendLine($"  adding {Car.Words(role)}: {refusal ?? "(allowed)"}");
            check($"adding {Car.Words(role)} refuses with the reason",
                edit == null && refusal is { Length: > 20 }, refusal ?? "it was allowed");
        }

        CarComponent? bare = car.Components.FirstOrDefault(c => c.IsBare && !c.BoneResolves)
            ?? car.Components.FirstOrDefault(c => !c.BoneResolves);
        if (bare != null)
        {
            CarEdit? edit = car.AddMarker(bare, CarMarkerRole.Seat, out string? refusal);
            sb.AppendLine($"  adding a seat to \"{bare.Name}\", whose bone is gone: {refusal}");
            check("adding a marker to a component whose bone is gone refuses with the reason",
                edit == null && refusal is { Length: > 20 }, refusal ?? "it was allowed");
        }

        // A number that is not one. Every field of the set is refused together, so the row is never left
        // holding half of what was typed.
        CarMarker? seat = car.Markers.FirstOrDefault(m => m.Role == CarMarkerRole.Seat);
        if (seat != null)
        {
            CarEdit? edit = car.SetMarker(seat,
                [.. seat.Fields.Select(f => f.Label == "Seat type" ? f with { Number = float.NaN } : f)],
                out string? refusal);
            sb.AppendLine($"  a seat type that is not a number: {refusal}");
            check("a value that is not a number refuses with the reason",
                edit == null && refusal is { Length: > 5 }, refusal ?? "it was allowed");
        }

        // A seat's number is unique within its car on all 213 shipped seats, and the add and remove paths
        // keep it that way — so the field that exposes it may not be the way that invariant is broken.
        CarMarker? second = car.Markers.Where(m => m.Role == CarMarkerRole.Seat).Skip(1).FirstOrDefault();
        if (seat != null && second != null)
        {
            float mine = seat.Fields.First(f => f.Label == "Seat number").Number;
            CarEdit? edit = car.SetMarker(second,
                [.. second.Fields.Select(f => f.Label == "Seat number" ? f with { Number = mine } : f)],
                out string? refusal);
            sb.AppendLine($"  a seat given the number another seat has: {refusal}");
            check("a seat number another seat of the car already has refuses with the reason",
                edit == null && refusal is { Length: > 20 }, refusal ?? "it was allowed");
        }

        check("and not one of those refusals wrote anything",
            File.ReadAllBytes(car.PrefabPath).AsSpan().SequenceEqual(was), "");
    }

    // ── plumbing ──

    /// <summary>How many of a car's rows already say something other than the frame beside them — measured
    /// straight off the prefab and the graph, so that what the sync writes can be compared with what a
    /// disagreement census says it ought to.</summary>
    private static int Disagreeing(Car car)
    {
        if (car.Prefab.Car is not { } assembly) return 0;
        int found = 0;
        IReadOnlyList<CarPrefab.ClimbBox> boxes = assembly.ClimbBoxes;
        for (int i = 0; i < boxes.Count; i++)
        {
            if (Dummy(car, boxes[i].Dummy) is not { } dummy) continue;
            (Vector3 min, Vector3 max) = Car.BoxOf(dummy);
            if ((min - boxes[i].Min).Length() >= 1e-4f || (max - boxes[i].Max).Length() >= 1e-4f) found++;
        }
        IReadOnlyList<CarPrefab.Seat> seats = assembly.Seats;
        for (int i = 0; i < seats.Count; i++)
        {
            if (Frame(car, seats[i].Frame) is not { } frame) continue;
            if ((frame.WorldTransform.Translation - seats[i].Position).Length() >= 1e-4f) found++;
        }
        return found;
    }

    /// <summary>A second mirror, made fresh from the game's own working copy — for the questions that are
    /// about a car NOBODY has touched, which the edited mirror can no longer answer.</summary>
    private static string Original(string mirror)
    {
        string source = MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(
            MafiaEnvironment.PcFolder, "sds", "cars", Path.GetFileName(mirror) + ".sds")));
        string pristine = mirror + "_pristine";
        if (Directory.Exists(pristine)) return pristine;

        Directory.CreateDirectory(pristine);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(pristine, Path.GetFileName(file)), overwrite: true);
        }
        return pristine;
    }

    /// <summary>The component and the first row of the given kind it carries.</summary>
    private static (CarComponent? Component, CarComponentRow? Row) PickRow(Car? car, string kind)
    {
        foreach (CarComponent component in car?.Components ?? [])
        {
            foreach (CarComponentRow row in component.Rows)
            {
                if (string.Equals(row.Kind, kind, StringComparison.Ordinal)) return (component, row);
            }
        }
        return (null, null);
    }

    private static FrameObjectDummy? Dummy(Car car, ulong hash) => Frame(car, hash) as FrameObjectDummy;

    private static FrameObjectBase? Frame(Car car, ulong hash)
    {
        foreach (object o in car.Frames?.FrameObjects?.Values ?? Enumerable.Empty<object>())
        {
            if (o is FrameObjectBase frame && frame.Name?.String is { Length: > 0 } name
                && Formats.Hashing.Fnv64.Hash(name) == hash)
            {
                return frame;
            }
        }
        return null;
    }

    /// <summary>The archive's frame resource file, or null when it carries none.</summary>
    private static string? Rig(string extracted)
    {
        try { return Formats.Archive.SdsManifest.Load(extracted).GetFiles("FrameResource").FirstOrDefault(); }
        catch (Exception ex) when (ex is IOException or Formats.SdsFormatException) { return null; }
    }
}
