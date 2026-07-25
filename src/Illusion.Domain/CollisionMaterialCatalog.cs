using System.Numerics;

namespace Illusion.Domain;

/// <summary>One Mafia II physics surface material: its table index, the game's own token, a display name and the
/// colour the collision overlay paints it with.</summary>
public readonly record struct CollisionMaterial(int Index, string Token, string Name, Vector3 Color);

/// <summary>
/// The Mafia II collision surface materials, keyed by their <c>MaterialsPhysics.tbl</c> index (1..63).
/// </summary>
/// <remarks>
/// Cooked collision meshes store a <b>raw PhysX slot id</b> per triangle
/// (<see cref="CollisionRenderPart"/> is built from it); the table index is that value minus
/// <see cref="RawToTableBias"/>. The same bias appears in the <c>.col</c> file itself, whose per-section material
/// field already stores the table index verbatim — which is why the two agree once the bias is applied.
/// <para/>
/// The bias is not a guess. Under it, the four character-body materials (<c>panak</c>, <c>panak_headshot</c>,
/// <c>panak_noha</c>, <c>panak_ruka</c>) are entirely absent from a 372 785-triangle static-world histogram, as
/// they must be; taking the raw id as the table index instead would claim ~50 000 triangles of character body in
/// static world collision, and shifting by one would reduce plain <c>silnice</c> (road) to eight triangles across
/// five districts while the whole road network became "dusty road". Raw id 64 also occurs in stock data, which
/// only resolves inside the table's range once biased.
/// <para/>
/// Entries are keyed by table index and converted <i>once</i>, in <see cref="ForRawId"/>. Do not hand-transcribe a
/// raw-id-keyed copy of this list: that is exactly how the reference toolkit's <c>CollisionMaterials</c> enum
/// drifted out of alignment in its tail.
/// </remarks>
public static class CollisionMaterialCatalog
{
    /// <summary>Raw cooked-mesh material ids are PhysX slot ids, offset from the table index by this much.</summary>
    public const int RawToTableBias = 2;

    /// <summary>Colour used for a material id that is not in the table (never occurs in stock data).</summary>
    public static readonly Vector3 UnknownColor = new(0.60f, 0.62f, 0.66f);

    // MaterialsPhysics.tbl itself runs 1..63. Index 0 is not in the table but does occur in stock cooked meshes
    // (raw slot 2, 76 triangles corpus-wide) — PhysX's own default material slot, i.e. geometry the authors never
    // assigned a surface to. It is a real, valid value, not a decode error.
    private const int FirstIndex = 0;
    private const int LastIndex = 63;

    // Surface families. Colour reads as the real-world material so a hull can be understood at a glance; members of
    // a family are separated by a small deterministic brightness step (see Build) so variants stay distinguishable
    // without breaking the semantic reading.
    private enum Family
    {
        Unassigned, Road, RoadMarking, Cobble, Sidewalk, Tile, Grass, Foliage, Soil, Gravel, Water, Snow,
        Metal, Wood, Concrete, Brick, Glass, Fabric, Universal, Character, Vehicle, Volume,
    }

    private static readonly Dictionary<Family, Vector3> FamilyColors = new()
    {
        [Family.Unassigned] = new Vector3(0.60f, 0.62f, 0.66f),
        [Family.Road] = new Vector3(0.28f, 0.29f, 0.31f),
        [Family.RoadMarking] = new Vector3(0.62f, 0.62f, 0.60f),
        [Family.Cobble] = new Vector3(0.45f, 0.42f, 0.40f),
        [Family.Sidewalk] = new Vector3(0.68f, 0.68f, 0.66f),
        [Family.Tile] = new Vector3(0.78f, 0.74f, 0.66f),
        [Family.Grass] = new Vector3(0.30f, 0.55f, 0.22f),
        [Family.Foliage] = new Vector3(0.18f, 0.38f, 0.18f),
        [Family.Soil] = new Vector3(0.42f, 0.31f, 0.20f),
        [Family.Gravel] = new Vector3(0.62f, 0.56f, 0.42f),
        [Family.Water] = new Vector3(0.20f, 0.48f, 0.75f),
        [Family.Snow] = new Vector3(0.88f, 0.92f, 0.96f),
        [Family.Metal] = new Vector3(0.55f, 0.60f, 0.66f),
        [Family.Wood] = new Vector3(0.55f, 0.36f, 0.18f),
        [Family.Concrete] = new Vector3(0.60f, 0.60f, 0.58f),
        [Family.Brick] = new Vector3(0.60f, 0.28f, 0.22f),
        [Family.Glass] = new Vector3(0.55f, 0.80f, 0.88f),
        [Family.Fabric] = new Vector3(0.72f, 0.62f, 0.50f),
        // "Universal hard/soft" is a generic structural surface and one of the most common materials in the game
        // (14 % of all collision triangles) — it has to read as neutral background, not as a warning.
        [Family.Universal] = new Vector3(0.70f, 0.66f, 0.52f),
        // Bodies and gameplay volumes are invisible in game and should never appear in static world collision —
        // paint them alarming so a stray one is impossible to miss.
        [Family.Character] = new Vector3(0.90f, 0.25f, 0.35f),
        [Family.Vehicle] = new Vector3(0.85f, 0.50f, 0.15f),
        [Family.Volume] = new Vector3(0.80f, 0.35f, 0.80f),
    };

