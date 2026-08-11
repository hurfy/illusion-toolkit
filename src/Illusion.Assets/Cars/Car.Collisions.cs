using System.Numerics;
using Illusion.Assets.Frames;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.ItemDesc;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Cars;

/// <summary>One ItemDesc record of the archive, with the file it came out of.</summary>
internal sealed record CarShapeRecord(ItemDescFile Shape, string File);

public sealed partial class Car
{
    /// <summary>The archive's ItemDesc records by the DATA hash a prefab volume names them by.</summary>
    private readonly Dictionary<ulong, CarShapeRecord> _shapesByData = [];

    /// <summary>The same records by the FILE hash a mirror stub names them by.</summary>
    private readonly Dictionary<ulong, CarShapeRecord> _shapesByFile = [];

    /// <summary>The mirror stubs of the frame graph, by the record file hash each one names.</summary>
    private readonly Dictionary<ulong, FrameObjectCollision> _stubsByFile = [];

    /// <summary>Where every bone of the car's rig sits — the two spaces a volume can be written in.</summary>
    private readonly Dictionary<ulong, int> _jointOfBone = [];

    /// <summary>
    /// ItemDesc files this car has changed and not yet written, by full path. A null value means the file must
    /// go. Flushed by <see cref="Save"/>, so a refused save writes nothing at all.
    /// </summary>
    private readonly Dictionary<string, byte[]?> _pendingShapes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The ItemDesc record a prefab volume's DATA hash names, or null when the archive carries none.
    ///
    /// <para>
    /// Two ids, two readers: a volume finds a record by this hash (1097 of 1097 shipped ones), while a mirror
    /// stub finds the same record by the record's own FILE hash. Both keys are needed to walk from either end
    /// to the other, which is why this answers with the record rather than with a size.
    /// </para>
    /// </summary>
    public ItemDescFile? Shape(ulong dataHash) =>
        dataHash != 0 && _shapesByData.TryGetValue(dataHash, out CarShapeRecord? found) ? found.Shape : null;

    /// <summary>The car's skinned model — what places a volume, and what carries the hit boxes.</summary>
    private FrameObjectModel? Model =>
        Frames?.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault();

    // ── reading ──

    /// <summary>
    /// Hangs each component's collisions off it, by role and shape, with every size and position restated in
    /// the COMPONENT's own space.
    ///
    /// <para>
    /// The restating is the whole job. A volume that places a shape is written in its own part's bone space,
    /// while one that describes itself is written in the bone space of the part its part hangs off — reading
    /// the second in the first's space misses by 1.196 m and leaves 47 % of them off the car entirely. Which
    /// of the two applies is decided by the stored type, and neither the type nor the spaces are ever shown.
    /// </para>
    /// </summary>
    private void HangCollisions(Rig rig)
    {
        foreach ((ulong bone, int joint) in rig.JointOfBone) _jointOfBone[bone] = joint;
        ReadShapes();
        ReadStubs();

        IReadOnlyList<CarDeformPart> parts = Prefab.CarDeformParts;
        FrameObjectModel? model = Model;

        foreach (CarComponent component in Components)
        {
            if (component.PartIndex < 0 || component.PartIndex >= parts.Count) continue;
            CarDeformPart part = parts[component.PartIndex];
            int own = component.BoneJoint;
            int space = SpaceJoint(part, own);

            foreach (CarPhysicsVolume volume in part.Volumes)
            {
                component.AddCollision(Read(component, part, volume, model, own, space));
            }
        }
    }

    /// <summary>The joint whose space a SELF-DESCRIBING volume of this part is written in: the bone of the
    /// part it hangs off, or its own when it hangs off nothing.</summary>
    private int SpaceJoint(CarDeformPart part, int own) =>
        part.ParentFrame != 0 && _jointOfBone.TryGetValue(part.ParentFrame, out int above) ? above : own;

    private CarCollision Read(
        CarComponent component, CarDeformPart part, CarPhysicsVolume volume,
        FrameObjectModel? model, int own, int space)
    {
        CarCollisionRole role = CarCollision.RoleOf(volume.VolumeType);
        if (role == CarCollisionRole.Body)
        {
            // A placed shape states its own size and stands in its own part's bone space, so nothing has to be
            // converted — but the size lives in the record, and a record the archive does not carry is a
            // collision the toolkit can show and must not resize.
            if (!_shapesByData.TryGetValue(volume.ShapeHash, out CarShapeRecord? record)
                || record.Shape.Element is not RigidBodyElement rigid)
            {
                return new CarCollision(
                    role, CarCollisionShape.Hull, Vector3.Zero, volume.Transform,
                    component.Id, part.Index, volume.Index,
                    "the shape record this names is not in the archive, so its size cannot be read or written");
            }
            CarCollisionShape shape = CarCollision.ShapeOf(rigid.Shape);
            return new CarCollision(
                role, shape, CarCollision.FullSizeOf(rigid), volume.Transform,
                component.Id, part.Index, volume.Index,
                shape == CarCollisionShape.Hull
                    ? "a cooked hull cannot be resized — the cooker the toolkit ships only cooks triangle "
                        + "meshes, so this shape cannot be made again at another size"
                    : null);
        }

        // Glass and zones describe themselves, in the space of the part their part hangs off. Restating that
        // in the component's own space is what keeps the modder out of a distinction they never chose.
        Matrix4x4 placement = volume.Transform;
        string? readOnly = null;
        if (space != own)
        {
            if (model == null || own < 0 || space < 0)
            {
                readOnly = "this component's bone is not in the car's rig, so its collision has no space to "
                    + "be placed in";
            }
            else
            {
                placement = TransformMath.ComputeLocalTransform(
                    model.PlaceOnJoint(volume.Transform, space), model.GetJointWorldTransform(own));
            }
        }
        return new CarCollision(
            role, CarCollisionShape.Box, volume.Size, placement,
            component.Id, part.Index, volume.Index, readOnly);
    }

