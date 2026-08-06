using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gizmos;
using Illusion.Rendering.Scene;
using Illusion.ViewModels;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Viewport navigation probes: the mouse-only camera (orbit / pan / zoom / frame-selected) and the lifecycle of a
/// modal transform. Headless — the camera math is pure, and the gizmo overlay is driven through a fake host.
/// </summary>
internal static class NavigationProbes
{
    /// <summary>Camera navigation + modal transform contract. Output: %TEMP%\illusion_navigation.txt</summary>
    internal static void RunNavigationProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_navigation.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            CheckOrbit(Check);
            CheckPan(Check);
            CheckDolly(Check);
            CheckWalkSpeed(Check);
            CheckFraming(Check);
            CheckParallelView(Check);
            CheckParallelPicking(Check);
            CheckPrecision(Check);
            CheckModalLifecycle(Check);
            CheckModalDrawsUnderAnyTool(Check);
            sb.Insert(0, $"NAVIGATION PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "NAVIGATION PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static Camera NewCamera()
    {
        var cam = new Camera { AspectRatio = 16f / 9f };
        cam.LookAt(new Vector3(0f, -60f, 25f), Vector3.Zero);
        return cam;
    }

    // Orbiting is the whole point of the default camera: what you centred stays centred while you walk around it.
    private static void CheckOrbit(Action<string, bool, string> check)
    {
        Camera cam = NewCamera();
        const float distance = 60f;
        Vector3 pivotBefore = CameraNavigator.PivotOf(cam, distance);
        Vector3 posBefore = cam.Position;

        CameraNavigator.Orbit(cam, distance, 0.4f, 0.2f);
        Vector3 pivotAfter = CameraNavigator.PivotOf(cam, distance);

        check("orbit leaves the pivot exactly where it was",
            (pivotAfter - pivotBefore).Length() < 1e-2f, $"{pivotBefore} → {pivotAfter}");
        check("orbit actually swings the camera", (cam.Position - posBefore).Length() > 1f, "");
        check("orbit keeps the standoff", MathF.Abs((cam.Position - pivotAfter).Length() - distance) < 1e-2f,
            $"{(cam.Position - pivotAfter).Length():F3}");

        // Straight up is the one direction that used to blow the basis apart, so it is the one to stop at
        // exactly rather than short of: a top view a degree off vertical is a top view you cannot align in.
        for (int i = 0; i < 40; i++) CameraNavigator.Orbit(cam, distance, 0f, 0.2f);
        check("orbit cannot be tipped past vertical", MathF.Abs(cam.Pitch) <= Camera.MaxPitch + 1e-4f,
            $"pitch={cam.Pitch:F3}");
        check("a tipped-over orbit is still a valid basis", !float.IsNaN(cam.Right.X), cam.Right.ToString());
        check("straight up is a direction the camera actually reaches",
            MathF.Abs(MathF.Abs(cam.Forward.Z) - 1f) < 1e-5f, $"forward={cam.Forward}");
        check("the basis stays orthonormal at the pole",
            MathF.Abs(cam.Right.Length() - 1f) < 1e-5f
                && MathF.Abs(Vector3.Dot(cam.Right, cam.Forward)) < 1e-5f
                && !float.IsNaN(cam.View.M11), $"right={cam.Right}");

        // Everywhere the library look-at IS defined, the hand-built view matrix has to put the world in the
        // same place — this is what says the fix at the pole changed nothing anywhere else. Measured in metres
        // of view-space displacement rather than in matrix entries, which is the error that would be visible.
        Camera plain = NewCameraAt();
        plain.AddLook(0.7f, -0.4f);
        Matrix4x4 byLookAt = Matrix4x4.CreateLookAt(plain.Position, plain.Position + plain.Forward,
            new Vector3(0f, 0f, 1f));
        float worst = 0f;
        foreach (Vector3 p in new[] { Vector3.Zero, new Vector3(120f, -40f, 15f), new Vector3(-8f, 60f, -3f) })
        {
            worst = MathF.Max(worst,
                (Vector3.Transform(p, plain.View) - Vector3.Transform(p, byLookAt)).Length());
        }
        check("the view matrix still puts the world where the library look-at does away from the pole",
            worst < 1e-3f, $"worst {worst:E2} m");
    }

    // Panning slides the view sideways without turning it, and has to keep pace with the scene at any zoom.
    private static void CheckPan(Action<string, bool, string> check)
    {
        Camera cam = NewCamera();
        const float distance = 60f;
        const double height = 600;
        Vector3 forward = cam.Forward, right = cam.Right, from = cam.Position;
        float yaw = cam.Yaw, pitch = cam.Pitch;

        CameraNavigator.Pan(cam, distance, 100f, 0f, height);
        Vector3 moved = cam.Position - from;

        check("pan does not turn the camera", cam.Yaw == yaw && cam.Pitch == pitch, "");
        check("pan stays in the view plane", MathF.Abs(Vector3.Dot(moved, forward)) < 1e-3f,
            $"along view={Vector3.Dot(moved, forward):F5}");
        check("dragging right carries the scene right (the camera goes left)",
            Vector3.Dot(moved, right) < 0f, $"{Vector3.Dot(moved, right):F3}");

        Camera up = NewCamera();
        CameraNavigator.Pan(up, distance, 0f, 100f, height);
        check("dragging down carries the scene down (the camera goes up)",
            Vector3.Dot(up.Position - from, CameraNavigator.UpOf(up)) > 0f, "");

        // Twice as far away, the same drag has to cover twice the ground, or panning feels glued at one zoom
        // and uncontrollable at another.
        Camera far = NewCamera();
        CameraNavigator.Pan(far, distance * 2f, 100f, 0f, height);
        float ratio = (far.Position - from).Length() / MathF.Max(moved.Length(), 1e-6f);
        check("pan speed follows the zoom level", MathF.Abs(ratio - 2f) < 1e-2f, $"×{ratio:F3}");
    }

    // Zooming approaches the pivot geometrically, so it slows as it closes in and never lands on top of it.
    private static void CheckDolly(Action<string, bool, string> check)
    {
        Camera cam = NewCamera();
        float distance = 60f;
        Vector3 pivot = CameraNavigator.PivotOf(cam, distance);

        float closer = CameraNavigator.Dolly(cam, distance, 1f);
        check("one notch in moves closer", closer < distance, $"{distance:F2} → {closer:F2}");
        check("zooming leaves the pivot alone",
            (CameraNavigator.PivotOf(cam, closer) - pivot).Length() < 1e-2f, "");

        float back = CameraNavigator.Dolly(cam, closer, -1f);
        check("one notch out returns to where it started", MathF.Abs(back - distance) < 1e-2f,
            $"{back:F3} vs {distance:F3}");
        check("zooming out leaves the pivot alone too",
            (CameraNavigator.PivotOf(cam, back) - pivot).Length() < 1e-2f, "");

        // Spinning the wheel forever must stop at the pivot rather than fly through it and invert the view.
        float d = back;
        for (int i = 0; i < 200; i++) d = CameraNavigator.Dolly(cam, d, 1f);
        check("zoom stops short of the pivot instead of passing through it",
            d >= CameraNavigator.MinPivotDistance - 1e-4f && Vector3.Dot(pivot - cam.Position, cam.Forward) > 0f,
            $"distance={d:F3}");
    }

    // Walk-mode speed modifiers. Shift and Ctrl are exact opposites on purpose, so holding both is a no-op
    // rather than some third arbitrary speed.
    private static void CheckWalkSpeed(Action<string, bool, string> check)
    {
        float plain = CameraNavigator.SpeedMultiplier(boost: false, crawl: false);
        float fast = CameraNavigator.SpeedMultiplier(boost: true, crawl: false);
        float slow = CameraNavigator.SpeedMultiplier(boost: false, crawl: true);
        float both = CameraNavigator.SpeedMultiplier(boost: true, crawl: true);

        check("no modifier flies at the speed in the status bar", plain == 1f, plain.ToString());
        check("Shift covers ground", MathF.Abs(fast - CameraNavigator.SpeedStep) < 1e-6f, fast.ToString());
        check("Ctrl creeps by exactly the same factor", MathF.Abs(slow * CameraNavigator.SpeedStep - 1f) < 1e-6f,
            slow.ToString());
        check("holding both cancels out instead of inventing a third speed",
            MathF.Abs(both - 1f) < 1e-6f, both.ToString());
    }

    // "Look at this" has to actually put the thing on screen — from wherever the camera happens to be looking.
    private static void CheckFraming(Action<string, bool, string> check)
    {
        var center = new Vector3(120f, -40f, 15f);
        const float radius = 8f;

        Camera cam = NewCamera();
        Vector3 forwardBefore = cam.Forward;
        (Vector3 eye, float distance) = CameraNavigator.FrameOn(cam, center, radius);
        cam.Position = eye;

        check("framing keeps the direction you were looking from",
            (cam.Forward - forwardBefore).Length() < 1e-4f, "");
        check("framing stands the reported distance away",
            MathF.Abs((center - eye).Length() - distance) < 1e-2f, $"{(center - eye).Length():F3} vs {distance:F3}");

        // The real test is the projection: the object's silhouette has to land inside the viewport, on a wide
        // window as much as a tall one.
        Vector3 right = cam.Right, up = CameraNavigator.UpOf(cam);
        bool allIn = true;
        foreach (Vector3 edge in new[] { center + right * radius, center - right * radius,
                                         center + up * radius, center - up * radius })
        {
            allIn &= InView(cam, edge);
        }
        check("the framed object lands inside the viewport", allIn, "");

        var tall = new Camera { AspectRatio = 0.6f };
        tall.LookAt(new Vector3(0f, -60f, 25f), Vector3.Zero);
        (Vector3 tallEye, _) = CameraNavigator.FrameOn(tall, center, radius);
        tall.Position = tallEye;
        check("a narrow window frames it just as fully",
            InView(tall, center + tall.Right * radius) && InView(tall, center - tall.Right * radius), "");

        // A frame with no mesh has no size at all — it still deserves a sane standoff rather than a camera
        // parked inside it.
        (_, float pointDistance) = CameraNavigator.FrameOn(NewCamera(), center, 0f);
        check("a sizeless target still gets a standoff", pointDistance >= CameraNavigator.MinPivotDistance,
            $"{pointDistance:F3}");
    }

    // The parallel projection an axis snap leaves the view in. Two things make it worth having and both are
    // asserted here: it draws everything at one scale whatever its depth, and switching in or out of it does
    // not move the picture.
    private static void CheckParallelView(Action<string, bool, string> check)
    {
        var near = new Vector3(0f, -10f, 0f);   // two points the camera looks straight at, 40 m apart in depth
        var far = new Vector3(0f, 30f, 0f);

        Camera cam = NewCameraAt();
        const float distance = 70f;                        // the pivot sits at the origin

        // A corner of whatever is being looked at, out at the pivot's own depth: the one place both projections
        // have to agree, and the reason the swap is invisible.
        var pivotEdge = new Vector3(12f, 0f, 9f);
        Vector2 edgeBefore = Ndc(cam, pivotEdge);
        CameraNavigator.EnterParallel(cam, distance);

        check("the parallel view is the one the projection reports", cam.Orthographic, "");
        check("switching to parallel leaves the pivot's own depth drawn exactly as it was",
            (Ndc(cam, pivotEdge) - edgeBefore).Length() < 1e-4f,
            $"{edgeBefore} → {Ndc(cam, pivotEdge)}");

        // The whole point: depth stops mattering. The same offset from the view axis has to project to the same
        // place on screen whether it is ten metres away or fifty.
        float offset = 4f;
        Vector2 nearOff = Ndc(cam, near + Vector3.UnitZ * offset);
        Vector2 farOff = Ndc(cam, far + Vector3.UnitZ * offset);
        check("a parallel view draws near and far at the same scale",
            MathF.Abs((nearOff.Y - Ndc(cam, near).Y) - (farOff.Y - Ndc(cam, far).Y)) < 1e-4f,
            $"near={nearOff.Y - Ndc(cam, near).Y:F5} far={farOff.Y - Ndc(cam, far).Y:F5}");

        // ...and the perspective one it came from has to disagree, or the check above proves nothing.
        Camera persp = NewCameraAt();
        Vector2 pNearOff = Ndc(persp, near + Vector3.UnitZ * offset);
        Vector2 pFarOff = Ndc(persp, far + Vector3.UnitZ * offset);
        check("a perspective view does not",
            MathF.Abs((pNearOff.Y - Ndc(persp, near).Y) - (pFarOff.Y - Ndc(persp, far).Y)) > 1e-2f, "");

        // Zoom is the extent, not the standoff: rolling the wheel changes the size, walking the camera does not.
        float sizeBefore = Ndc(cam, near + Vector3.UnitZ * offset).Y - Ndc(cam, near).Y;
        cam.Position += cam.Forward * 20f;
        check("moving a parallel camera forward changes nothing on screen",
            MathF.Abs((Ndc(cam, near + Vector3.UnitZ * offset).Y - Ndc(cam, near).Y) - sizeBefore) < 1e-5f, "");

        cam.Position -= cam.Forward * 20f;
        float zoomed = CameraNavigator.DollyParallel(cam, distance, 4f);
        check("the wheel does zoom a parallel view",
            Ndc(cam, near + Vector3.UnitZ * offset).Y - Ndc(cam, near).Y > sizeBefore * 1.5f, "");
        check("zooming in never pushes the camera into what it is looking at", zoomed <= distance + 1e-3f,
            $"{distance:F2} → {zoomed:F2}");

        // Zooming out has to walk the camera back with it, or the near plane starts eating the scene.
        float outward = CameraNavigator.DollyParallel(cam, zoomed, -40f);
        check("zooming out backs the camera off to keep the extent in front of it",
            outward >= CameraNavigator.DistanceForOrthoHeight(cam, cam.OrthoHeight) - 1e-2f,
            $"distance={outward:F2} extent={cam.OrthoHeight:F2}");

        // Panning is measured in what is on screen, which for a parallel view is its extent and nothing else.
        Camera panA = NewCameraAt();
        CameraNavigator.EnterParallel(panA, distance);
        Camera panB = NewCameraAt();
        CameraNavigator.EnterParallel(panB, distance);
        CameraNavigator.Pan(panA, distance, 100f, 0f, 600);
        CameraNavigator.Pan(panB, distance * 3f, 100f, 0f, 600);   // a standoff it must now ignore
        check("a parallel pan follows the extent, not the standoff",
            (panA.Position - panB.Position).Length() < 1e-3f, "");

        // Coming back out is the same swap in reverse and has to be just as invisible.
        Camera round = NewCameraAt();
        Vector2 cornerBefore = Ndc(round, new Vector3(12f, 0f, 9f));
        CameraNavigator.EnterParallel(round, distance);
        round.OrthoHeight *= 0.4f;                                  // zoom in, so the standoff has to follow
        float back = CameraNavigator.LeaveParallel(round, distance);
        check("leaving the parallel view puts the perspective back", !round.Orthographic, "");
        check("leaving it keeps the framing it was zoomed to",
            (Ndc(round, new Vector3(12f, 0f, 9f)) - cornerBefore * 2.5f).Length() < 0.05f,
            $"{cornerBefore * 2.5f} vs {Ndc(round, new Vector3(12f, 0f, 9f))}");
        check("leaving it hands back the standoff that spans the same height",
            MathF.Abs(back - CameraNavigator.DistanceForOrthoHeight(round, round.OrthoHeight)) < 1e-3f,
            $"{back:F3}");
        check("the extent and the standoff are exact inverses",
            MathF.Abs(CameraNavigator.DistanceForOrthoHeight(round,
                CameraNavigator.OrthoHeightFor(round, 37f)) - 37f) < 1e-3f, "");
    }

    // Under a parallel projection the rays no longer meet at the camera: the direction is shared and it is the
    // ORIGIN that differs per pixel. A ray built the other way round picks the same line for the whole screen.
    private static void CheckParallelPicking(Action<string, bool, string> check)
    {
        Camera cam = NewCameraAt();
        CameraNavigator.EnterParallel(cam, 70f);
        Matrix4x4 vp = cam.ViewProjection;
        const double w = 800, h = 600;

        (Vector3 Origin, Vector3 Dir) left = Picking.BuildRay(vp, cam.Position, w * 0.25, h / 2.0, w, h);
        (Vector3 Origin, Vector3 Dir) right = Picking.BuildRay(vp, cam.Position, w * 0.75, h / 2.0, w, h);

        check("a parallel view gives every pixel its own ray",
            (left.Origin - right.Origin).Length() > 1f, $"{(left.Origin - right.Origin).Length():F3} m apart");
        check("a parallel view gives them all one direction",
            (left.Dir - right.Dir).Length() < 1e-5f, "");
        check("the rays run down the view axis", Vector3.Dot(left.Dir, cam.Forward) > 0.999f, "");

        // The failure a camera-anchored origin would produce, stated as the property that catches it: a click on
        // the left half of the screen must hit what is drawn on the left half and miss what is drawn on the right.
        Vector3 leftHit = left.Origin + left.Dir * 70f;
        Vector3 rightHit = right.Origin + right.Dir * 70f;
        check("a ray lands where its own pixel is aimed",
            Vector3.Dot(leftHit - rightHit, cam.Right) < -1f,
            $"{Vector3.Dot(leftHit - rightHit, cam.Right):F2}");

        var box = new Vector3(1.5f);
        check("a click on the left half hits the object drawn there",
            Picking.IntersectAabb(left.Origin, left.Dir, leftHit - box, leftHit + box, out _), "");
        check("and misses the one drawn on the other side",
            !Picking.IntersectAabb(left.Origin, left.Dir, rightHit - box, rightHit + box, out _), "");

        // The glyph click allowance turns from an angle into a length for the same reason.
        check("the glyph allowance stops opening up with distance under a parallel view",
            ActorPicking.ParallelSlack(cam) > 0f, ActorPicking.ParallelSlack(cam).ToString("F3"));
        check("and is still an angle under a perspective one",
            ActorPicking.ParallelSlack(NewCameraAt()) < 0f, "");
    }

    // The probe's standard camera, placed 70 m out on -Y looking at the origin — the view every parallel check
    // is measured against.
    private static Camera NewCameraAt()
    {
        var cam = new Camera { AspectRatio = 16f / 9f };
        cam.LookAt(new Vector3(0f, -70f, 0f), Vector3.Zero);
        return cam;
    }

    private static Vector2 Ndc(Camera cam, Vector3 world)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), cam.ViewProjection);
        return MathF.Abs(clip.W) < 1e-6f ? Vector2.Zero : new Vector2(clip.X / clip.W, clip.Y / clip.W);
    }

    private static bool InView(Camera cam, Vector3 world)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), cam.ViewProjection);
        if (clip.W <= 1e-4f) return false;
        float x = clip.X / clip.W, y = clip.Y / clip.W;
        return x is >= -1f and <= 1f && y is >= -1f and <= 1f;
    }

    // Held Ctrl slows the transform down for fine work. The whole difficulty is continuity — pressing it or
    // letting go must never make the object jump, which is exactly what naively scaling the result would do.
    private static void CheckPrecision(Action<string, bool, string> check)
    {
        var p = new PrecisionPointer();

        check("with Ctrl up the pointer is passed through untouched",
            p.Solve(new Point(100, 100), precise: false) == new Point(100, 100), "");

        // Engage: the very first slowed reading has to equal where the pointer already was.
        Point engaged = p.Solve(new Point(100, 100), precise: true);
        check("pressing Ctrl does not move anything on its own", engaged == new Point(100, 100), engaged.ToString());

        // ...and from there movement counts for a tenth.
        Point slow = p.Solve(new Point(200, 100), precise: true);
        check("Ctrl follows a tenth of the movement", Near(slow.X, 110) && Near(slow.Y, 100), slow.ToString());

        // Release: no jump either — it carries on from where the slowed run left it.
        Point released = p.Solve(new Point(200, 100), precise: false);
        check("releasing Ctrl does not move anything on its own", released == slow, $"{slow} → {released}");

        // ...and then movement counts in full again, from that same place.
        Point after = p.Solve(new Point(250, 100), precise: false);
        check("after Ctrl the pointer moves one-to-one again", Near(after.X, 160), after.ToString());

        // Re-engaging picks up where it is now, not where the first run started.
        Point again = p.Solve(new Point(250, 100), precise: true);
        check("Ctrl can be pressed again without a jump", again == after, $"{after} → {again}");
        Point crawl = p.Solve(new Point(350, 100), precise: true);
        check("the second Ctrl run is slowed too", Near(crawl.X, 170), crawl.ToString());

        // A fresh drag starts clean, or the leftovers of the last one would offset it.
        p.Reset();
        check("a reset drops the accumulated offset",
            p.Solve(new Point(250, 100), precise: false) == new Point(250, 100), "");
    }

    // What a modal transform promises: it starts, it owns the keyboard while it runs, and it ends in exactly one
    // of two ways — kept or put back. Nothing else may reach the app in between.
    // What the viewport overlay reports after a transform: the CHANGE, measured from where the object stood
    // before the drag. The invariant that makes the field editable is that the two directions are inverses —
    // retyping the number already on screen has to leave the object exactly where it is.
    private static void CheckOverlayDelta(Action<string, bool, string> check)
    {
        foreach ((GizmoMode mode, float baseline, float current, float expected) in new[]
                 {
                     (GizmoMode.Move, 10f, 22.5f, 12.5f),        // moved by +12.5 units
                     (GizmoMode.Move, 10f, 7f, -3f),             // and backwards
                     (GizmoMode.Rotate, 90f, 105f, 15f),         // turned by 15 degrees
                     (GizmoMode.Scale, 2f, 2.5f, 1.25f),         // a FACTOR: half again as big
                     (GizmoMode.Scale, 0.1f, 1f, 10f),           // which a difference could not express
                 })
        {
            float measured = TransformDelta.Measure(mode, baseline, current);
            check($"{mode} from {baseline} to {current} reads as {expected}",
                MathF.Abs(measured - expected) < 1e-4f, measured.ToString(CultureInfo.InvariantCulture));
            check($"{mode}: typing {expected} back lands on {current} again",
                MathF.Abs(TransformDelta.Apply(mode, baseline, measured) - current) < 1e-4f, "");
        }

        // A zero starting scale has no factor that reaches anything else; reporting infinity (or NaN) would
        // put an unusable number in an editable field.
        float fromZero = TransformDelta.Measure(GizmoMode.Scale, 0f, 5f);
        check("a zero starting scale reports 'unchanged' rather than infinity",
            fromZero == TransformDelta.Neutral(GizmoMode.Scale) && float.IsFinite(fromZero),
            fromZero.ToString(CultureInfo.InvariantCulture));

        check("an unchanged object reports the neutral amount for its transform",
            TransformDelta.Measure(GizmoMode.Move, 4f, 4f) == TransformDelta.Neutral(GizmoMode.Move)
            && TransformDelta.Measure(GizmoMode.Scale, 4f, 4f) == TransformDelta.Neutral(GizmoMode.Scale), "");
    }

    private static void CheckModalLifecycle(Action<string, bool, string> check)
    {
        (TransformGizmo gizmo, FakeTransformGizmoHost host) = NewOverlay();

        host.CanTransformSelection = false;
        check("nothing selected → no modal transform starts",
            !gizmo.BeginModal(GizmoMode.Move, new Point(200, 150)) && host.Calls.Count == 0, "");

        host.CanTransformSelection = true;
        // The pointer is the transform's origin, so one resting on the tree or the property panel is not a
        // starting point — it would fling the object the moment it entered the viewport.
        check("a pointer outside the viewport does not start one",
            !gizmo.BeginModal(GizmoMode.Move, new Point(OverlayW + 40, 150)) && host.Calls.Count == 0, "");

        check("G starts a move", gizmo.BeginModal(GizmoMode.Move, new Point(200, 150)) && gizmo.IsModalActive, "");
        check("the host was told a drag began", string.Join(",", host.Calls) == "begin", string.Join(",", host.Calls));

        // Confirm.
        gizmo.EndModal(commit: true);
        check("confirming records the edit and ends the modal",
            !gizmo.IsModalActive && string.Join(",", host.Calls) == "begin,end", string.Join(",", host.Calls));

        // Cancel.
        host.Calls.Clear();
        gizmo.BeginModal(GizmoMode.Scale, new Point(200, 150));
        gizmo.EndModal(commit: false);
        check("cancelling puts it back instead of recording",
            !gizmo.IsModalActive && string.Join(",", host.Calls) == "begin,cancel", string.Join(",", host.Calls));

        // What the host is TOLD the drag is. The bug this pins: it used to be handed the tool shelf's mode, and
        // a keyboard-started transform never touches the shelf — so a scale under a shelf set to Move (or to
        // Select, with no tool at all) reported itself as a move, and the viewport overlay labelled a resize
        // "Position". The shelf is deliberately left on something else in each case below.
        foreach ((GizmoMode started, GizmoMode shelf) in new[]
                 {
                     (GizmoMode.Scale, GizmoMode.Move),
                     (GizmoMode.Rotate, GizmoMode.None),
                     (GizmoMode.Move, GizmoMode.Scale),
                 })
        {
            host.GizmoMode = shelf;
            gizmo.BeginModal(started, new Point(200, 150));
            check($"a modal {started} is reported as {started}, not as the shelf's {shelf}",
                host.LastDragMode == started, host.LastDragMode.ToString());
            gizmo.EndModal(commit: false);
        }
        host.GizmoMode = GizmoMode.Move;
        CheckOverlayDelta(check);

        // Keys: Esc abandons, Enter and Space accept. Space matters — it is the walk-mode hotkey everywhere
        // else, and a running transform has to win it.
        foreach ((Key key, string expected) in new[]
                 { (Key.Escape, "begin,cancel"), (Key.Enter, "begin,end"), (Key.Space, "begin,end") })
        {
            host.Calls.Clear();
            gizmo.BeginModal(GizmoMode.Move, new Point(200, 150));
            bool taken = gizmo.HandleModalKey(key, ModifierKeys.None);
            check($"{key} ends the modal ({expected.Split(',')[1]})",
                taken && !gizmo.IsModalActive && string.Join(",", host.Calls) == expected,
                string.Join(",", host.Calls));
        }

        // Switching tool mid-transform restarts from the ORIGINAL state, so G-then-R rotates the object rather
        // than rotating a half-finished move.
        host.Calls.Clear();
        gizmo.BeginModal(GizmoMode.Move, new Point(200, 150));
        check("R during a move restarts as a rotate",
            gizmo.HandleModalKey(Key.R, ModifierKeys.None) && gizmo.IsModalActive
            && string.Join(",", host.Calls) == "begin,cancel,begin", string.Join(",", host.Calls));

        // The axis lock composes with the modal, and modified shortcuts still belong to the app (Ctrl+S saves).
        check("X inside a modal reaches the axis lock", gizmo.HandleModalKey(Key.X, ModifierKeys.None), "");
        check("Ctrl+S is not swallowed by a running modal",
            !gizmo.HandleModalKey(Key.S, ModifierKeys.Control), "");
        gizmo.EndModal(commit: false);

        // And with nothing running, the same keys are free — otherwise Space could never toggle walk mode.
        check("with no modal running the keys pass through",
            !gizmo.HandleModalKey(Key.Space, ModifierKeys.None)
            && !gizmo.HandleModalKey(Key.Escape, ModifierKeys.None), "");
    }

    // A modal transform is started from the keyboard under ANY tool, so the overlay has to draw for it even when
    // the tool shelf is on Select and would otherwise show no gizmo at all.
    private static void CheckModalDrawsUnderAnyTool(Action<string, bool, string> check)
    {
        (TransformGizmo gizmo, FakeTransformGizmoHost host) = NewOverlay();
        host.GizmoMode = GizmoMode.None;    // Select tool
        host.HasGizmoTarget = false;        // ...so the shelf shows no gizmo

        check("no gizmo is drawn for the Select tool on its own", PaintedPixels(gizmo) == 0, "");
        gizmo.BeginModal(GizmoMode.Move, new Point(200, 150));
        int painted = PaintedPixels(gizmo);
        gizmo.EndModal(commit: false);
        check("a modal transform draws its own gizmo under the Select tool", painted > 0, $"{painted} px");
    }

    private const int OverlayW = 400, OverlayH = 300;

    // A laid-out gizmo overlay bound to a fake host, ready to render or be driven by keys.
    private static (TransformGizmo Gizmo, FakeTransformGizmoHost Host) NewOverlay()
    {
        var host = new FakeTransformGizmoHost();   // identity view-projection → the pivot lands mid-element
        var gizmo = new TransformGizmo();
        gizmo.Attach(host);
        gizmo.Measure(new Size(OverlayW, OverlayH));
        gizmo.Arrange(new Rect(0, 0, OverlayW, OverlayH));
        gizmo.UpdateLayout();
        return (gizmo, host);
    }

    private static int PaintedPixels(TransformGizmo gizmo)
    {
        gizmo.InvalidateVisual();
        gizmo.UpdateLayout();
        var rtb = new RenderTargetBitmap(OverlayW, OverlayH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(gizmo);
        int stride = OverlayW * 4;
        var px = new byte[stride * OverlayH];
        rtb.CopyPixels(px, stride, 0);
        int painted = 0;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) painted++;
        return painted;
    }
}
