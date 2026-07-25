namespace Illusion.Assets;

/// <summary>
/// Atomic file replacement: write to a sibling temp file, then move it over the target. A crash mid-write
/// can never leave a truncated file behind — the target is either the old content or the new one. Every
/// saver that rewrites a working-copy file goes through here so the strategy lives in one place.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
