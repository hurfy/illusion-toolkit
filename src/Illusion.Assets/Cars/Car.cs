using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Cars;

/// <summary>
/// A car, as the thing a modder authored rather than as the files it is scattered over — and the ONE seam
/// between a component-level intent and bytes.
///
/// <para>
/// One door is written down five times, in four parallel lists that share nothing but an FNV64 hash: a
/// deform part says it crumples and names its bone, a separate row says where its handle and lock are, a
/// seat row says you get in through it, its window is a deform part of its own pointing back at it, and a
/// climb box may name its bone. Nothing in the format ties those together, so the toolkit has to — and it
/// has to do it in exactly one place, or the tree, the viewport and the bridge each grow their own path to
/// the file and drift apart.
/// </para>
/// <para>
/// This is the READING half. It stitches the lists into components, hangs the markers off them, and returns
/// what it could not stitch as <see cref="Faults"/> rather than dropping it. It writes nothing.
/// </para>
/// <para>
/// The structures it read are kept as they were read. When the writing half lands it patches THOSE — it
/// never rebuilds an existing deform part from its component, because a large part of that struct is
/// undecoded and has to reach the game untouched.
/// </para>
/// </summary>
public sealed partial class Car
{
    private readonly Dictionary<ulong, CarComponent> _byBone;
    private readonly Dictionary<long, CarComponent> _byId;
    private readonly Dictionary<long, ComponentId> _byAnchor;

    private Car(
        PrefabFile prefab, FrameResource? frames, string? prefabPath, string? extracted, int lod,
        List<CarComponent> components, List<CarComponent> roots, CarComponent? body,
        List<CarMarker> markers, List<CarFault> faults,
        Dictionary<ulong, CarComponent> byBone, Dictionary<long, ComponentId> byAnchor)
    {
        Prefab = prefab;
        Frames = frames;
        PrefabPath = prefabPath;
        Extracted = extracted;
        Lod = lod;
        Components = components;
        Roots = roots;
        Body = body;
        Markers = markers;
        Faults = faults;
        _byBone = byBone;
        _byAnchor = byAnchor;
        _byId = components.ToDictionary(c => c.Id.Value);
    }

    /// <summary>
    /// The prefab as it was read. The writing half patches this; nothing else may.
    ///
    /// <para>
    /// Replaced whole in exactly one place: restoring an undo SNAPSHOT, which puts back the bytes the prefab
    /// held rather than running a derivation backwards.
    /// </para>
    /// </summary>
    public PrefabFile Prefab { get; private set; }

    /// <summary>The frame graph as it was read, or null when the archive carries none.</summary>
    public FrameResource? Frames { get; }

    /// <summary>Where the prefab came from, when it came from disk.</summary>
    public string? PrefabPath { get; }

    /// <summary>
    /// The archive's extracted working copy this car was read out of, or null when it was stitched in memory
    /// — which is what a bridge push does. It is where <see cref="Save"/> writes, and the reason a car
    /// stitched in memory has nothing to write to.
    /// </summary>
    public string? Extracted { get; }

    /// <summary>
    /// Which level of detail the components were measured at. A component's EXISTENCE is LOD-scoped — 4882
    /// of 5046 bones that carry geometry at LOD 0 carry none at LOD 1 — while its identity is not, so the
    /// same component keeps the same <see cref="ComponentId"/> across a switch.
    /// </summary>
    public int Lod { get; }

    /// <summary>Every component, deform parts first in file order, then the bare ones in rig order.</summary>
    public IReadOnlyList<CarComponent> Components { get; }

    /// <summary>The components nothing hangs off — the top of the tree.</summary>
    public IReadOnlyList<CarComponent> Roots { get; }

    /// <summary>
    /// The body: the component on the bone the prefab names as its SCALE BONE.
    ///
    /// <para>
    /// Matched by hash and never by string — the bone ships under four different casings (<c>scale bone</c>,
    /// <c>Scale bone</c>, <c>Scale_bone</c>, <c>Scale Bone</c>), so a spelling would find three quarters of
    /// the corpus and quietly lose the rest.
    /// </para>
    /// <para>
    /// It is where every marker with no component of its own goes, so it is load-bearing rather than
    /// decorative. Present on 85 of 85 shipped cars; null is a <see cref="CarFaultKind.NoBody"/> fault.
    /// </para>
    /// </summary>
    public CarComponent? Body { get; }

