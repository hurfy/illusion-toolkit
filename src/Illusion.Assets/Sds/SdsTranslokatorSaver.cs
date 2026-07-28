using Illusion.Formats.Archive;
using Illusion.Formats.Translokator;

namespace Illusion.Assets.Sds;

/// <summary>
/// Writes an edited <see cref="TranslokatorLoader"/> back over the <c>Translokator</c> (.tra) resource inside an
/// SDS's extracted working folder — the crash-placement analog of <see cref="SdsCollisionSaver"/>. Placements the
/// user did not touch are re-quantized to the very same bytes they were read from, so an edited table differs
/// from the shipped one only where it was edited. Pack the result back into the <c>.sds</c> with
/// <see cref="SdsWriter.PackSds(System.IO.FileInfo, bool)"/>, exactly like frame and collision edits.
/// </summary>
public static class SdsTranslokatorSaver
{
    /// <summary>
    /// Re-serializes <paramref name="table"/> over the <c>Translokator</c> resource file in
    /// <paramref name="sds"/>'s extracted folder. Returns the file written.
    /// </summary>
    public static string SaveWorkingCopy(TranslokatorLoader table, FileInfo sds)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(sds);
        return SaveToExtracted(table, MafiaEnvironment.ExtractedDir(sds), sds.Name);
    }

    /// <summary>
    /// Writes <paramref name="table"/> over the <c>Translokator</c> resource file inside an already-extracted SDS
    /// folder (serialize to memory, then temp→move, so a mid-write failure never truncates the only working copy).
    /// Public so the probes can exercise it against a throwaway folder without touching a live working copy.
    /// </summary>
    public static string SaveToExtracted(TranslokatorLoader table, string extractedDir, string sdsLabel = "archive")
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!File.Exists(Path.Combine(extractedDir, "SDSContent.xml")))
            throw new FileNotFoundException(
                $"Extracted SDS content not found for {sdsLabel} — the archive must be opened before it can be saved.",
                Path.Combine(extractedDir, "SDSContent.xml"));

        string? file = ResolvePath(extractedDir);
        if (file == null)
            throw new InvalidOperationException($"{sdsLabel} has no Translokator resource to write.");

        byte[] bytes = table.ToBytes();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        AtomicFile.WriteAllBytes(file, bytes);
        return file;
    }

    /// <summary>
    /// The <c>.tra</c> inside an extracted folder, resolved through the SDS manifest so reading and writing can
    /// never disagree about which resource they mean, or null when the archive carries none.
    /// </summary>
    public static string? ResolvePath(string extractedDir)
    {
        try
        {
            return SdsManifest.Load(extractedDir).GetFiles("Translokator").FirstOrDefault(File.Exists);
        }
        catch
        {
            return null; // a malformed manifest is not a save target; the caller treats it as "no table"
        }
    }
}
