namespace Illusion.Assets.Library;

/// <summary>
/// What an archive holds. Mafia II files its archives by content — <c>cars\</c> is cars, <c>fmv\</c> is video,
/// <c>hchar\</c> is people — so the folder an <c>.sds</c> sits in IS its type, and classifying one costs
/// nothing on top of the directory walk that builds the catalog anyway.
/// <para>
/// This is game knowledge rather than a look, which is why it lives beside the catalog; the browser owns the
/// icon and the colour it draws per member (see <c>Views\ResourceTypeIcons.cs</c>).
/// </para>
/// </summary>
public enum LibraryResourceKind
{
    /// <summary>A folder this table has no entry for — drawn as a plain archive.</summary>
    Unknown,

    /// <summary>Drivable vehicles: <c>cars\</c>.</summary>
    Car,

    /// <summary>Story and street people: <c>hchar\</c>.</summary>
    Character,

    /// <summary>The player character: <c>player\</c>.</summary>
    Player,

    /// <summary>Law enforcement: <c>police_char\</c>.</summary>
    Police,

    /// <summary>Clothing the player can wear: <c>wardrobe\</c>.</summary>
    Wardrobe,

    /// <summary>The street population — pedestrians, not vehicles: <c>traffic\</c>.</summary>
    Traffic,

    /// <summary>A piece of the city: <c>city\</c>, <c>city_univers\</c>, <c>small\</c>.</summary>
    District,

    /// <summary>Destructible street props: <c>city_crash\</c>.</summary>
    CityCrash,

    /// <summary>Terrain: <c>ground\</c>.</summary>
    Terrain,

    /// <summary>Sky domes and weather: <c>skies\</c>.</summary>
    Sky,

    /// <summary>Menus and HUD: <c>gui\</c>.</summary>
    Interface,

    /// <summary>Cutscene and bink video: <c>fmv\</c>, <c>video\</c>.</summary>
    Video,

    /// <summary>Sound banks: <c>sound_city\</c>, <c>sound_default\</c>, <c>script_sounds\</c>.</summary>
    Sound,

    /// <summary>Soundtrack: <c>music\</c>.</summary>
    Music,

    /// <summary>Spoken lines: <c>speech\</c>, <c>speech_shops\</c>.</summary>
    Speech,

    /// <summary>Animation banks: <c>anims_city\</c>, <c>basic_anim\</c>.</summary>
    Animation,

    /// <summary>Gameplay scripts: <c>script\</c>.</summary>
    Script,

    /// <summary>Mission scripts: <c>missionscript\</c>.</summary>
    Mission,

    /// <summary>Particle effects: <c>particles\</c>.</summary>
    Particle,

    /// <summary>Shop interiors: <c>shops\</c>.</summary>
    Shop,

    /// <summary>Data tables: <c>tables\</c>.</summary>
    Table,

    /// <summary>Localised strings: <c>text\</c>.</summary>
    Text,

    /// <summary>Weapons: <c>weapons\</c>.</summary>
    Weapon,

    /// <summary>APEX cloth and destruction: <c>apex\</c>.</summary>
    Cloth,

    /// <summary>Generated content: <c>generate\</c>.</summary>
    Generated,

    /// <summary>Loading-screen maps: <c>mapa\</c>.</summary>
    Map,
}

/// <summary>
/// Turns a browser path into a <see cref="LibraryResourceKind"/>. The first folder under <c>sds</c> decides:
/// that is the level the game sorts at, and everything below it is a sub-division of the same content
/// (<c>sds/city/tsoeb</c> is still city).
/// </summary>
public static class LibraryResourceKinds
{
    private static readonly Dictionary<string, LibraryResourceKind> ByFolder =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cars"] = LibraryResourceKind.Car,
            ["hchar"] = LibraryResourceKind.Character,
            ["player"] = LibraryResourceKind.Player,
            ["police_char"] = LibraryResourceKind.Police,
            ["wardrobe"] = LibraryResourceKind.Wardrobe,
            ["traffic"] = LibraryResourceKind.Traffic,
            ["city"] = LibraryResourceKind.District,
            ["city_univers"] = LibraryResourceKind.District,
            ["small"] = LibraryResourceKind.District,
            ["city_crash"] = LibraryResourceKind.CityCrash,
            ["ground"] = LibraryResourceKind.Terrain,
            ["skies"] = LibraryResourceKind.Sky,
            ["gui"] = LibraryResourceKind.Interface,
            ["fmv"] = LibraryResourceKind.Video,
            ["video"] = LibraryResourceKind.Video,
            ["sound_city"] = LibraryResourceKind.Sound,
            ["sound_default"] = LibraryResourceKind.Sound,
            ["script_sounds"] = LibraryResourceKind.Sound,
            ["music"] = LibraryResourceKind.Music,
            ["speech"] = LibraryResourceKind.Speech,
            ["speech_shops"] = LibraryResourceKind.Speech,
            ["anims_city"] = LibraryResourceKind.Animation,
            ["basic_anim"] = LibraryResourceKind.Animation,
            ["script"] = LibraryResourceKind.Script,
            ["missionscript"] = LibraryResourceKind.Mission,
            ["particles"] = LibraryResourceKind.Particle,
            ["shops"] = LibraryResourceKind.Shop,
            ["tables"] = LibraryResourceKind.Table,
            ["text"] = LibraryResourceKind.Text,
            ["weapons"] = LibraryResourceKind.Weapon,
            ["apex"] = LibraryResourceKind.Cloth,
            ["generate"] = LibraryResourceKind.Generated,
            ["mapa"] = LibraryResourceKind.Map,
        };

    /// <summary>
    /// The kind for a browser path (<c>sds</c>, <c>sds/cars</c>, <c>sds/city/tsoeb</c>). <c>sds</c> itself and
    /// any folder the table does not name come back <see cref="LibraryResourceKind.Unknown"/> — a stripped or
    /// modded install may hold folders this build has never heard of, and that is not an error.
    /// </summary>
    public static LibraryResourceKind Of(string path)
    {
        int start = path.IndexOf('/') + 1;              // step over the leading "sds"
        if (start <= 0 || start >= path.Length) return LibraryResourceKind.Unknown;
        int end = path.IndexOf('/', start);
        string top = end < 0 ? path[start..] : path[start..end];
        return ByFolder.GetValueOrDefault(top, LibraryResourceKind.Unknown);
    }
}
