using System.Numerics;

namespace Illusion.Assets.World;

/// <summary>
/// Load zone: world AABB of an AREA box (from FrameResource city_univers) + districts that the engine
/// keeps resident while the camera is inside (from cityareas.bin by AREA name).
/// </summary>
public sealed class AreaZone
{
    public string Name { get; init; } = null!;
    public Vector3 Min { get; init; }
    public Vector3 Max { get; init; }
    public IReadOnlyList<string> Districts { get; init; } = null!;

    // XY only: AREA zones are thin ground-level volumes tiling the city in plan. The editor camera flies
    // high, so height must NOT cull streaming — what matters is the footprint under the camera.
    public bool Contains(Vector3 p, float margin = 0f) =>
        p.X >= Min.X - margin && p.X <= Max.X + margin &&
        p.Y >= Min.Y - margin && p.Y <= Max.Y + margin;
}