    // Table index → the game's token (from MaterialsPhysics.tbl), a display name, and its surface family.
    // Verified against the game's own table by --probe-collision-materials.
    private static readonly (string Token, string Name, Family Family)[] Definitions =
    {
        ("default", "Unassigned", Family.Unassigned),                       // 0 — PhysX default slot, not in the tbl
        ("silnice", "Road", Family.Road),                                   // 1
        ("silnice_prasna", "Dusty Road", Family.Road),                      // 2
        ("prechod_pro_chodce", "Pedestrian Crossing", Family.RoadMarking),  // 3
        ("kocici_hlavy", "Cobblestones", Family.Cobble),                    // 4
        ("chodnik", "Sidewalk", Family.Sidewalk),                           // 5
        ("dlazdice", "Paving Tiles", Family.Tile),                          // 6
        ("trava", "Grass", Family.Grass),                                   // 7
        ("hlina", "Soil", Family.Soil),                                     // 8
        ("sterk", "Ballast", Family.Gravel),                                // 9
        ("pisek", "Sand", Family.Gravel),                                   // 10
        ("blato", "Mud", Family.Soil),                                      // 11
        ("kaluz", "Puddle", Family.Water),                                  // 12
        ("voda", "Water", Family.Water),                                    // 13
        ("snih", "Snow", Family.Snow),                                      // 14
        ("kov", "Metal", Family.Metal),                                     // 15
        ("plech", "Sheet Metal", Family.Metal),                             // 16
        ("pletivo", "Wire Mesh", Family.Metal),                             // 17
        ("zabradli", "Railing", Family.Metal),                              // 18
        ("drevo", "Wood", Family.Wood),                                     // 19
        ("koberec", "Carpet", Family.Fabric),                               // 20
        ("drevo_prkna", "Wood Planks", Family.Wood),                        // 21
        ("parkety", "Parquet", Family.Wood),                                // 22
        ("skripavy beton", "Gritty Concrete", Family.Concrete),             // 23
        ("kachlicky", "Ceramic Tiles", Family.Tile),                        // 24
        ("zed", "Wall", Family.Concrete),                                   // 25
        ("omitka", "Plaster", Family.Concrete),                             // 26
        ("cihly", "Bricks", Family.Brick),                                  // 27
        ("sklo_rozbitelne_1", "Breakable Glass 1", Family.Glass),           // 28
        ("sklo_rozbitelne_2", "Breakable Glass 2", Family.Glass),           // 29
        ("sklo_neprustrelne", "Bulletproof Glass", Family.Glass),           // 30
        ("kere_stromy", "Bushes and Trees", Family.Foliage),                // 31
        ("universal_tvrdy", "Universal Hard", Family.Universal),            // 32
        ("universal_meky", "Universal Soft", Family.Universal),             // 33
        ("panak", "Character Body", Family.Character),                      // 34
        ("no_shot_coll", "No-Shot Volume", Family.Volume),                  // 35
        ("papir", "Paper", Family.Fabric),                                  // 36
        ("calouneni", "Upholstery", Family.Fabric),                         // 37
        ("platena_latka", "Canvas", Family.Fabric),                         // 38
        ("camera_coll", "Camera Volume", Family.Volume),                    // 39
        ("player_coll", "Player Volume", Family.Volume),                    // 40
        ("sicily_zed", "Sicily Wall", Family.Concrete),                     // 41
        ("trava_trashy", "Trashy Grass", Family.Grass),                     // 42
        ("trava_negen", "Grass (no regrow)", Family.Grass),                 // 43
        ("trava_trashy_negen", "Trashy Grass (no regrow)", Family.Grass),   // 44
        ("chodnik_human", "Sidewalk Edge", Family.Sidewalk),                // 45
        ("auto", "Car Body", Family.Vehicle),                               // 46
        ("panak_headshot", "Character Head", Family.Character),             // 47
        ("panak_noha", "Character Leg", Family.Character),                  // 48
        ("panak_ruka", "Character Arm", Family.Character),                  // 49
        ("trava_sicily", "Sicily Grass", Family.Grass),                     // 50
        ("hedgerow", "Hedgerow", Family.Foliage),                           // 51
        ("dno", "Seabed", Family.Water),                                    // 52
        ("kanal", "Channel", Family.Water),                                 // 53
        ("silnice_ky", "Road (KY)", Family.Road),                           // 54
        ("silnice_prasna_ky", "Dusty Road (KY)", Family.Road),              // 55
        ("kocici_hlavy_ky", "Cobblestones (KY)", Family.Cobble),            // 56
        ("chodnik_ky", "Sidewalk (KY)", Family.Sidewalk),                   // 57
        ("dlazdice_ky", "Paving Tiles (KY)", Family.Tile),                  // 58
        ("drevo_prkna_ky", "Wood Planks (KY)", Family.Wood),                // 59
        ("silnice_tunel", "Tunnel Road", Family.Road),                      // 60
        ("zabradli_beton", "Concrete Railing", Family.Concrete),            // 61
        ("zabradli_drevo", "Wooden Railing", Family.Wood),                  // 62
        ("papunddeckel", "Cardboard", Family.Fabric),                       // 63
    };

