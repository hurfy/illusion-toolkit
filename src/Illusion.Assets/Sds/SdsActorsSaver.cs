using Illusion.Assets.Actors;
using Illusion.Formats.Actors;

namespace Illusion.Assets.Sds;

/// <summary>
/// Writes edited actor packs back over the <c>Actors</c> (.act) resources inside an SDS's extracted working
/// folder — the actor analog of <see cref="SdsTranslokatorSaver"/>. Only the transform of an actor is editable
/// so far, and that is a run of fixed-size fields: every other actor, every offset table and every capsule
/// re-emits byte for byte, so an edited pack differs from the shipped one only where it was edited. Pack the
/// result back into the <c>.sds</c> with <see cref="SdsWriter.PackSds(System.IO.FileInfo, bool)"/>, exactly like
/// frame and collision edits.
/// </summary>
public static class SdsActorsSaver
{
    /// <summary>
    /// Re-serializes every pack of <paramref name="placements"/> over the file it was read from. Returns the
    /// files written (empty when the placements carry no file origin, as the probes' do).
    /// </summary>
    public static IReadOnlyList<string> SaveWorkingCopy(ActorPlacements placements)
    {
        ArgumentNullException.ThrowIfNull(placements);

        placements.RefreshFrameIndices();

        var written = new List<string>();
        foreach ((ActorsFile pack, string path) in placements.Packs)
        {
            written.Add(Save(pack, path));
        }
        return written;
    }

    /// <summary>
    /// Writes one pack over <paramref name="path"/> (serialize to memory, then temp→move, so a mid-write
    /// failure never truncates the only working copy). Public so the probes can exercise it against a
    /// throwaway file without touching a live working copy.
    /// </summary>
    public static string Save(ActorsFile pack, string path)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentException.ThrowIfNullOrEmpty(path);

        byte[] bytes = pack.ToBytes();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllBytes(path, bytes);
        return path;
    }
}
