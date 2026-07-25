using System.IO;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// Persistence tracking of the viewport (keyed by the scene-document wrapper node, which carries the
/// source .sds). Two sets, always unsaved ⊆ edited:
/// unsaved — edited but not yet written to the extracted folder (drives the title '*'; cleared by Save);
/// edited — edited at least once this session, not yet packed (what Build repacks; cleared by Build).
/// Both are pruned when their district streams out / the scene resets, so we never save a frame that has
/// left memory.
/// </summary>
internal sealed class ScenePersistence
{
    private readonly D3DImageHost _host;

    public ScenePersistence(D3DImageHost host) => _host = host;

    private readonly HashSet<SceneNode> _unsavedFrames = new();
    private readonly HashSet<SceneNode> _editedFrames = new();

    /// <summary>Whether there are transform edits not yet written to disk (the title shows a '*' while true).</summary>
    public bool HasUnsavedEdits => _unsavedFrames.Count > 0;

    /// <summary>Whether any archive has edits to repack (saved or not) — gates the Build action.</summary>
    public bool HasBuildableEdits => _editedFrames.Count > 0;

    // Flags the owning frame resource as edited (unsaved + needs-pack) and notifies the title/menus. (Repainting an
    // edited collision hull is handled separately, live, by the streamer's per-frame RenderDirty check.)
    public void MarkFrameModified(SceneNode node)
    {
        if (node.OwningDocumentNode() is not { } fr) return;
        _editedFrames.Add(fr);
        if (_unsavedFrames.Add(fr)) _host.RaiseDirtyChanged();
    }

    // Flags the owning document so the next save also rewrites its FrameNameTable (a name / on-table edit).
    public void MarkNameTableDirty(SceneNode node)
    {
        if (node.OwningDocumentNode()?.Source is ISceneDocument doc) doc.MarkNameTableDirty();
    }

    // Drops persistence flags for frames whose subtree is leaving the scene (district unload) — they can no longer
    // be saved from memory. Returns true if the unsaved set shrank (so the caller can refresh the title).
    public bool PruneEditedFrames(Predicate<SceneNode> gone)
    {
        _editedFrames.RemoveWhere(gone);
        return _unsavedFrames.RemoveWhere(gone) > 0;
    }

    /// <summary>Drops all persistence state on a scene reset; notifies the title if anything was pending.</summary>
    public void Reset()
    {
        bool hadEdits = _unsavedFrames.Count > 0 || _editedFrames.Count > 0;
        _unsavedFrames.Clear(); // the frame resources are being unloaded — nothing left to save/pack
        _editedFrames.Clear();
        if (hadEdits) _host.RaiseDirtyChanged();
    }

    /// <summary>Writes every edited-but-unsaved scene document back into its extracted folder. Frames that leave
    /// the scene mid-session are skipped defensively. Returns the number of archives written; clears the '*'.</summary>
    public int SaveEdits()
    {
        int saved = 0;
        foreach (SceneNode fr in _unsavedFrames.ToList())
        {
            if (fr.Source is not ISceneDocument document || !_host.Tree.IsInScene(fr))
            {
                _unsavedFrames.Remove(fr);   // stale (unloaded) — nothing to write
                _editedFrames.Remove(fr);
                continue;
            }
            document.SaveWorkingCopy();
            _unsavedFrames.Remove(fr);        // written; still tracked in _editedFrames until packed
            saved++;
        }
        _host.RaiseDirtyChanged();
        return saved;
    }

    /// <summary>The distinct source archives that <see cref="BuildEdits"/> would repack right now — edited this
    /// session and still in the scene. Feeds the build dialog so it can list exactly what will be written.</summary>
    public IReadOnlyList<FileInfo> PendingBuildArchives()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<FileInfo>();
        foreach (SceneNode fr in _editedFrames)
            if (fr.Source is ISceneDocument doc && _host.Tree.IsInScene(fr) && seen.Add(doc.SourceArchive.FullName))
                list.Add(doc.SourceArchive);
        return list;
    }

    /// <summary>Saves any pending edits, then repacks every edited archive (deduped by path) into its .sds. When
    /// <paramref name="createBackup"/> is set, each archive's previous contents are versioned into a timestamped
    /// backup first (all archives in this build share one timestamp). Each archive is packed independently: a
    /// failure is captured (not thrown) so the remaining archives still build, and only successfully-packed
    /// archives are dropped from the edited set.</summary>
    public D3DImageHost.BuildReport BuildEdits(bool createBackup = true)
    {
        SaveEdits(); // ensure the extracted folders match memory before we pack them

        // Group the edited frames by their source archive (still in scene), so one archive is packed once and, on
        // failure, ALL of its frames stay buildable together for a retry.
        var byArchive = new Dictionary<string, (FileInfo Sds, List<SceneNode> Frames)>(StringComparer.OrdinalIgnoreCase);
        foreach (SceneNode fr in _editedFrames.ToList())
        {
            if (fr.Source is not ISceneDocument doc || !_host.Tree.IsInScene(fr)) { _editedFrames.Remove(fr); continue; }
            FileInfo sds = doc.SourceArchive;
            if (!byArchive.TryGetValue(sds.FullName, out (FileInfo Sds, List<SceneNode> Frames) entry))
                byArchive[sds.FullName] = entry = (sds, new List<SceneNode>());
            entry.Frames.Add(fr);
        }

        var packed = new List<SdsWriter.PackResult>();
        var failed = new List<D3DImageHost.BuildFailure>();
        DateTime when = DateTime.Now; // one stamp for the whole build, so co-packed archives group in backups\
        foreach ((FileInfo sds, List<SceneNode> frames) in byArchive.Values)
        {
            try
            {
                packed.Add(SdsWriter.PackSds(sds, createBackup, when));
                foreach (SceneNode fr in frames) _editedFrames.Remove(fr); // drop only on a successful pack
            }
            catch (Exception ex)
            {
                failed.Add(new D3DImageHost.BuildFailure(sds.FullName, ex.Message)); // leave frames buildable for a retry
            }
        }
        _host.RaiseDirtyChanged();
        return new D3DImageHost.BuildReport(packed, failed);
    }
}
