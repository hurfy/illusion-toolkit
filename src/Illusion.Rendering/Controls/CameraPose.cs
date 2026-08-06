using System.Numerics;

namespace Illusion.Rendering.Controls;

/// <summary>
/// A saved viewpoint: where the camera stands, where it looks, and how far ahead the point it orbits sits.
/// Enough to hand a view back exactly as it was left — which is what the main window does when it leaves the
/// map for the library and comes back.
/// </summary>
/// <param name="OrbitDistance">Distance to the orbit pivot. Zero means "unknown, keep the current one".</param>
/// <param name="Orthographic">The view was in the parallel projection an axis snap leaves it in.</param>
/// <param name="OrthoHeight">How much world that parallel view spanned — its zoom, which no other field
/// carries: with no vanishing point the standoff says nothing about how big anything was drawn.</param>
public readonly record struct CameraPose(Vector3 Position, float Yaw, float Pitch, float OrbitDistance,
    bool Orthographic = false, float OrthoHeight = 0f);
