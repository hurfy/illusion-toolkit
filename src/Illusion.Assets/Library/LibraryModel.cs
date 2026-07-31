namespace Illusion.Assets.Library;

/// <summary>What a folder in the content browser stands for.</summary>
public enum LibraryFolderKind
{
    /// <summary>A curated entry point that is no folder on disk — Cars, Characters, City objects.</summary>
    Category,

    /// <summary>A real folder under the game's <c>pc\sds</c>.</summary>
    Directory,
}

/// <summary>
/// One archive card: an <c>.sds</c> the browser can put on the stage. Carries only what a folder listing
/// gives — nothing here is parsed or extracted, so building the whole catalog stays a directory walk.
/// </summary>
public sealed class LibraryEntry
{
    /// <summary>Archive file name without the extension — what the card shows.</summary>
    public required string Name { get; init; }

    public required FileInfo File { get; init; }

    /// <summary>Archive size on disk, in bytes.</summary>
    public required long Size { get; init; }

    /// <summary>Slash-separated folder this archive lives in (<c>sds/cars</c>) — the subtitle of a search hit,
    /// which is otherwise a bare name with no clue where it came from.</summary>
    public required string FolderPath { get; init; }
}

/// <summary>
/// A branch of the browser's folder tree: either a real <c>sds\</c> folder or one of the curated categories.
/// Both kinds hold the same thing — sub-folders and archive cards — so the tree needs no special casing.
/// </summary>
public sealed class LibraryFolder
{
    public required string Name { get; init; }

    public required LibraryFolderKind Kind { get; init; }

    /// <summary>Slash-separated path for a <see cref="LibraryFolderKind.Directory"/>; the name alone for a
    /// category (it has no path — it gathers folders from elsewhere in the tree).</summary>
    public required string Path { get; init; }

    public IReadOnlyList<LibraryFolder> Folders => FolderList;

    public IReadOnlyList<LibraryEntry> Entries => EntryList;

    /// <summary>Archives in this folder and everything under it — the count the folder row shows, so a
    /// collapsed branch still says how much is inside.</summary>
    public int TotalEntries { get; internal set; }

    internal List<LibraryFolder> FolderList { get; init; } = new();

    internal List<LibraryEntry> EntryList { get; init; } = new();
}
