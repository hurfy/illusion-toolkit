using System.Numerics;

namespace Illusion.Rendering.Controls;

/// <summary>
/// A saved viewpoint: where the camera stands, where it looks, and how far ahead the point it orbits sits.
/// Enough to hand a view back exactly as it was left — which is what the main window does when it leaves the
/// map for the library and comes back.
/// </summary>
/// <param name="OrbitDistance">Distance to the orbit pivot. Zero means "unknown, keep the current one".</param>
public readonly record struct CameraPose(Vector3 Position, float Yaw, float Pitch, float OrbitDistance);