    /// <summary>Every marker of the car, flat and in list order. Each also hangs off its own component.</summary>
    public IReadOnlyList<CarMarker> Markers { get; }

    /// <summary>What could not be stitched. Empty on a car the toolkit understands completely.</summary>
    public IReadOnlyList<CarFault> Faults { get; }

    /// <summary>Which component a bone belongs to — the lookup the viewport and the bridge resolve through.
    /// Unambiguous on 85 of 85 cars, so it is a straight lookup and not a disambiguation.</summary>
    public CarComponent? ComponentOfBone(ulong boneHash) =>
        boneHash != 0 && _byBone.TryGetValue(boneHash, out CarComponent? found) ? found : null;

    /// <summary>The component an identity names, or null when this car has no such component.</summary>
    public CarComponent? ComponentById(ComponentId id) =>
        id.IsSet && _byId.TryGetValue(id.Value, out CarComponent? found) ? found : null;

    /// <summary>
    /// Reads a car out of an archive's working copy.
    /// </summary>
    /// <param name="archive">The .sds. Its extracted folder is read, never the packed file.</param>
    /// <param name="lod">Which level to measure geometry at.</param>
    /// <param name="previous">The same car as it was stitched before, when there is one. Identities are
    /// carried across from it, which is what makes them survive a rename and a bridge push.</param>
    /// <returns>Null when the archive carries no car prefab — which is most archives.</returns>
    public static Car? Read(FileInfo archive, int lod = 0, Car? previous = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return ReadFrom(MafiaEnvironment.ExtractedDir(archive), lod, previous);
    }

    /// <summary>The same, from a working copy that is not the archive's own — what the regression harness
    /// reads, so an edit it makes never lands in the player's install.</summary>
    public static Car? ReadFrom(string extracted, int lod = 0, Car? previous = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(extracted);

        IReadOnlyList<string> files;
        try { files = SdsManifest.Load(extracted).GetFiles("PREFAB"); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { return null; }

        foreach (string file in files)
        {
            PrefabFile prefab;
            try { prefab = PrefabFile.Load(file); }
            catch (Exception ex) when (ex is IOException or SdsFormatException) { continue; }
            if (prefab.Car == null) continue;

            FrameResource? frames = null;
            try { frames = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception ex) when (ex is IOException or SdsFormatException)
            {
                // A car whose frame resource cannot be opened still stitches: every part becomes a component
                // whose bone does not resolve, which is a diagnosis rather than a crash.
            }
            return Stitch(prefab, frames, lod, previous, file, extracted);
        }
        return null;
    }

    /// <summary>
    /// Reads the car of an archive that is already OPEN — the one the editor stages, stitched against the
    /// frame graph the viewport is drawing rather than against a second copy read off disk.
    ///
    /// <para>
    /// Which matters twice. A bone renamed or a frame minted this session exists only in that graph, so a car
    /// read from the file would show a component the modder has already changed the name of; and holding a
    /// second frame resource for a car the editor already has open is megabytes of duplicate for a tree.
    /// The prefab itself IS read from the working copy, because that is where a prefab edit lands the moment
    /// it is made — the same rule the property panel's own read follows.
    /// </para>
    /// </summary>
    /// <param name="document">The staged frame document. A document this layer does not recognise falls back
    /// to <see cref="Read(FileInfo, int, Car?)"/>, which reads both halves off disk.</param>
    /// <returns>
    /// Null when the archive carries no car prefab — which is most archives.
    /// <para>
    /// A read that FAILS throws rather than returning null, which is the difference between "this is not a
    /// car" and "I could not tell". The caller shows a hierarchy either way, but only the first of those is
    /// an answer worth remembering: memoing a working copy that was locked for a moment would leave the
    /// archive with no components for the rest of the session.
    /// </para>
    /// </returns>
    public static Car? ReadStaged(ISceneDocument document, int lod = 0, Car? previous = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document is not SceneDocumentAdapter staged) return Read(document.SourceArchive, lod, previous);

        string extracted = MafiaEnvironment.ExtractedDir(document.SourceArchive);
        foreach (string file in SdsManifest.Load(extracted).GetFiles("PREFAB"))
        {
            PrefabFile prefab = PrefabFile.Load(file);
            if (prefab.Car == null) continue;
            return Stitch(prefab, staged.Frame, lod, previous, file, extracted);
        }
        return null;
    }
}
