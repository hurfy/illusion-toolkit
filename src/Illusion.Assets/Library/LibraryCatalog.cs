namespace Illusion.Assets.Library;

/// <summary>
/// The content browser's index over the game's archives. Built from directory listings alone — no archive is
/// opened, extracted or parsed, so the whole catalog costs one walk of <c>pc\sds</c> and can be rebuilt at any
/// time. The library is an INDEX, never a store: every card resolves back to the .sds it came from, and edits
/// land there through the ordinary extracted → Save → Build path.
///
/// The tree the browser shows has two halves: the curated categories (Cars, Characters, City objects), which
/// are entry points rather than folders on disk, and "All archives" — the real <c>sds\</c> folder tree, so
/// everything is reachable from day one even before a category exists for it.
/// </summary>
public sealed class LibraryCatalog
{
    /// <summary>Top-level browser folders: the categories first, "All archives" last.</summary>
    public IReadOnlyList<LibraryFolder> Roots { get; }

    /// <summary>Every archive in the game, flat — what the browser's search box queries. Each archive appears
    /// once here however many categories point at its folder.</summary>
    public IReadOnlyList<LibraryEntry> AllEntries { get; }

    private LibraryCatalog(IReadOnlyList<LibraryFolder> roots, IReadOnlyList<LibraryEntry> all)
    {
        Roots = roots;
        AllEntries = all;
    }

    /// <summary>
    /// The v1 categories. Each names the real <c>sds\</c> folders it gathers, in the order they should read.
    /// A category over one folder shows that folder's archives directly; over several it keeps them as
    /// sub-branches — the folders are separate archives, not variants of each other, and merging them would
    /// invent a relationship the game does not have.
    ///
    /// <c>traffic\</c> sits under Characters, not Cars: despite the name it holds the street population
    /// (barmen, guards, prostitutes, bikers, mission extras — <c>m14barman</c>, <c>cguard1</c>,
    /// <c>m03_prostitute</c>, <c>driver</c>), not traffic vehicles. Every drivable car, truck, train and
    /// wagon lives in <c>cars\</c>.
    /// </summary>
    private static readonly (string Name, string[] Folders)[] Categories =
    {
        ("Cars", new[] { "cars" }),
        ("Characters", new[] { "hchar", "player", "police_char", "wardrobe", "traffic" }),
        ("City objects", new[] { "city_crash" }),
    };

    /// <summary>
    /// Folders that are working state rather than game content, and must never show up as browsable content:
    /// our own extracted mirror and versioned backups, plus the <c>BackupSDS\</c> that the old MafiaToolkit
    /// leaves behind. They sit right beside the archives they shadow, and hold .sds copies of their own — so
    /// listing them would offer the same car twice, one of the two an old backup.
    /// </summary>
    private static readonly string[] WorkFolders = { "extracted", "backups", "BackupSDS" };

    /// <summary>
    /// Walks <paramref name="sdsFolder"/> (the game's <c>pc\sds</c>) and builds the browser tree. A missing
    /// folder yields an empty catalog rather than throwing — the launcher already refuses an install without
    /// one, and the browser has to survive being built before a game path is picked.
    /// </summary>
    public static LibraryCatalog Build(string sdsFolder)
    {
        var all = new List<LibraryEntry>();
        LibraryFolder? tree = Directory.Exists(sdsFolder)
            ? Scan(new DirectoryInfo(sdsFolder), "sds", all)
            : null;

        var roots = new List<LibraryFolder>();
        if (tree != null)
        {
            var byName = new Dictionary<string, LibraryFolder>(StringComparer.OrdinalIgnoreCase);
            foreach (LibraryFolder f in tree.Folders) byName[f.Name] = f;

            foreach ((string name, string[] folders) in Categories)
            {
                LibraryFolder? category = BuildCategory(name, folders, byName);
                if (category != null) roots.Add(category);
            }

            // The raw folder tree, kept whole so nothing in the game is out of reach — its name is the only
            // thing the category half changes about it.
            roots.Add(new LibraryFolder
            {
                Name = "All archives",
                Kind = LibraryFolderKind.Directory,
                Path = "sds",
                FolderList = tree.FolderList,
                EntryList = tree.EntryList,
                TotalEntries = tree.TotalEntries,
            });
        }

        all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return new LibraryCatalog(roots, all);
    }

    /// <summary>Finds the archive card for a file already on disk (the map → library jump, and reselecting a
    /// staged archive after a rebuild). Matched by full path, so a backup copy of the same name never wins.</summary>
    public LibraryEntry? Find(FileInfo file)
    {
        foreach (LibraryEntry e in AllEntries)
            if (string.Equals(e.File.FullName, file.FullName, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    // One category: the folders it gathers, in the order the table names them. A single source folder is shown
    // flattened (the category IS that folder, renamed); several stay as sub-branches. Missing folders — a
    // stripped or modded install — are skipped, and a category left with nothing is dropped by the caller.
    private static LibraryFolder? BuildCategory(string name, string[] folders,
        Dictionary<string, LibraryFolder> byName)
    {
        var sources = new List<LibraryFolder>();
        foreach (string f in folders)
            if (byName.TryGetValue(f, out LibraryFolder? found)) sources.Add(found);
        if (sources.Count == 0) return null;

        var category = new LibraryFolder
        {
            Name = name,
            Kind = LibraryFolderKind.Category,
            Path = name,
        };

        if (sources.Count == 1)
        {
            category.FolderList.AddRange(sources[0].Folders);
            category.EntryList.AddRange(sources[0].Entries);
        }
        else
        {
            category.FolderList.AddRange(sources);
        }

        foreach (LibraryFolder s in sources) category.TotalEntries += s.TotalEntries;
        return category;
    }

    // Recursive listing. A folder survives only if it holds an archive somewhere below it: the sds tree also
    // carries pure sound / text / video folders with no .sds at all, and a browser row that opens onto nothing
    // is worse than no row.
    private static LibraryFolder? Scan(DirectoryInfo dir, string path, List<LibraryEntry> all)
    {
        var folder = new LibraryFolder
        {
            Name = dir.Name,
            Kind = LibraryFolderKind.Directory,
            Path = path,
        };

        foreach (FileInfo f in Enumerate(dir))
        {
            var entry = new LibraryEntry
            {
                Name = Path.GetFileNameWithoutExtension(f.Name),
                File = f,
                Size = f.Length,
                FolderPath = path,
            };
            folder.EntryList.Add(entry);
            all.Add(entry);
        }
        folder.EntryList.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        foreach (DirectoryInfo sub in EnumerateFolders(dir))
        {
            LibraryFolder? child = Scan(sub, path + "/" + sub.Name, all);
            if (child != null) folder.FolderList.Add(child);
        }

        folder.TotalEntries = folder.EntryList.Count;
        foreach (LibraryFolder c in folder.Folders) folder.TotalEntries += c.TotalEntries;
        return folder.TotalEntries > 0 ? folder : null;
    }

    private static FileInfo[] Enumerate(DirectoryInfo dir)
    {
        try { return dir.GetFiles("*.sds"); }
        catch (Exception) { return Array.Empty<FileInfo>(); } // unreadable folder: skip it, never fail the walk
    }

    private static IEnumerable<DirectoryInfo> EnumerateFolders(DirectoryInfo dir)
    {
        DirectoryInfo[] subs;
        try { subs = dir.GetDirectories(); }
        catch (Exception) { yield break; }

        foreach (DirectoryInfo sub in subs)
        {
            if (WorkFolders.Contains(sub.Name, StringComparer.OrdinalIgnoreCase)) continue;
            yield return sub;
        }
    }
}
