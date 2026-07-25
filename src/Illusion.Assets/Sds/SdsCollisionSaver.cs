using Illusion.Formats.Archive;
using Illusion.Formats.Collisions;

namespace Illusion.Assets.Sds;

/// <summary>
/// Writes an edited <see cref="CollisionFile"/> back over the <c>Collisions</c> (.col) resource inside an SDS's
/// extracted working folder — the collision analog of <see cref="SdsWriter.SaveFrameResource"/>. Phase 2 edits
/// only the instance placements; the cooked PhysX mesh blobs are re-emitted verbatim by
/// <see cref="CollisionFile.ToBytes"/>, so everything the user did not touch round-trips byte-for-byte. Pack the
/// result back into the <c>.sds</c> with <see cref="SdsWriter.PackSds(System.IO.FileInfo, bool)"/>, exactly like frame edits.
/// </summary>
public static class SdsCollisionSaver
{
    /// <summary>
    /// Re-serializes <paramref name="collision"/> over the <c>Collisions</c> resource file in
    /// <paramref name="sds"/>'s extracted folder. Returns the file written.
    /// </summary>
    public static string SaveWorkingCopy(CollisionFile collision, FileInfo sds)
    {
        ArgumentNullException.ThrowIfNull(collision);
        ArgumentNullException.ThrowIfNull(sds);
        return SaveToExtracted(collision, MafiaEnvironment.ExtractedDir(sds), sds.Name);
    }

    /// <summary>
    /// Writes <paramref name="collision"/> over the <c>Collisions</c> resource file inside an already-extracted
    /// SDS folder (serialize to memory, then temp→move so a mid-write failure never truncates the only working
    /// copy — the <see cref="SdsWriter.SaveFrameResource"/> pattern). Public so the save probe can exercise it
    /// against a throwaway folder without touching a live working copy.
    /// </summary>
    public static string SaveToExtracted(CollisionFile collision, string extractedDir, string sdsLabel = "archive")
    {
        ArgumentNullException.ThrowIfNull(collision);
        if (!File.Exists(Path.Combine(extractedDir, "SDSContent.xml")))
            throw new FileNotFoundException(
                $"Extracted SDS content not found for {sdsLabel} — the archive must be opened before it can be saved.",
                Path.Combine(extractedDir, "SDSContent.xml"));

        IReadOnlyList<string> files =
            SdsManifest.Load(extractedDir).GetFiles("Collisions");
        if (files.Count == 0)
            throw new InvalidOperationException($"{sdsLabel} has no Collisions resource to write.");

        byte[] bytes = collision.ToBytes();
        Directory.CreateDirectory(Path.GetDirectoryName(files[0])!);
        AtomicFile.WriteAllBytes(files[0], bytes);
        return files[0];
    }
}
