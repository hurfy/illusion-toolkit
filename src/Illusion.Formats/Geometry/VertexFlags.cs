namespace Illusion.Formats.Geometry;

/// <summary>
/// Per-LOD vertex declaration bits (which packed channels a vertex buffer carries). Values are the
/// engine's own; bits 11/12 and 19+ exist in the engine's full table (Texture3/4, Velocity, morph and
/// instancing channels) but never occur in Mafia II city data.
/// </summary>
[Flags]
public enum VertexFlags : uint
{
    Position = 1 << 0,
    Position2D = 1 << 1,
    Normals = 1 << 2,
    Tangent = 1 << 4,
    Skin = 1 << 6,
    Color = 1 << 7,
    TexCoords0 = 1 << 8,
    TexCoords1 = 1 << 9,
    TexCoords2 = 1 << 10,
    Unk05 = 1 << 11,
    ShadowTexture = 1 << 15,
    Color1 = 1 << 17,
    BBCoeffs = 1 << 18,
    DamageGroup = 1 << 20,
}