    private static readonly CollisionMaterial[] Entries = Build();

    /// <summary>Every known material, ordered by table index.</summary>
    public static IReadOnlyList<CollisionMaterial> All => Entries;

    /// <summary>Looks a material up by its <c>MaterialsPhysics.tbl</c> index.</summary>
    public static CollisionMaterial ForTableIndex(int index) =>
        index >= FirstIndex && index <= LastIndex
            ? Entries[index - FirstIndex]
            : new CollisionMaterial(index, "unknown", $"Unknown ({index})", UnknownColor);

    /// <summary>Looks a material up by the raw slot id stored per triangle in a cooked collision mesh.</summary>
    public static CollisionMaterial ForRawId(int rawId) => ForTableIndex(rawId - RawToTableBias);

    /// <summary>The overlay colour for a raw cooked-mesh material id.</summary>
    public static Vector3 ColorForRawId(int rawId) => ForRawId(rawId).Color;

    /// <summary>
    /// Replaces the shipped tokens with the ones read from the installed game's <c>MaterialsPhysics.tbl</c>,
    /// keyed by table index. Display names and colours are unaffected — only the game's own spelling changes —
    /// so this is safe to skip entirely when the table cannot be read. Returns how many tokens differed.
    /// </summary>
    public static int ApplyGameTokens(IReadOnlyDictionary<int, string> tokensByTableIndex)
    {
        ArgumentNullException.ThrowIfNull(tokensByTableIndex);
        int changed = 0;
        foreach ((int index, string token) in tokensByTableIndex)
        {
            if (index < FirstIndex || index > LastIndex || string.IsNullOrWhiteSpace(token)) continue;
            CollisionMaterial current = Entries[index - FirstIndex];
            if (string.Equals(current.Token, token, StringComparison.Ordinal)) continue;
            Entries[index - FirstIndex] = current with { Token = token };
            changed++;
        }
        return changed;
    }

    private static CollisionMaterial[] Build()
    {
        var entries = new CollisionMaterial[Definitions.Length];
        var seenInFamily = new Dictionary<Family, int>();
        for (int i = 0; i < Definitions.Length; i++)
        {
            (string token, string name, Family family) = Definitions[i];
            seenInFamily.TryGetValue(family, out int ordinal);
            seenInFamily[family] = ordinal + 1;
            entries[i] = new CollisionMaterial(
                FirstIndex + i, token, name, Shade(FamilyColors[family], ordinal));
        }
        return entries;
    }

    // Separate same-family materials by stepping brightness a little either side of the base, alternating so the
    // first few members stay closest to the family's true colour.
    private static Vector3 Shade(Vector3 baseColor, int ordinal)
    {
        if (ordinal == 0) return baseColor;
        int step = (ordinal + 1) / 2;
        float factor = 1f + (ordinal % 2 == 1 ? 0.13f : -0.13f) * step;
        return Vector3.Clamp(baseColor * factor, Vector3.Zero, Vector3.One);
    }
}
