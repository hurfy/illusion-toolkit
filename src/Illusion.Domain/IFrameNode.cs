using System.Numerics;

namespace Illusion.Domain;

/// <summary>
/// A transformable object of a loaded scene document (a frame in Mafia's scene graph). This is the exact
/// surface the editor needs: transforms for the gizmo/property fields, the frame-graph parent chain for
/// group-drag and delete logic, and the name-table flags for season/proxy classification.
/// </summary>
/// <remarks>
/// Implementations must be canonical: one instance per underlying frame object for the lifetime of its
/// document. Selection, group-drag and delete logic key sets by reference identity, and
/// <see cref="Parent"/> chains must yield the same instances those sets contain.
/// </remarks>
public interface IFrameNode : ISceneSource
{
    /// <summary>Local transform relative to the parent frame. Setting it cascades fresh world transforms
    /// through the frame subtree (children move with their parent).</summary>
    Matrix4x4 LocalTransform { get; set; }

    /// <summary>World transform (kept current by <see cref="LocalTransform"/> set-cascades).</summary>
    Matrix4x4 WorldTransform { get; }

    /// <summary>The parent frame's world transform, identity for a root — the matrix a world-space edit is
    /// re-localized against.</summary>
    Matrix4x4 ParentWorldTransform { get; }

    /// <summary>Parent in the frame graph (not the visual tree), null for a root.</summary>
    IFrameNode? Parent { get; }

    /// <summary>Whether this frame is listed in the archive's frame name table (only named entries carry
    /// authoritative <see cref="NameTableFlags"/>).</summary>
    bool IsOnNameTable { get; }

    /// <summary>Raw name-table flag bits: 0 = normal, 3 (flag_1|flag_2) = winter geometry, any other
    /// non-zero combination = proxy (verified across all Mafia II districts — see --probe-flags).</summary>
    int NameTableFlags { get; }
}
