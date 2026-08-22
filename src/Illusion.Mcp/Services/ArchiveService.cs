using Illusion.Formats.Archive;

namespace Illusion.Mcp.Services;

/// <summary>One archive the service is holding, with the extraction names resolved alongside it.</summary>
public sealed class CachedArchive
{
    /// <summary>Full path the archive was read from.</summary>
    public required string Path { get; init; }

    public required SdsArchive Archive { get; init; }

    /// <summary>Extraction names, one per <see cref="SdsArchive.Entries"/> slot — resolved once at
    /// load, because resolving them mutates a CrySDS-locked archive (see
    /// <see cref="SdsArchive.ResolveEntryNames"/>) and is not something to repeat per tool call.</summary>
    public required IReadOnlyList<string> EntryNames { get; init; }

    /// <summary>Size of the file on disk, as read.</summary>
    public required long FileSize { get; init; }

    /// <summary>Write stamp the cached copy was read at — how a stale entry is recognized.</summary>
    public required DateTime WriteTimeUtc { get; init; }

    /// <summary>Decompressed payload bytes this entry is holding; what the budget is spent on.</summary>
    public required long Footprint { get; init; }

    internal DateTime LastAccessedUtc { get; set; }
}

/// <summary>
/// Opens SDS archives for the browsing tools and keeps them around, because a model exploring one
/// archive calls five or six tools against it in a row and decompressing a district archive on each
/// of them would be the whole cost of the session.
/// <para>
/// A cached archive is dropped when the file changes underneath it. That matters here in a way it
/// would not in a standalone server: the editor writes archives from this same process, so a model
/// browsing an archive the user just saved would otherwise be served the copy from before the save.
/// Size and write stamp are compared on every open — cheap next to a decompress.
/// </para>
/// <para>
/// The service is a singleton in the server's DI container and tools take it as a plain parameter,
/// which the SDK binds and hides from the schema. It holds no UI state and no scene state, so it
/// needs no <see cref="IUiThreadMarshal"/> hop: a tool call can read it straight off the Kestrel
/// thread it arrived on.
/// </para>
/// </summary>
public sealed class ArchiveService
{
    /// <summary>Decompressed bytes the cache may hold before it starts evicting.</summary>
    private const long BudgetBytes = 512L * 1024 * 1024;

    /// <summary>How long an untouched archive stays. A modding session moves between archives; the
    /// one opened half an hour ago is almost certainly finished with.</summary>
    private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, CachedArchive> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>
    /// The archive at <paramref name="path"/>, from the cache when it is still current. Throws what
    /// the format layer throws — a missing file, a console archive, a corrupt block table — and the
    /// tool turns that into the failure payload.
    /// </summary>
    public CachedArchive Open(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"no such file: {full}", full);
        }

        lock (_gate)
        {
            Sweep();
            if (_cache.TryGetValue(full, out CachedArchive? hit)
                && hit.FileSize == info.Length
                && hit.WriteTimeUtc == info.LastWriteTimeUtc)
            {
                hit.LastAccessedUtc = DateTime.UtcNow;
                return hit;
            }
        }

        // Decompressing outside the lock: a district archive takes seconds, and holding the gate for
        // that would serialize every other tool call in the process behind it. Two threads racing on
        // the same path both do the work and the second insert wins — wasteful once, never wrong.
        SdsArchive archive = SdsArchive.Open(full);
        List<string> names = archive.ResolveEntryNames();
        long footprint = 0;
        foreach (ResourceEntry entry in archive.Entries)
        {
            footprint += entry.Data?.Length ?? 0;
        }

        var cached = new CachedArchive
        {
            Path = full,
            Archive = archive,
            EntryNames = names,
            FileSize = info.Length,
            WriteTimeUtc = info.LastWriteTimeUtc,
            Footprint = footprint,
            LastAccessedUtc = DateTime.UtcNow,
        };

        lock (_gate)
        {
            _cache[full] = cached;
            Evict();
        }
        return cached;
    }

    /// <summary>Drops one archive. True when something was actually cached under that path.</summary>
    public bool Close(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        lock (_gate)
        {
            return _cache.Remove(full);
        }
    }

    /// <summary>What the cache is currently holding — path, footprint and last use, newest first.</summary>
    public IReadOnlyList<(string Path, long Footprint, DateTime LastAccessedUtc)> Snapshot()
    {
        lock (_gate)
        {
            return _cache.Values
                .OrderByDescending(c => c.LastAccessedUtc)
                .Select(c => (c.Path, c.Footprint, c.LastAccessedUtc))
                .ToList();
        }
    }

    /// <summary>Total decompressed bytes held. Caller must hold <see cref="_gate"/>.</summary>
    private long Held()
    {
        long total = 0;
        foreach (CachedArchive cached in _cache.Values)
        {
            total += cached.Footprint;
        }
        return total;
    }

    /// <summary>Drops anything untouched for longer than <see cref="Expiry"/>.</summary>
    private void Sweep()
    {
        DateTime cutoff = DateTime.UtcNow - Expiry;
        List<string>? stale = null;
        foreach (KeyValuePair<string, CachedArchive> pair in _cache)
        {
            if (pair.Value.LastAccessedUtc < cutoff)
            {
                (stale ??= []).Add(pair.Key);
            }
        }
        foreach (string key in stale ?? [])
        {
            _cache.Remove(key);
        }
    }

    /// <summary>Evicts least-recently-used archives until the cache is back inside its budget. The
    /// archive just inserted can itself be evicted — a single archive larger than the whole budget
    /// is served to its caller and then dropped, rather than pinning the process at that size.</summary>
    private void Evict()
    {
        if (Held() <= BudgetBytes)
        {
            return;
        }

        foreach (CachedArchive victim in _cache.Values.OrderBy(c => c.LastAccessedUtc).ToList())
        {
            _cache.Remove(victim.Path);
            if (Held() <= BudgetBytes)
            {
                return;
            }
        }
    }
}