    private void ReadShapes()
    {
        _shapesByData.Clear();
        _shapesByFile.Clear();
        if (Extracted == null) return;
        foreach (CarShapeRecord record in EachShape(Extracted)) Register(record);
    }

    private void Register(CarShapeRecord record)
    {
        _shapesByFile[record.Shape.Hash] = record;
        if (record.Shape.Element != null) _shapesByData[record.Shape.Element.DataHash] = record;
    }

    private void Forget(string path)
    {
        foreach (ulong key in _shapesByFile
                     .Where(p => string.Equals(p.Value.File, path, StringComparison.OrdinalIgnoreCase))
                     .Select(p => p.Key).ToList())
        {
            _shapesByFile.Remove(key);
        }
        foreach (ulong key in _shapesByData
                     .Where(p => string.Equals(p.Value.File, path, StringComparison.OrdinalIgnoreCase))
                     .Select(p => p.Key).ToList())
        {
            _shapesByData.Remove(key);
        }
    }

    private static IEnumerable<CarShapeRecord> EachShape(string extracted)
    {
        IReadOnlyList<string> files;
        try { files = SdsManifest.Load(extracted).GetFiles("ItemDesc"); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { yield break; }

        foreach (string file in files)
        {
            ItemDescFile? shape = null;
            try { shape = ItemDescFile.Load(file); }
            catch (Exception) { /* a record this library cannot read has nothing to contribute */ }
            if (shape != null) yield return new CarShapeRecord(shape, file);
        }
    }

    private void ReadStubs()
    {
        _stubsByFile.Clear();
        if (Frames?.FrameObjects == null) return;
        foreach (FrameObjectCollision stub in Frames.FrameObjects.Values.OfType<FrameObjectCollision>())
        {
            _stubsByFile.TryAdd(stub.Hash, stub);
        }
    }

    // ── writing ──

    /// <summary>
    /// Gives a component one more collision: a role, a shape, a size and a position in the component's own
    /// space. Everything else is derived and nothing else is asked.
    ///
    /// <para>
    /// Derived here and never shown: the stored volume type, which bone's space the matrix goes in, the axis
    /// reversal between the two copies of it, whether the extents mean a full size or half of one, the
    /// ItemDesc record a solid needs and the hashes that link the two of them, and the mirror stub in the
    /// frame graph — a copy the game does not read on a car at all, kept only so an edited archive stays the
    /// shape a shipped one is.
    /// </para>
    /// <para>
    /// Nothing reaches a file here. The edit lands in the structures this car holds, and <see cref="Save"/>
    /// is what writes them.
    /// </para>
    /// </summary>
    /// <param name="fullSize">The whole size in metres, never half of one. A capsule and a cylinder take
    /// their width from X and Y and their whole length from Z.</param>
    /// <param name="position">Where it sits, in the COMPONENT's own space.</param>
    /// <returns>Null with a <paramref name="refusal"/> when it cannot be done; nothing is changed then.</returns>
    public CarEdit? AddCollision(
        CarComponent component, CarCollisionRole role, CarCollisionShape shape,
        Vector3 fullSize, Vector3 position, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(component);
        refusal = null;

        if (!Placeable(component, role, shape, ref refusal)) return null;
        if (!CarCollision.Describe(shape, fullSize, out Vector3 half, out float radius, out float height,
                out refusal))
        {
            return null;
        }

        // The state to come BACK to is captured now and assembled at the end, because half of what this
        // intent touches does not exist yet: an undo has to know the name of the record it must unwrite and
        // the stub it must take out of the graph, and neither has been minted at this point.
        byte[] prefabWas = Prefab.ToBytes();
        FrameObjectModel.HitBoxInfo[]? boxesWere = CopyBoxes();
        var placement = Matrix4x4.CreateTranslation(position);

        ulong dataHash = 0;
        string? shapeFile = null;
        FrameObjectCollision? stub = null;
        if (role == CarCollisionRole.Body)
        {
            CarShapeRecord record = MintShape(shape, half, radius, height);
            dataHash = record.Shape.Element!.DataHash;
            shapeFile = record.File;
            stub = MintStub(component, placement, record.Shape.Hash);
        }

        int part = component.PartIndex;
        int volume = Prefab.AddCarVolume(
            part, Stored(component, role, placement), Extents(role, fullSize), dataHash,
            CarCollision.TypeOfRole(role));
        if (volume < 0)
        {
            // Nothing has been written to a file yet, so putting the car back is dropping what was just made.
            if (shapeFile != null) { _pendingShapes.Remove(shapeFile); Forget(shapeFile); }
            if (stub != null) DropFrame(stub);
            refusal = "the car's prefab would not take another collision on this component";
            return null;
        }

        var before = new CarState(
            prefabWas,
            shapeFile == null ? [] : [(shapeFile, null)],
            stub == null
                ? []
                : [new CarFrameState(stub, Present: false, stub.LocalTransform, component.BoneJoint,
                    Parent: null, Root: null, Order: -1, Attached: -1)],
            boxesWere);
        return new CarEdit(
            $"{CarCollision.RoleName(role)} {CarCollision.ShapeName(shape)} added to \"{component.Name}\"",
            before,
            Snapshot(shapeFile == null ? [] : [shapeFile], stub == null ? [] : [stub]));
    }

    /// <summary>
    /// Resizes and moves an existing collision, in the component's own space.
    ///
    /// <para>
    /// A SCALE on the placement is baked into the size rather than written: neither the prefab volume nor a
    /// PhysX shape transform has anywhere to keep one, so passing it through would drop it silently — which is
    /// what once made a box drawn 0.41 m thick in the editor reach 0.10 m in the game, thin enough for an arm
    /// to pass through the part.
    /// </para>
    /// </summary>
    /// <param name="placement">Where it goes, in the component's own space, scale and all.</param>
    /// <returns>Null with a <paramref name="refusal"/> when it cannot be done; nothing is changed then.</returns>
    public CarEdit? SetCollision(
        CarCollision collision, Vector3 fullSize, Matrix4x4 placement, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(collision);
        refusal = collision.ReadOnlyReason;
        if (refusal != null) return null;

        CarComponent? component = ComponentById(collision.Component);
        if (component == null) { refusal = "that collision's component is no longer there"; return null; }

        // The scale is folded into the numbers before anything else looks at them, so every path below sees
        // one size and none of them has to remember which of the two carries it.
        TransformMath.TryDecompose(placement, out Vector3 scale, out Quaternion rotation, out Vector3 at);
        if (scale.X > 0f && scale.Y > 0f && scale.Z > 0f) fullSize *= scale;
        Matrix4x4 unscaled = TransformMath.Compose(rotation, Vector3.One, at);

        if (!CarCollision.Describe(collision.Shape, fullSize, out Vector3 half, out float radius,
                out float height, out refusal))
        {
            return null;
        }

        var shapeFiles = new List<string>();
        var stubs = new List<FrameObjectCollision>();
        CarPhysicsVolume? stored = Volume(collision);
        if (stored == null) { refusal = "that collision is no longer there"; return null; }
        if (collision.Role == CarCollisionRole.Body)
        {
            if (!_shapesByData.TryGetValue(stored.ShapeHash, out CarShapeRecord? record)
                || record.Shape.Element is not RigidBodyElement rigid)
            {
                refusal = "the shape record this collision names is not in the archive";
                return null;
            }
            shapeFiles.Add(record.File);
            if (_stubsByFile.TryGetValue(record.Shape.Hash, out FrameObjectCollision? stub)) stubs.Add(stub);
        }

        CarState before = Snapshot(shapeFiles, stubs);

        if (collision.Role == CarCollisionRole.Body)
        {
            CarShapeRecord record = _shapesByData[stored.ShapeHash];
            Resize((RigidBodyElement)record.Shape.Element!, collision.Shape, half, radius, height);
            _pendingShapes[record.File] = record.Shape.ToBytes();
            foreach (FrameObjectCollision stub in stubs) stub.LocalTransform = unscaled;
        }

        if (!Prefab.SetCarVolume(collision.PartIndex, collision.VolumeIndex,
                Stored(component, collision.Role, unscaled), Extents(collision.Role, fullSize)))
        {
            refusal = "the prefab would not take the change";
            Restore(before);
            return null;
        }

        return new CarEdit(
            $"{collision.Label} on \"{component.Name}\" changed", before, Snapshot(shapeFiles, stubs));
    }

    /// <summary>
    /// Takes a collision off a component, and its ItemDesc record and mirror stub with it when nothing else
    /// names them — a record another volume still points at stays, because deleting it would take that other
    /// collision out of the car as a side effect.
    /// </summary>
    /// <returns>Null with a <paramref name="refusal"/> when it cannot be done; nothing is changed then.</returns>
    public CarEdit? RemoveCollision(CarCollision collision, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(collision);
        refusal = null;

        CarComponent? component = ComponentById(collision.Component);
        if (component == null) { refusal = "that collision's component is no longer there"; return null; }
        CarPhysicsVolume? stored = Volume(collision);
        if (stored == null) { refusal = "that collision is no longer there"; return null; }

        // What goes WITH it: the record it names when this is the last volume naming it, and that record's
        // mirror stub. Worked out before the volume is dropped, while the count still includes it.
        var shapeFiles = new List<string>();
        var stubs = new List<FrameObjectCollision>();
        CarShapeRecord? record = null;
        if (stored.ShapeHash != 0 && NamesOf(stored.ShapeHash) == 1
            && _shapesByData.TryGetValue(stored.ShapeHash, out record))
        {
            shapeFiles.Add(record.File);
            if (_stubsByFile.TryGetValue(record.Shape.Hash, out FrameObjectCollision? stub)) stubs.Add(stub);
        }

        CarState before = Snapshot(shapeFiles, stubs);
        if (Prefab.TakeCarVolume(collision.PartIndex, collision.VolumeIndex) == null)
        {
            refusal = "the prefab would not give the collision up";
            return null;
        }
        if (record != null)
        {
            _pendingShapes[record.File] = null;
            Forget(record.File);
            _stubsByFile.Remove(record.Shape.Hash);
        }
        foreach (FrameObjectCollision stub in stubs) DropFrame(stub);

        return new CarEdit(
            $"{collision.Label} removed from \"{component.Name}\"", before, Snapshot(shapeFiles, stubs));
    }

    /// <summary>
    /// Puts the car back to a state one intent recorded — the undo, and the redo, of everything above.
    ///
    /// <para>
    /// It restores what the structures HELD rather than reversing what was done to them. Running the
    /// derivation backwards would leave a car that differs from the one the modder started with wherever the
    /// derivation is lossy, and the hit-box rule is: it reproduces 65.3 % of the boxes a car ships with, so an
    /// undo that recomputed them would leave the other third rebuilt.
    /// </para>
    /// </summary>
    public void Restore(CarState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        using (var buffer = new MemoryStream(state.Prefab, writable: false))
        {
            Prefab = PrefabFile.Read(buffer);
        }

        foreach ((string path, byte[]? bytes) in state.Shapes)
        {
            _pendingShapes[path] = bytes;
            Forget(path);
            if (bytes == null) continue;
            try
            {
                using var buffer = new MemoryStream(bytes, writable: false);
                Register(new CarShapeRecord(ItemDescFile.Read(buffer), path));
            }
            catch (Exception) { /* a record we cannot read back is one we never wrote */ }
        }

        foreach (CarFrameState frame in state.FrameStates)
        {
            if (frame.Present) PutFrame(frame); else DropFrame(frame.Frame);
        }

        if (state.HitBoxes != null && Model is { } model)
        {
            // A COPY, never the snapshot's own array. `RebuildHitBoxes` writes into `model.HitBoxes` in
            // place, so handing the stored array out would make the live boxes and the snapshot the same
            // object — and the next edit would silently rewrite the state an undo is meant to come back to.
            model.HitBoxes = Copy(state.HitBoxes);
            model.RecomputeSplitCounters();
            TouchFrames();
        }
    }

    // ── the derivation ──

    /// <summary>Whether a component can take a collision of this role and shape at all.</summary>
    private bool Placeable(
        CarComponent component, CarCollisionRole role, CarCollisionShape shape, ref string? refusal)
    {
        if (component.IsBare)
        {
            refusal = $"\"{component.Name}\" has no deform part, so there is nothing for a collision to hang "
                + "off. Give it one first.";
            return false;
        }
        if (component.PartIndex < 0 || component.PartIndex >= Prefab.CarDeformParts.Count)
        {
            refusal = "that component is no longer in the car";
            return false;
        }
        // Without a bone there is no space to write the placement in — and the game has nothing to hang the
        // collision off either, so a volume written here would be one nothing ever reads.
        if (!component.BoneResolves || component.BoneJoint < 0)
        {
            refusal = $"\"{component.Name}\" names a bone this car does not have, so there is nowhere to put "
                + "a collision. Put the bone back in Blender and push again.";
            return false;
        }
        if (role != CarCollisionRole.Body && shape != CarCollisionShape.Box)
        {
            refusal = $"{CarCollision.RoleName(role).ToLowerInvariant()} is always a plain box — only the "
                + "car's own solid names a shape record, and a box is all a self-describing volume can say";
            return false;
        }
        if (role == CarCollisionRole.Body && Extracted == null)
        {
            refusal = "this car was stitched in memory, so there is nowhere to put the shape record a solid "
                + "collision needs";
            return false;
        }
        return true;
    }

    /// <summary>
    /// The matrix as the FILE wants it: a placed shape in the component's own bone space, which is where it
    /// already is, and a self-describing one in the bone space of the part its part hangs off.
    /// </summary>
    private Matrix4x4 Stored(CarComponent component, CarCollisionRole role, Matrix4x4 inComponentSpace)
    {
        if (role == CarCollisionRole.Body) return inComponentSpace;

        IReadOnlyList<CarDeformPart> parts = Prefab.CarDeformParts;
        if (component.PartIndex < 0 || component.PartIndex >= parts.Count) return inComponentSpace;
        int own = component.BoneJoint;
        int space = SpaceJoint(parts[component.PartIndex], own);
        if (space == own || own < 0 || space < 0 || Model is not { } model) return inComponentSpace;

        return TransformMath.ComputeLocalTransform(
            model.PlaceOnJoint(inComponentSpace, own), model.GetJointWorldTransform(space));
    }

    /// <summary>What the volume states for its own size: a full size when it describes itself, and the 1 cm
    /// placeholder every shipped placed shape carries when the record states it instead.</summary>
    private static Vector3 Extents(CarCollisionRole role, Vector3 fullSize) =>
        role == CarCollisionRole.Body ? new Vector3(0.01f) : fullSize;

    /// <summary>How many volumes of the whole car name one shape record — what decides whether removing a
    /// collision may take the record with it.</summary>
    private int NamesOf(ulong dataHash)
    {
        int found = 0;
        foreach (CarDeformPart part in Prefab.CarDeformParts)
        {
            foreach (CarPhysicsVolume volume in part.Volumes)
            {
                if (volume.ShapeHash == dataHash) found++;
            }
        }
        return found;
    }

    private CarPhysicsVolume? Volume(CarCollision collision)
    {
        IReadOnlyList<CarDeformPart> parts = Prefab.CarDeformParts;
        if (collision.PartIndex < 0 || collision.PartIndex >= parts.Count) return null;
        IReadOnlyList<CarPhysicsVolume> volumes = parts[collision.PartIndex].Volumes;
        return collision.VolumeIndex >= 0 && collision.VolumeIndex < volumes.Count
            ? volumes[collision.VolumeIndex]
            : null;
    }

    // ── the ItemDesc record ──

    /// <summary>Manifest version the shipped ItemDesc entries carry.</summary>
    private const int ItemDescVersion = 3;

    /// <summary>
    /// Writes a new shape record for a solid collision — into this car's pending writes, not yet to a file.
    ///
    /// <para>
    /// BOTH of its hashes are minted clear of everything the archive already carries. A record answers to two
    /// of them and they are read by different readers — the prefab volume finds it by the DATA hash, the
    /// mirror stub by the FILE hash — and handing out a data hash some other record already answers to is a
    /// bug this codebase has had once: the new volume resolved to the older record and the shape drew in the
    /// wrong place.
    /// </para>
    /// </summary>
    private CarShapeRecord MintShape(CarCollisionShape shape, Vector3 half, float radius, float height)
    {
        var taken = new HashSet<ulong>(_shapesByFile.Keys);
        foreach (ulong data in _shapesByData.Keys) taken.Add(data);

        string stem = CarCollision.ShapeName(shape) + "_"
            + taken.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ulong hash = MintHash(stem, taken);
        ulong dataHash = MintHash(stem + "#data", taken);

        var record = new ItemDescFile
        {
            Hash = hash,
            Type = ItemDescType.RigidBody,
            SubType = (byte)CarCollision.RecordShapeOf(shape)!.Value,
            Element = new RigidBodyElement
            {
                DataHash = dataHash,
                Shape = CarCollision.RecordShapeOf(shape)!.Value,
                // 0 on all 1174 shipped car shapes: a car's surfaces are not chosen here.
                MaterialId = 0,
                Layer = -1,                 // every shape on every shipped car
                Transform = [1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f],
            },
        };
        Resize((RigidBodyElement)record.Element, shape, half, radius, height);

        var made = new CarShapeRecord(record, Path.Combine(Extracted!, NextShapeFile()));
        _pendingShapes[made.File] = record.ToBytes();
        Register(made);
        return made;
    }

    /// <summary>Puts a size into a record, in the numbers that shape states itself with.</summary>
    private static void Resize(
        RigidBodyElement rigid, CarCollisionShape shape, Vector3 half, float radius, float height)
    {
        rigid.BoxDimensions = shape == CarCollisionShape.Box ? half : default;
        rigid.Radius = shape == CarCollisionShape.Box ? 0f : radius;
        // The axis is the shape's local Z, which is where the height goes — measured over the 251 capsules of
        // the shipped cars, twice and by two independent readings.
        rigid.Height = shape is CarCollisionShape.Capsule or CarCollisionShape.Cylinder ? height : 0f;
    }

    /// <summary>A 64-bit id nothing in the archive is using yet.</summary>
    private static ulong MintHash(string seed, HashSet<ulong> taken)
    {
        for (int salt = 0; ; salt++)
        {
            ulong candidate = Fnv64.Hash(salt == 0
                ? seed
                : seed + "#" + salt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (candidate != 0 && taken.Add(candidate)) return candidate;
        }
    }

    /// <summary>A shape file name neither the manifest nor this car's pending writes are already using.</summary>
    private string NextShapeFile()
    {
        SdsManifest? manifest = null;
        try { manifest = SdsManifest.Load(Extracted!); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { /* the name still has to be free */ }

        for (int index = 0; ; index++)
        {
            string name = $"ItemDesc_{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}.ids";
            string path = Path.Combine(Extracted!, name);
            if (manifest?.HasFile(name) != true && !File.Exists(path) && !_pendingShapes.ContainsKey(path))
            {
                return name;
            }
        }
    }

    // ── the mirror stub ──

    /// <summary>
    /// Mints the frame-graph copy of a placement — the one a car does not read.
    ///
    /// <para>
    /// It is kept because an edited archive should stay the shape a shipped one is: 1097 of the shipped
    /// placed-shape volumes have exactly this second copy beside them, and 1060 of those agree to the byte.
    /// If the archive carries no collision frame to copy the wiring from, the volume is written on its own and
    /// nothing is said — a modder should never be refused an edit over a copy the game ignores.
    /// </para>
    /// </summary>
    private FrameObjectCollision? MintStub(CarComponent component, Matrix4x4 placement, ulong fileHash)
    {
        if (Frames?.FrameObjects == null || Model is not { } model) return null;
        if (component.BoneJoint < 0 || component.BoneJoint > byte.MaxValue) return null;
        FrameObjectCollision? donor = Donor(model);
        if (donor == null) return null;

        var stub = new FrameObjectCollision(Frames)
        {
            Name = new HashName(StubName(component)),
            Hash = fileHash,
            LocalTransform = placement,
        };
        stub.IsOnFrameTable = donor.IsOnFrameTable;
        stub.FrameNameTableFlags = donor.FrameNameTableFlags;

        // The wiring is the archive's own convention rather than an invention: ParentIndex1 cascades the
        // transform and ParentIndex2 anchors the frame to a scene, and getting the two the wrong way round is
        // what sends an attachment off to the model's origin.
        // Order -1: a frame that never existed joins at the end, which is where a new one belongs.
        PutFrame(new CarFrameState(
            stub, Present: true, placement, component.BoneJoint, donor.Parent, donor.Root,
            Order: -1, Attached: -1));
        return stub;
    }

    private static FrameObjectCollision? Donor(FrameObjectModel model)
    {
        foreach (FrameObjectModel.AttachmentReference reference in model.AttachmentReferences ?? [])
        {
            if (reference.Attachment is FrameObjectCollision onBone) return onBone;
        }
        return model.Resource.FrameObjects.Values.OfType<FrameObjectCollision>().FirstOrDefault();
    }

    /// <summary>A frame name the archive is not already using.</summary>
    private string StubName(CarComponent component)
    {
        string stem = string.IsNullOrWhiteSpace(component.Name) ? "part" : component.Name;
        for (int n = 0; ; n++)
        {
            string candidate = n == 0
                ? $"{stem}_Collision"
                : $"{stem}{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}_Collision";
            bool taken = Frames!.FrameObjects.Values.OfType<FrameObjectBase>()
                .Any(f => string.Equals(f.Name.ToString(), candidate, StringComparison.Ordinal));
            if (!taken) return candidate;
        }
    }

    /// <summary>
    /// Puts a frame into the graph exactly as the state describes it — and does so IDEMPOTENTLY, because a
    /// restore is run on frames that never left.
    ///
    /// <para>
    /// Both halves need saying. <c>AttachToJoint</c> appends an attachment reference without looking for one
    /// it already has, so re-attaching an attached frame gives the model two of them, and a few undo/redo
    /// cycles give it ten — a shape no shipped car has and one that grows for as long as the session lasts.
    /// And the two parent slots are set from the state rather than left alone, because taking a frame out
    /// clears them: one put back without them is anchored to no scene, which loads and is invisible.
    /// </para>
    /// </summary>
    private void PutFrame(CarFrameState state)
    {
        if (Frames?.FrameObjects == null || Model is not { } model) return;
        FrameObjectBase frame = state.Frame;
        frame.LocalTransform = state.Local;
        // A Dummy's own box is the other half of where a climb box is, and typing new corners resizes it.
        if (state.Bounds is { } box && frame is FrameObjectDummy dummy) dummy.Bounds = box;
        frame.SetParent(ParentInfo.ParentType.ParentIndex1, state.Parent);
        frame.SetParent(ParentInfo.ParentType.ParentIndex2, state.Root);
        if (!Frames.FrameObjects.ContainsKey(frame.RefID)) Reinsert(frame, state.Order);
        model.DetachFromJoints(frame);
        if (state.Joint >= 0 && state.Joint <= byte.MaxValue)
        {
            model.AttachToJoint(frame, (byte)state.Joint);
            Reorder(model, frame, state.Attached);
        }
        if (frame is FrameObjectCollision stub) _stubsByFile[stub.Hash] = stub;
        // A frame added to the graph is a frame the name table has to gain, or it loads and is invisible.
        TouchFrames(nameTable: true);
    }

    /// <summary>
    /// Puts a frame back where it stood among the graph's frames rather than at the end of them.
    ///
    /// <para>
    /// The frames are held in a dictionary and written in the order they were put into it, so re-adding one
    /// appends it — and every frame that used to come after it is written one place earlier. The archive is
    /// the same car and not the same bytes, which is exactly what an undo must not produce. Rebuilding the
    /// dictionary is the price; it is paid once, on an undo, over a few hundred frames.
    /// </para>
    /// </summary>
    private void Reinsert(FrameObjectBase frame, int order)
    {
        Dictionary<int, object> frames = Frames!.FrameObjects;
        if (order < 0 || order >= frames.Count) { frames.Add(frame.RefID, frame); return; }

        List<KeyValuePair<int, object>> had = [.. frames];
        had.Insert(order, new KeyValuePair<int, object>(frame.RefID, frame));
        frames.Clear();
        foreach ((int refId, object held) in had) frames.Add(refId, held);
    }

    /// <summary>Puts a re-attached frame back at the place its reference held in the model's attachment list,
    /// which <c>AttachToJoint</c> appends to — the same reason <see cref="Reinsert"/> exists.</summary>
    private static void Reorder(FrameObjectModel model, FrameObjectBase frame, int to)
    {
        FrameObjectModel.AttachmentReference[] refs = model.AttachmentReferences ?? [];
        int at = Array.FindIndex(refs, r => ReferenceEquals(r.Attachment, frame));
        if (at < 0 || to < 0 || to >= refs.Length || at == to) return;

        List<FrameObjectModel.AttachmentReference> had = [.. refs];
        FrameObjectModel.AttachmentReference one = had[at];
        had.RemoveAt(at);
        had.Insert(to, one);
        model.AttachmentReferences = [.. had];
    }

    private void DropFrame(FrameObjectBase frame)
    {
        if (Frames?.FrameObjects == null) return;
        Model?.DetachFromJoints(frame);
        frame.SetParent(ParentInfo.ParentType.ParentIndex1, null);
        frame.SetParent(ParentInfo.ParentType.ParentIndex2, null);
        Frames.FrameObjects.Remove(frame.RefID);
        // A stub the graph no longer holds must stop answering lookups, or the next edit finds a detached
        // frame and syncs a placement into nothing.
        if (frame is FrameObjectCollision stub
            && _stubsByFile.TryGetValue(stub.Hash, out FrameObjectCollision? found)
            && ReferenceEquals(found, stub))
        {
            _stubsByFile.Remove(stub.Hash);
        }
        TouchFrames(nameTable: true);
    }

    /// <summary>Where a frame sits among the graph's frames, or -1 when the graph does not hold it.</summary>
    private int OrderOf(FrameObjectBase frame)
    {
        if (Frames?.FrameObjects == null) return -1;
        int at = 0;
        foreach (int refId in Frames.FrameObjects.Keys)
        {
            if (refId == frame.RefID) return at;
            at++;
        }
        return -1;
    }

    /// <summary>Which joint a frame hangs off, so an undo can put it back on the same one.</summary>
    private int JointOf(FrameObjectBase frame)
    {
        foreach (FrameObjectModel.AttachmentReference reference in Model?.AttachmentReferences ?? [])
        {
            if (ReferenceEquals(reference.Attachment, frame)) return reference.JointIndex;
        }
        return -1;
    }

    /// <summary>Where its reference sits in the model's attachment list, or -1 when it is not attached.</summary>
    private int AttachedOf(FrameObjectBase frame) =>
        Array.FindIndex(Model?.AttachmentReferences ?? [], r => ReferenceEquals(r.Attachment, frame));

    // ── the hit boxes ──

    /// <summary>
    /// Recomputes the per-piece hit boxes of ONE component from its geometry, and leaves every other piece's
    /// box exactly as it was — bit for bit, including the opaque turn word no reading has cracked.
    ///
    /// <para>
    /// A hit box is what lets a piece of a car be shot: proven in game by changing nothing else, collapsing
    /// every box stops the whole car registering bullets and opening them wide makes geometry that had never
    /// taken a hit start taking them. It is derived from GEOMETRY, so whoever changes geometry owes the boxes
    /// that follow from it, and this is the scoped way to pay that debt.
    /// </para>
    /// <para>
    /// <b>A collision edit does not call this, deliberately.</b> Adding a volume changes no geometry, so the
    /// only thing a rebuild could write back is the standing disagreement between the toolkit's rule and what
    /// the car shipped with — the rule reproduces 65.3 % of the shipped boxes, and on the focus car an
    /// unscoped rebuild moves 90 of 187. Silently re-aiming a third of a car's bullet gating because somebody
    /// resized a box is not derived data being kept up to date; it is an edit nobody asked for. The scoping
    /// is what makes this safe to call when geometry really has changed, which is the bridge's business.
    /// </para>
    /// </summary>
    /// <returns>How many boxes actually moved.</returns>
    public int RebuildHitBoxes(CarComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (Model is not { } model) return 0;
        FrameObjectModel.HitBoxInfo[] had = model.HitBoxes ?? [];
        if (had.Length == 0) return 0;

        FrameObjectModel.HitBoxInfo[]? built = HitBoxBuilder.Compute(model);
        if (built == null || built.Length != had.Length) return 0;

        var mine = new bool[had.Length];
        if (!PiecesOf(component, mine)) return 0;

        int moved = 0;
        for (int i = 0; i < had.Length; i++)
        {
            if (!mine[i] || Same(had[i], built[i])) continue;
            had[i] = built[i];
            moved++;
        }
        if (moved == 0) return 0;

        model.HitBoxes = had;
        model.RecomputeSplitCounters();
        TouchFrames();
        return moved;
    }

    /// <summary>
    /// Marks the flat piece ordinals belonging to a component's bone, in the split-then-piece order the boxes
    /// are stored in.
    ///
    /// <para>
    /// A split's bone is <c>BoneRemapIDs[BlendIndex]</c> through LOD 0's own remap table — reading the raw
    /// blend index as a bone id is right 2.2 % of the time.
    /// </para>
    /// </summary>
    /// <returns>False when the model cannot say which piece belongs to whom, in which case nothing moves.</returns>
    private bool PiecesOf(CarComponent component, bool[] mine)
    {
        if (Model is not { } model) return false;
        FrameObjectModel.WeightedByMeshSplit[] splits = model.BlendMeshSplits ?? [];
        if (splits.Length == 0) return false;

        byte[] remap;
        try
        {
            Formats.Frames.Resources.FrameBlendInfo.BoneIndexInfo[] levels =
                model.GetBlendInfoObject().BoneIndexInfos ?? [];
            remap = levels.Length > 0 ? levels[0].BoneRemapIDs ?? [] : [];
        }
        catch (Exception) { return false; }

        string[] bones;
        try { bones = [.. (model.GetSkeletonObject().BoneNames ?? []).Select(b => b.ToString() ?? "")]; }
        catch (Exception) { return false; }

        int at = 0;
        bool any = false;
        foreach (FrameObjectModel.WeightedByMeshSplit split in splits)
        {
            int bone = split.BlendIndex < remap.Length ? remap[split.BlendIndex] : -1;
            bool ours = bone >= 0 && bone < bones.Length && bones[bone].Length > 0
                && Fnv64.Hash(bones[bone]) == component.BoneHash;
            foreach (FrameObjectModel.BlendMeshSplitInfo _ in split.Data ?? [])
            {
                if (at >= mine.Length) return any;
                if (ours) { mine[at] = true; any = true; }
                at++;
            }
        }
        return any;
    }

    private static bool Same(FrameObjectModel.HitBoxInfo a, FrameObjectModel.HitBoxInfo b) =>
        a.Position.S1 == b.Position.S1 && a.Position.S2 == b.Position.S2 && a.Position.S3 == b.Position.S3
        && a.Size.S1 == b.Size.S1 && a.Size.S2 == b.Size.S2 && a.Size.S3 == b.Size.S3;

    // ── snapshots ──

    /// <summary>
    /// Everything the named structures hold right now, as bytes — see <see cref="CarState"/> for why an undo is
    /// this rather than a reversed derivation.
    /// </summary>
    /// <param name="boxes">Whether the model's per-piece hit boxes are part of this state. False for an intent
    /// that cannot have changed one, and that is not a nicety: restoring a state that carries them writes them
    /// back over the live model and marks the FRAME GRAPH dirty, so an undo of a mass would demand — and get —
    /// a rewritten frame resource for an edit that never touched geometry.</param>
    private CarState Snapshot(
        IReadOnlyList<string> shapeFiles, IReadOnlyList<FrameObjectBase> frames, bool boxes = true)
    {
        var shapes = new List<(string, byte[]?)>(shapeFiles.Count);
        foreach (string path in shapeFiles) shapes.Add((path, ShapeBytes(path)));

        var placed = new List<CarFrameState>(frames.Count);
        foreach (FrameObjectBase frame in frames)
        {
            placed.Add(new CarFrameState(
                frame, Frames?.FrameObjects?.ContainsKey(frame.RefID) == true, frame.LocalTransform,
                JointOf(frame), frame.Parent, frame.Root, OrderOf(frame), AttachedOf(frame),
                frame is FrameObjectDummy dummy ? dummy.Bounds : null));
        }

        return new CarState(Prefab.ToBytes(), shapes, placed, boxes ? CopyBoxes() : null);
    }

    /// <summary>The model's hit boxes as they stand, copied rather than referenced — the array is rewritten
    /// in place, so a snapshot holding the same instance would follow the edit it is meant to undo.</summary>
    private FrameObjectModel.HitBoxInfo[]? CopyBoxes() =>
        Model?.HitBoxes is { Length: > 0 } had ? Copy(had) : null;

    private static FrameObjectModel.HitBoxInfo[] Copy(FrameObjectModel.HitBoxInfo[] had)
    {
        var boxes = new FrameObjectModel.HitBoxInfo[had.Length];
        for (int i = 0; i < had.Length; i++) boxes[i] = new FrameObjectModel.HitBoxInfo(had[i]);
        return boxes;
    }

    /// <summary>What a shape file holds now — the write this car has queued for it, or the file on disk, or
    /// null when there is neither and the undo of this intent has to take the file away.</summary>
    private byte[]? ShapeBytes(string path)
    {
        if (_pendingShapes.TryGetValue(path, out byte[]? queued)) return queued;
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
