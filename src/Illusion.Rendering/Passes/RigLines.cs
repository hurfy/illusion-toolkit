using System.Numerics;

namespace Illusion.Rendering.Passes;

/// <summary>
/// One archive's rig overlay, as three line lists in world space (endpoint pairs, A,B,A,B,…).
/// Kept together because they are uploaded, dropped and drawn as one thing — see
/// <see cref="SceneRenderer.SetSkeletonDistrict"/>, which draws them in three weights.
/// </summary>
/// <param name="Bones">A segment from each bone to its parent.</param>
/// <param name="Joints">A three-axis tick at every joint.</param>
/// <param name="Attachments">A segment from each joint to whatever hangs off it — a car's collision hulls,
/// door handles, locks and climb boxes are frames attached to a bone, and this is the only drawing that says
/// which bone carries which.</param>
public readonly record struct RigLines(
    IReadOnlyList<Vector3> Bones,
    IReadOnlyList<Vector3> Joints,
    IReadOnlyList<Vector3> Attachments)
{
    /// <summary>True when there is nothing at all to draw.</summary>
    public bool IsEmpty => Bones.Count == 0 && Joints.Count == 0 && Attachments.Count == 0;
}
