using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;
using Illusion.ViewModels;
using Illusion.Viewport;
using Illusion.Views;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What a Blender push does to a car the modder is in the middle of editing.
///
/// <para>
/// A push can change the very bones the stitching keys on — a renamed bone, a deleted one, a new one — so the
/// resolver has to run again over what landed, exactly as it does on every open. The failure this measures
/// against is not a crash: it is a tree that goes on showing a car the file no longer holds, and a modder
/// typing numbers into a component that is no longer there.
/// </para>
/// <para>
/// The push is driven at its own seam — <c>PushLanding</c> / <c>PushLanded</c>, the pair the bridge raises
/// around its apply — with the frame graph changed by hand in between, the way a push changes it. Blender is
/// never launched: what is under test is the editor's half.
/// </para>
/// <para>
/// The install is read and never written. The one save runs through a redirect into a scratch mirror; every
/// other mutation is made in memory and put back. Output: %TEMP%\illusion_car_push.txt
/// </para>
/// </summary>
internal static class CarPushProbes
{
    /// <summary>Where the redirected save lands. Cleared at the start of every run.</summary>
    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "illusion_car_push");

    internal static void RunCarPushProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_push.txt");
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
            Clear();
            string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
            var archive = new FileInfo(Path.Combine(folder, focus + ".sds"));
            string extracted = MafiaEnvironment.ExtractedDir(archive);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml")))
            {
                Check("the focus car is extracted", false, focus);
                return;
            }

            Resolver(sb, extracted, focus, Check);
            Verbatim(sb, extracted, focus, Check);
            // Staged ONCE for both panel sections: staging loads the archive and builds the whole scene tree,
            // and probes already run sequentially because they race each other on extraction.
            if (ComponentTreeProbes.Stage(archive, out ScenePanel? panel, out D3DImageHost? host)
                && panel != null && host != null)
            {
                Tree(sb, archive, panel, host, Check);
                Concurrency(sb, panel, host, Check);
            }
            else
            {
                Check("the car stages", false, archive.FullName);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            // The verdict goes on in the FINALLY, because it is the only thing every exit path shares: a
            // probe's exit code is 0 whatever happened, so a report with no verdict line at the top reads as
            // inconclusive rather than as failed.
            sb.Insert(0, $"CAR PUSH PROBE ({focus}): {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // ── the resolver: who is still who after a push ──

    /// <summary>
    /// The three things a re-stitch has to get right about identity, in the order they are hard.
    ///
    /// <para>
    /// A push that changed nothing must renumber nothing. A push that RENAMED a bone must leave the part
    /// where it was — the part is what the modder edits, and the renamed bone is a new thing of the car
    /// beside it. And a component that has since been given a deform part is still the same component,
    /// although the key it is carried by has moved from the rig to the part list.
    /// </para>
    /// </summary>
    private static void Resolver(
        StringBuilder sb, string extracted, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine("════ who is still who after a push ════");

        Car? before = Car.ReadFrom(extracted);
        if (before?.Frames == null) { check("the focus car reads", false, focus); return; }

        Car quiet = Car.Stitch(before.Prefab, before.Frames, lod: 0, previous: before);
        int kept = quiet.Components.Count(c => before.ComponentById(c.Id) != null);
        check("a push that changed nothing renumbers nothing",
            quiet.Components.Count == before.Components.Count && kept == quiet.Components.Count,
            $"{kept} of {quiet.Components.Count} components kept their identity");

        // ── the rename, which is the case the whole ticket turns on ──
        //
        // Two rows come out of one and both are honest: the PART, which now names a hash nothing answers to,
        // and the bone under its new name, which no part claims and which therefore carries geometry as a bare
        // component. What must not happen is the identities swapping over — the part reading as brand new and
        // the stub inheriting the door the modder had selected.
        CarComponent? door = before.Components.FirstOrDefault(c =>
            !c.IsBare && c.BoneJoint >= 0 && c.PartType != 1);
        HashName[] bones = Bones(before);
        if (door == null || bones.Length == 0)
        {
            check("the focus car has a part to rename", false, focus);
            return;
        }

        string was = bones[door.BoneJoint].String;
        string now = was + "_pushed";
        bones[door.BoneJoint].String = now;
        Car pushed = Car.Stitch(before.Prefab, before.Frames, lod: 0, previous: before);
        bones[door.BoneJoint].String = was;

        CarComponent? still = pushed.ComponentById(door.Id);
        CarComponent? stub = pushed.Components.FirstOrDefault(c =>
            c.IsBare && string.Equals(c.Name, now, StringComparison.Ordinal));
        CarFault? fault = pushed.Faults.FirstOrDefault(f =>
            f.Kind == CarFaultKind.ComponentBoneUnresolved && f.Component == door.Id);

        sb.AppendLine($"  \"{was}\" ({door.Kind}, part {door.PartIndex}, joint {door.BoneJoint}) renamed to "
            + $"\"{now}\": the part is identity {door.Id} either side, the bone is now "
            + $"{(stub == null ? "no component" : "identity " + stub.Id)}");
        check("a push that renames a bone leaves the part where it was, under the identity it had",
            still != null && still.PartIndex == door.PartIndex && !still.BoneResolves,
            still == null ? "the identity was not carried" : $"{still.Name}, part {still.PartIndex}");
        check("…shown as a fault rather than as a component that vanished",
            fault != null, fault?.What ?? "nothing reported");
        check("…and the fault names the bone that now stands in its place, so the two read as one accident",
            fault != null && fault.What.Contains(now, StringComparison.Ordinal),
            fault?.What ?? "");
        check("…while the renamed bone is a component of its OWN, not the part wearing a new name",
            stub != null && stub.Id != door.Id && before.ComponentById(stub.Id) == null,
            stub == null ? "no bare component was minted" : $"{stub.Name} is identity {stub.Id}");

        // ── and the other direction: a key that MOVES between stitches ──
        //
        // A bare component is carried by its place in the rig and a part by its place in the part list, so
        // giving one a deform part moves it from the first key to the second. It is the same door either way,
        // and undo and the bridge have to go on saying so.
        Car? granted = Car.ReadFrom(extracted);
        CarComponent? bare = granted?.Components.FirstOrDefault(c =>
            c.IsBare && c.HasGeometry && c.BoneResolves);
        if (granted?.Frames == null || bare == null || granted.Body is not { } body)
        {
            check("the focus car has a bare component to promote", false, focus);
            return;
        }
        if (granted.GrantDeformPart(bare, Car.DefaultPartKind, body, out string? refusal) == null)
        {
            check("the bare component takes a deform part", false, refusal ?? "");
            return;
        }

        Car promoted = Car.Stitch(granted.Prefab, granted.Frames, lod: 0, previous: granted);
        CarComponent? grown = promoted.ComponentById(bare.Id);
        check("a component given a deform part is still the same component",
            grown is { IsBare: false } && grown.BoneHash == bare.BoneHash,
            grown == null ? "the identity was not carried" : $"{grown.Name} ({grown.Kind})");

        // ── and the same key moving the other way: a part taken OUT ──
        //
        // Removing a deform part removes the entry, so every part after it shifts down one index. A component
        // keyed on its place in the part list would then recall its neighbour's identity, and the whole tail
        // of the car would slide along by one with nothing to say it had. This is what the joint keeps steady.
        Car? demoted = Car.ReadFrom(extracted);
        if (demoted?.Frames == null) { check("the focus car reads", false, focus); return; }

        // The first part that will actually come out. Most of them will not: the body never does, and a part
        // any joint of the deformation machinery names would leave that pointing at nothing. Which one it is
        // does not matter here — what is measured is what happens to everybody BELOW it.
        CarComponent? victim = null;
        Dictionary<int, ComponentId> below = [];
        string? why = null;
        foreach (CarComponent candidate in demoted.Components.Where(c =>
                     !c.IsBare && c.PartIndex < demoted.Prefab.CarDeformParts.Count - 1))
        {
            below = demoted.Components
                .Where(c => !c.IsBare && c.PartIndex > candidate.PartIndex)
                .ToDictionary(c => c.PartIndex, c => c.Id);
            if (demoted.RemoveDeformPart(candidate, out why) == null) continue;
            victim = candidate;
            break;
        }
        if (victim == null)
        {
            check("the focus car has a part that can be taken out", false, why ?? "");
            return;
        }

        Car after = Car.Stitch(demoted.Prefab, demoted.Frames, lod: 0, previous: demoted);
        // Every part that was AFTER the one taken out now sits one index lower, and must still be itself.
        int slid = below.Count(p => after.ComponentById(p.Value) is not { } now
            || now.PartIndex != p.Key - 1);
        sb.AppendLine($"  part {victim.PartIndex} (\"{victim.Name}\", {victim.Kind}) taken out: "
            + $"{below.Count} parts renumbered below it, {slid} of them lost their identity");
        check("a part taken out renumbers the list and moves nobody's identity",
            below.Count > 0 && slid == 0, $"{slid} of {below.Count} slid onto a neighbour");
        // …and the component it was taken FROM is the same component, now bare.
        CarComponent? stripped = after.ComponentById(victim.Id);
        check("…and the component it was taken from is still the same component, now bare",
            stripped is { IsBare: true } && stripped.BoneHash == victim.BoneHash,
            stripped == null ? "the identity was not carried" : $"{stripped.Name} ({stripped.Kind})");
        sb.AppendLine();
    }


    // ── what a save owes a car somebody else has just written ──

    /// <summary>
    /// A car pushed and then saved with no further edit still writes back what the push did not touch, byte
    /// for byte. The push is the frame graph's business; the prefab is the aggregate's, and it must come out
    /// of a re-stitch holding exactly the bytes it went in with.
    /// </summary>
    private static void Verbatim(
        StringBuilder sb, string extracted, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine("════ a car pushed and then saved with no further edit ════");

        Car? car = Car.ReadFrom(extracted);
        if (car?.Frames == null || car.PrefabPath == null)
        {
            check("the focus car reads", false, focus);
            return;
        }
        byte[] original = File.ReadAllBytes(car.PrefabPath);

        HashName[] bones = Bones(car);
        CarComponent? part = car.Components.FirstOrDefault(c =>
            !c.IsBare && c.BoneJoint >= 0 && c.PartType != 1);
        if (part == null || bones.Length == 0)
        {
            check("the focus car has a part to rename", false, focus);
            return;
        }

        string was = bones[part.BoneJoint].String;
        bones[part.BoneJoint].String = was + "_pushed";
        Car pushed = Car.Stitch(
            car.Prefab, car.Frames, lod: 0, previous: car, car.PrefabPath, extracted);
        CarSave saved = pushed.Save(path => Path.Combine(Scratch, focus, Path.GetRelativePath(extracted, path)));
        bones[part.BoneJoint].String = was;

        byte[] written = saved.Written.Count == 1 ? File.ReadAllBytes(saved.Written[0]) : [];
        sb.AppendLine($"  saved after the push: {saved.Written.Count} file(s) written, "
            + $"{saved.Unchanged.Count} already right, {saved.Lost.Count} lost");
        check("a car pushed and then saved with no further edit writes its prefab back byte for byte",
            saved.Ok && written.Length == original.Length && original.AsSpan().SequenceEqual(written),
            saved.Ok
                ? $"{written.Length} bytes against {original.Length}, first difference at "
                    + FirstDiff(original, written)
                : string.Join("; ", saved.Lost.Take(3)));
        // The push changed the FRAME graph, and the car did not: re-serializing a frame resource nobody asked
        // it to renumbers indices and prunes blocks, which is a rewrite of a resource this save has no
        // business touching. Whoever applied the push owns writing it.
        check("…and it does not take the frame graph with it, which is not its edit to save",
            !pushed.FramesChanged && saved.Written.Count == 1,
            $"framesChanged={pushed.FramesChanged}, {saved.Written.Count} file(s)");
        sb.AppendLine();
    }

    // ── the tree the modder is looking at while it lands ──

    private static void Tree(
        StringBuilder sb, FileInfo archive, ScenePanel panel, D3DImageHost host,
        Action<string, bool, string> check)
    {
        sb.AppendLine("════ the tree while a push lands ════");
        var notices = new List<string>();
        host.TransientNotice += (message, _) => notices.Add(message);
        ComponentTreeViewModel components = panel.Components;
        if (components.Car is not { } staged) { check("the car stitched", false, ""); return; }
        if (Model(host) is not { } model) { check("the staged car has a skinned model", false, ""); return; }
        HashName[] bones = model.GetSkeletonObject().BoneNames!;

        // ── a rename, with the affected component selected ──
        ComponentRowViewModel? row = components.Roots.SelectMany(r => r.SelfAndDescendants())
            .FirstOrDefault(r => !r.IsBare && !r.IsBroken && r.Component.PartType != 1
                && r.Component.BoneJoint >= 0);
        if (row == null) { check("the car has a part to select", false, ""); return; }

        components.Select(row);
        ComponentId chosen = row.Id;
        int rowsBefore = components.Roots.SelectMany(r => r.SelfAndDescendants()).Count();
        string was = bones[row.Component.BoneJoint].String;
        bones[row.Component.BoneJoint].String = was + "_pushed";
        Land(host);

        List<ComponentRowViewModel> after = [.. components.Roots.SelectMany(r => r.SelfAndDescendants())];
        ComponentRowViewModel? kept = components.RowOf(chosen);
        check("a push re-runs the resolver and the tree reflects the pushed car",
            after.Count == rowsBefore + 1
            && after.Any(r => r.IsBare && r.Name == was + "_pushed"),
            $"{after.Count} rows, {rowsBefore} before");
        check("…and the selected component keeps its row, because it kept its identity",
            kept != null && ReferenceEquals(components.Selected, kept) && kept.IsBroken,
            kept == null ? "the row is gone" : $"{kept.Name}, broken={kept.IsBroken}");

        bones[row.Component.BoneJoint].String = was;
        Land(host);

        // ── the redo branch ──
        //
        // Every action on it was recorded against the car as it stood BEFORE the push, so replaying one would
        // put its snapshot back over geometry that has changed underneath it. The undo stack is left alone:
        // it is the way back out, and it is what a modder reaches for when a push went wrong.
        var log = new List<string>();
        var first = new FakeEdit(log, "first");
        var second = new FakeEdit(log, "second");
        host.History.Push(first);
        host.History.Push(second);
        host.History.Undo();
        bool armed = host.History.CanRedo && host.History.CanUndo;
        Land(host);
        check("the redo stack does not survive a push",
            armed && !host.History.CanRedo && second.Discarded,
            $"armed={armed}, canRedo={host.History.CanRedo}, discarded={second.Discarded}");
        check("…and the undo stack does",
            host.History.CanUndo && !first.Discarded, $"canUndo={host.History.CanUndo}");
        host.History.Clear();

        // ── the bridge and the tree naming the same component ──
        //
        // A bone IS a component, and a rig push comes back naming bones. The two are joined through the
        // aggregate's own lookup and nowhere else, so what the push reports and what the tree lights are the
        // same component rather than two readings that agree until one of them is edited.
        CarComponent? moved = components.Car?.Components.FirstOrDefault(c =>
            c.BoneResolves && c.BoneHash != 0 && !c.IsBare);
        if (moved != null)
        {
            notices.Clear();
            Land(host, moved.Name);
            ComponentRowViewModel? named = components.RowOf(moved.Id);
            check("a push names the components it moved, by the identity the tree holds",
                named != null && components.Car?.ComponentOfBone(Fnv64.Hash(moved.Name))?.Id == named.Id
                && notices.Any(n => n.Contains(moved.Name, StringComparison.Ordinal)
                    && n.Contains("moved", StringComparison.Ordinal)),
                notices.Count == 0 ? "nothing was reported" : string.Join(" | ", notices));
        }

        // ── a component the push took away ──
        //
        // A bare component is minted FROM its geometry, so a push that takes the last of it away makes the row
        // stop appearing rather than break anything visibly. With that row selected, silence would leave the
        // modder editing a licence plate that is not there.
        ComponentRowViewModel? plate = components.Roots.SelectMany(r => r.SelfAndDescendants())
            .FirstOrDefault(r => r.IsBare && r.Component.HasGeometry && r.Component.BoneJoint >= 0);
        if (plate != null)
        {
            components.Select(plate);
            string name = plate.Name;
            FrameObjectModel.WeightedByMeshSplit[] splits = model.BlendMeshSplits;
            model.BlendMeshSplits = [.. splits.Where(s => Bone(s, model, bones) != plate.Component.BoneHash)];
            notices.Clear();
            Land(host);

            check("a component the push took away is reported rather than quietly dropped",
                components.RowOf(plate.Id) == null
                && notices.Any(n => n.Contains(name, StringComparison.Ordinal)),
                notices.Count == 0 ? "nothing was reported" : string.Join(" | ", notices));
            check("…and the tree stops claiming it is selected",
                components.Selected?.Id != plate.Id,
                components.Selected?.Name ?? "(nothing)");

            model.BlendMeshSplits = splits;
            Land(host);
        }
        sb.AppendLine($"  {archive.Name}: {staged.Components.Count} components, "
            + $"{notices.Count} notice(s) raised by the pushes above\n");
    }

    // ── an edit and a push, never at the same time ──

    /// <summary>
    /// Every component-level intent refuses while a push has the car, and every one of them is available
    /// again the moment it lets go.
    ///
    /// <para>
    /// All eleven, deliberately. The failure this guards against is not one intent being wrong: it is a
    /// twelfth being added later without the hold, and only a list that names them all can catch that. Each
    /// is called with no row, so an intent that took the gate and one that did not are told apart by WHICH
    /// refusal comes back, and nothing is written to the archive either way.
    /// </para>
    /// </summary>
    private static void Concurrency(
        StringBuilder sb, ScenePanel panel, D3DImageHost host, Action<string, bool, string> check)
    {
        sb.AppendLine("════ a component edit and a push are never applied at the same time ════");
        ComponentTreeViewModel components = panel.Components;

        // How many intents there ARE, counted off the type rather than off the list below — a twelfth added
        // later without the hold is the failure this section exists to catch, and a hand-written count would
        // simply be updated to match it.
        int intents = typeof(ComponentTreeViewModel).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Count(m => m.GetParameters() is { Length: > 0 } p && p[^1].IsOut
                && p[^1].Name == "refusal");

        IReadOnlyList<string?> idle = Intents(components);
        check("every component-level intent this view offers is one this probe asks for",
            idle.Count == intents, $"{idle.Count} asked for, {intents} on the view");
        check("with no push in flight, an intent gets as far as looking for its row",
            idle.All(r => r != null && !Landing(r)), string.Join(" | ", idle.Distinct()));

        if (!host.BridgeSession.TryHoldForEdit())
        {
            check("a push can take the car", false, "the gate was already held");
            return;
        }
        IReadOnlyList<string?> held;
        try { held = Intents(components); }
        finally { host.BridgeSession.ReleaseAfterEdit(); }

        sb.AppendLine($"  {held.Count} intents, refused with: "
            + string.Join(" | ", held.Where(r => r != null).Distinct()));
        check("every component-level intent refuses while a push has the car",
            held.Count == intents && held.All(Landing),
            $"{held.Count(Landing)} of {held.Count} refused with the push");
        check("…and every one of them is available again the moment it lets go",
            Intents(components).All(r => r != null && !Landing(r)), "");
        sb.AppendLine();
    }

    /// <summary>Whether a refusal is the one a push landing gives.</summary>
    private static bool Landing(string? refusal) =>
        refusal != null && refusal.Contains("push from Blender is landing", StringComparison.Ordinal);

    /// <summary>
    /// Every intent the component view can make, each asked for with no row — so the answer is a refusal
    /// whatever the gate says, and the two refusals are told apart by their words.
    /// </summary>
    private static IReadOnlyList<string?> Intents(ComponentTreeViewModel components)
    {
        var refusals = new List<string?>();
        components.AddCollision(
            null, CarCollisionRole.Body, CarCollisionShape.Box, Vector3.One, Vector3.Zero, out string? add);
        refusals.Add(add);
        components.SetCollision(null, Vector3.One, Vector3.Zero, out string? set);
        refusals.Add(set);
        components.RemoveCollision(null, out string? remove);
        refusals.Add(remove);
        components.GrantDeformPart(null, Car.DefaultPartKind, null, out string? grant);
        refusals.Add(grant);
        components.RemoveDeformPart(null, out string? demote);
        refusals.Add(demote);
        // The two that reach into the rig. Both refuse in the aggregate whatever the gate says, and they are
        // here because what is being measured is the GATE: an intent that skipped it would come back with its
        // own refusal instead of the push's, which is exactly what the check below reads.
        components.AddComponent("", Car.DefaultPartKind, null, out string? addComponent);
        refusals.Add(addComponent);
        components.RemoveComponent(null, out string? removeComponent);
        refusals.Add(removeComponent);
        components.SetMarker(null, [], out string? marker);
        refusals.Add(marker);
        components.SetDataRow(null, [], out string? dataRow);
        refusals.Add(dataRow);
        components.SetDamage(null, [], out string? damage);
        refusals.Add(damage);
        components.SetHandle(null, [], out string? handle);
        refusals.Add(handle);
        components.AddMarker(null, CarMarkerRole.Seat, out string? addMarker);
        refusals.Add(addMarker);
        components.RemoveMarker(null, out string? removeMarker);
        refusals.Add(removeMarker);
        return refusals;
    }

    // ── the seam a push lands through ──

    /// <summary>One push, driven at the pair the bridge raises around its own apply. The frame graph has
    /// already been changed by whoever called this — that is what a push is.</summary>
    private static void Land(D3DImageHost host, params string[] movedBones)
    {
        host.RaisePushLanding();
        host.RaisePushLanded(movedBones);
    }

    private static HashName[] Bones(Car car) =>
        car.Frames?.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault() is { } model
            ? model.GetSkeletonObject().BoneNames ?? []
            : [];

    private static FrameObjectModel? Model(D3DImageHost host)
    {
        FrameObjectModel? found = null;
        void Walk(Scene.SceneNode node)
        {
            if (found != null) return;
            if (node.Source is Assets.Adapters.FrameNodeAdapter { Frame: FrameObjectModel model })
            {
                found = model;
                return;
            }
            foreach (Scene.SceneNode child in node.Children) Walk(child);
        }
        foreach (Scene.SceneNode root in host.Tree.Roots) Walk(root);
        return found;
    }

    /// <summary>The bone a split names, through level 0's own remap table — reading the raw
    /// <c>BlendIndex</c> as a bone id is right 2.2 % of the time.</summary>
    private static ulong Bone(
        FrameObjectModel.WeightedByMeshSplit split, FrameObjectModel model, HashName[] bones)
    {
        byte[] remap;
        try
        {
            FrameBlendInfo.BoneIndexInfo[] levels = model.GetBlendInfoObject().BoneIndexInfos ?? [];
            remap = levels.Length > 0 ? levels[0].BoneRemapIDs ?? [] : [];
        }
        catch (Exception) { return 0; }
        if (split.BlendIndex >= remap.Length) return 0;

        int bone = remap[split.BlendIndex];
        return bone >= 0 && bone < bones.Length ? Fnv64.Hash(bones[bone].ToString()) : 0;
    }

    private static void Clear()
    {
        try
        {
            if (Directory.Exists(Scratch)) Directory.Delete(Scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover mirror only costs disk: the one file this run compares is one it wrote itself.
        }
    }
}
