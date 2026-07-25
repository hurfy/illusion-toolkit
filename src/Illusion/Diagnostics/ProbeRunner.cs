using Illusion.Diagnostics.Probes;

namespace Illusion.Diagnostics;

/// <summary>
/// Headless probes of load chains: <c>Illusion.exe --probe-*</c> runs a single scenario
/// without UI and writes a report to <c>%TEMP%\illusion_*.txt</c>. The game path is taken from settings
/// (the last one opened in the launcher).
/// </summary>
internal static class ProbeRunner
{
    /// <summary>Runs a probe from command-line arguments; false — no probe requested.</summary>
    public static bool TryRun(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        switch (args[0])
        {
            // SDS read chain: Illusion.exe --probe-sds [path.sds]
            case "--probe-sds":
                ArchiveProbes.RunSdsProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // StreamMap catalog (timeline of scripts/cutscenes).
            case "--probe-streammap":
                WorldProbes.RunStreamMapProbe();
                return true;
            // Location catalog (Location × Season).
            case "--probe-map":
                WorldProbes.RunMapProbe();
                return true;
            // AREA boxes (load zones) from city_univers.
            case "--probe-areas":
                WorldProbes.RunAreasProbe();
                return true;
            // Streaming zones (box⋈cityareas + lookup by position).
            case "--probe-stream":
                WorldProbes.RunStreamProbe();
                return true;
            // Dump of district scenes + their categories (proxy/snow/normal).
            case "--probe-scenes":
                SceneProbes.RunScenesProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // FrameNameTable flags: link flags→objects and correlate with name-based proxy/snow detection.
            case "--probe-flags":
                SceneProbes.RunFlagsProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // FrameNameTable flag STRUCTURE: how proxy/snow-flagged objects group under scene folders + cascade.
            case "--probe-flagtree":
                SceneProbes.RunFlagTreeProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // city_crash: Translokator + frame_resource → instances.
            case "--probe-crash":
                SceneProbes.RunCrashProbe(args.Length >= 2 && args[1] == "winter");
                return true;
            // GPU smoke: context + renderer (compiling both shaders) + instanced draw.
            case "--probe-gpu":
                GpuProbes.RunGpuProbe();
                return true;
            // Free-threaded resource creation: loader threads build meshes (racing on shared textures)
            // while the main thread renders — validates the background scene-build path on this driver.
            case "--probe-async":
                GpuProbes.RunAsyncProbe();
                return true;
            // Render modes: render one mesh once per RenderMode (Render/MaterialPreview/Solid/Wireframe).
            case "--probe-modes":
                GpuProbes.RunModesProbe();
                return true;
            // Navigation gizmo: render the axis widget to a PNG at a fixed camera orientation (no game data).
            case "--probe-gizmo":
                EditorProbes.RunGizmoProbe();
                return true;
            // Selection math: ray-picking + gizmo transform ops + Euler round-trip (no game data, no GPU).
            case "--probe-select":
                EditorProbes.RunSelectProbe();
                return true;
            // Selection outline: renders the silhouette contour of a selected mesh and reads pixels back to prove
            // the contour appears on the silhouette and the interior stays untouched (no game data; needs a GPU).
            case "--probe-outline":
                GpuProbes.RunOutlineProbe();
                return true;
            // Edit history + Shift-snap math (headless, no game data, no GPU): undo/redo stack semantics and the
            // gizmo snap quantization for move/rotate/scale.
            case "--probe-edit":
                EditorProbes.RunEditProbe();
                return true;
            // UI smoke: the Vector3Box control loads + its copy/paste format round-trips (no game data, no GPU).
            case "--probe-ui":
                EditorProbes.RunUiProbe();
                return true;
            // Save + pack chain: edit a frame's transform → SdsWriter.SaveFrameResource → reload & verify the edit
            // persisted (extracted folder restored after), then repack the folder to a TEMP .sds and re-open it.
            case "--probe-save":
                SaveProbes.RunSaveProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Renders the viewport transform overlay (compact, actions-off Vector3Box at large coords) to a PNG so
            // the fields-fit / no-clip can be eyeballed. Output: %TEMP%\illusion_panel.png
            case "--probe-panel":
                EditorProbes.RunPanelProbe();
                return true;
            // Reparent (hierarchy): reparent objects via SceneDocumentAdapter.Reparent, verify persistence,
            // cycle rejection and scene-folder targets. Output: %TEMP%\illusion_reparent.txt
            case "--probe-reparent":
                SaveProbes.RunReparentProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Parent-picker click sequence: reparent via the Object-tab picker must not swap the candidate
            // view mid-click (the mouse-up would land on an arbitrary unfiltered row and re-reparent).
            // Output: %TEMP%\illusion_reparent_picker.txt
            case "--probe-reparent-picker":
                PickerProbes.RunReparentPickerProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Frame-object delete persistence: DetachedFrames drops a leaf + a subtree from the FrameResource
            // and the rebuilt name table; reattach makes the next save byte-identical (undo is byte-faithful).
            // Output: %TEMP%\illusion_framedelete.txt
            case "--probe-framedelete":
                SaveProbes.RunFrameDeleteProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Frame-object duplication: FrameDuplicator deep-copies a static mesh (fresh blocks + buffers,
            // byte-identical geometry, same parents); undo restores the pre-duplicate save byte-identically.
            // Output: %TEMP%\illusion_duplicate.txt
            case "--probe-duplicate":
                SaveProbes.RunDuplicateProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // glTF import, reader half: GLB parsing, hierarchy transforms, per-primitive materials,
            // COL_ routing, payload conversion (axes/scale/offset/UV), Draco refusal. In-memory fixture.
            // Output: %TEMP%\illusion_gltf.txt
            case "--probe-gltf":
                ImportProbes.RunGltfProbe();
                return true;
            // glTF import, render-mesh route: fixture cube (2 game materials) → BridgeObjectFactory →
            // save/reload survival, bridge export, byte-faithful undo. Output: %TEMP%\illusion_import_mesh.txt
            case "--probe-import-mesh":
                ImportProbes.RunMeshProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // glTF import, collision route: fixture COL_ cube → CollisionPushAcceptor (sections/cook/mint)
            // → decoded hull matches. Skips without the PhysX runtime. Output: %TEMP%\illusion_import_collision.txt
            case "--probe-import-collision":
                ImportProbes.RunCollisionProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Game-material creation for imports, on a TEMP copy of default.mtl: writer fixpoint
            // (byte-identical rewrite), FNV64-named default-preset creation, backup + reload survival.
            // Output: %TEMP%\illusion_import_materials.txt
            case "--probe-import-materials":
                ImportProbes.RunMaterialsProbe();
                return true;
            // Versioned .sds backups: SdsWriter.BackupArchive writes timestamped copies into a "backups" folder
            // beside the archive (temp files only — no game data, no GPU). Output: %TEMP%\illusion_backup.txt
            case "--probe-backup":
                SaveProbes.RunBackupProbe();
                return true;
            // Restore-from-backup: ListBackups filters strictly by stem+stamp, RestoreArchive swaps the live
            // .sds back atomically (history untouched), DeleteExtracted drops the mirror marker-first
            // (temp files only — no game data, no GPU). Output: %TEMP%\illusion_restore.txt
            case "--probe-restore":
                SaveProbes.RunRestoreProbe();
                return true;
            // Reusable AppDialog: construct it from options + render its content to a PNG so the layout can be
            // eyeballed (no game data, no GPU). Output: %TEMP%\illusion_dialog.png / .txt
            case "--probe-dialog":
                EditorProbes.RunDialogProbe();
                return true;
            // Transient notice surface (the non-modal refusal channel): repeat collapsing, visible cap,
            // dismissal and clearing, plus a PNG of a representative stack.
            // Output: %TEMP%\illusion_notice.txt / .png
            case "--probe-notice":
                EditorProbes.RunNoticeProbe();
                return true;
            // Refactor ground-truth net: for every game .sds — archive write-idempotence (open → serialize to
            // memory → re-open → entry tables must match), FrameResource generation stability (write A → parse A →
            // write B, A==B byte-exact), and a census of block compression (zlib/oodle/uncompressed) per archive.
            // Optional arg filters archives by path substring. Output: %TEMP%\illusion_roundtrip.txt
            case "--probe-roundtrip":
                ArchiveProbes.RunRoundtripProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // FrameNameTable rebuild fidelity: rebuild each archive's name table from its FrameResource, reload,
            // relink, and verify per-object membership/flags/names match the original (semantic fixpoint for the
            // name-table rewrite). Optional arg filters archives. Output: %TEMP%\illusion_nametable.txt
            case "--probe-nametable":
                ArchiveProbes.RunNameTableProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // Extraction parity: re-extract every archive that already has a folder in the /resources mirror
            // (made by the previous extractor) into TEMP and compare the two trees file-by-file, byte-exact.
            // Optional arg filters archives by path substring. Output: %TEMP%\illusion_extract.txt
            case "--probe-extract":
                ArchiveProbes.RunExtractProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // Property descriptors: build the property catalog for every frame-object type and assert coverage,
            // read/round-trip, the Name name-table lock, and (with game data) an identity-write-back serialization
            // fixpoint. Optional arg selects the district for the fixpoint. Output: %TEMP%\illusion_properties.txt
            case "--probe-properties":
                PropertyProbes.RunPropertiesProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // V2-ported format parsers: bulk-parse every file of a type across the extracted resources and
            // byte-roundtrip it (parse → write → compare). Arg selects the format: ids (ItemDesc). Output:
            // %TEMP%\illusion_formats.txt
            case "--probe-formats":
                FormatProbes.RunFormatsProbe(args.Length >= 2 ? args[1] : "ids");
                return true;
            // Collision decode: decode every cooked collision mesh into vertices+triangles and re-parse each
            // blob's OPCODE tail as an integrity oracle. Output: %TEMP%\illusion_collision_decode.txt
            case "--probe-collision-decode":
                FormatProbes.RunCollisionDecodeProbe();
                return true;
            // Collision surface materials: cross-check the .col sections against the cooked mesh's per-triangle
            // material array, resolve every id through MaterialsPhysics.tbl, and assert the render parts partition
            // the index buffer. Output: %TEMP%\illusion_collision_materials.txt
            case "--probe-collision-materials":
                FormatProbes.RunCollisionMaterialsProbe();
                return true;
            // Collision render pipeline (no GPU): build a district's collision layer and compare its world AABB
            // to the render meshes' (position-convention check). Output: %TEMP%\illusion_collision_render.txt
            case "--probe-collision-render":
                SceneProbes.RunCollisionRenderProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision render (GPU): load a district's meshes + collision, prove the collision pass draws
            // (pixel diff vs collision-off), save a PNG for a visual axis check. Output: illusion_collision_gpu.txt/.png
            case "--probe-collision-gpu":
                GpuProbes.RunCollisionGpuProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision placement oracle: pair .col instances with same-hash FrameObjectCollision world
            // transforms and empirically fit the Euler convention. Output: %TEMP%\illusion_collision_align.txt
            case "--probe-collision-align":
                SceneProbes.RunCollisionAlignProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision placement save (Phase 2): edit placements, prove ToBytes round-trip + untouched blobs
            // byte-identical + SdsCollisionSaver writes atomically. Output: %TEMP%\illusion_collision_save.txt
            case "--probe-collision-save":
                SceneProbes.RunCollisionSaveProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision instance editing (Phase 2): drive the CollisionInstanceAdapter + property catalog
            // descriptors and assert they read/write the placement. Output: %TEMP%\illusion_collision_edit.txt
            case "--probe-collision-edit":
                SceneProbes.RunCollisionEditProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision viewport picking (Phase 2): aim rays at each hull's first triangle and confirm the CPU
            // ray-cast reports a hit (validates the pick convention). Output: %TEMP%\illusion_collision_pick.txt
            case "--probe-collision-pick":
                SceneProbes.RunCollisionPickProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Build faithfulness: Pack(extracted) vs the original archive (the app's Build path, unedited).
            // Output: %TEMP%\illusion_buildcheck.txt
            case "--probe-buildcheck":
                ArchiveProbes.RunBuildCheckProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Resource-by-resource diff of two .sds archives (stock vs a build), grouped by resource type so the
            // packer's type regrouping is not mistaken for corruption. Output: %TEMP%\illusion_archdiff.txt
            // FrameResource write fidelity: load every district's .fr and write it back unedited; the bytes must
            // be identical. Output: %TEMP%\illusion_frameroundtrip.txt
            case "--probe-frameroundtrip":
                ArchiveProbes.RunFrameRoundtripProbe();
                return true;
            // FrameResource edit fidelity: move one object in memory and assert the save changes only that
            // object's transform. Pass "*" for every district. Output: %TEMP%\illusion_frameedit.txt
            case "--probe-frameedit":
                ArchiveProbes.RunFrameEditProbe(args.Length >= 2 ? args[1] : "*");
                return true;
            case "--probe-archdiff":
                ArchiveProbes.RunArchiveDiffProbe(
                    args.Length >= 2 ? args[1] : null, args.Length >= 3 ? args[2] : null);
                return true;
            // Blender bridge: .ilx container write→read fidelity, unknown-kind tolerance, atomic
            // rename (no game data, no GPU). Output: %TEMP%\illusion_bridge_payload.txt
            case "--probe-bridge-payload":
                BridgeProbes.RunPayloadProbe();
                return true;
            // Blender bridge: weld/split export fidelity against a real district (per-loop attrs
            // match the viewport decode bit-exactly, UV V-flip, determinism).
            case "--probe-bridge-weld":
                BridgeProbes.RunWeldProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Blender bridge: control-protocol handshake/denial/ping + malformed-line resilience
            // against a fake in-process server (no game data, no GPU, no Blender).
            case "--probe-bridge-hello":
                BridgeProbes.RunHelloProbe();
                return true;
            // Blender bridge: locate the installed Blender and ask it for --version. SKIPs cleanly
            // when Blender is absent.
            case "--probe-bridge-blender":
                BridgeProbes.RunBlenderProbe();
                return true;
            // Blender bridge: full tracer-bullet loop against a real Blender (launch/reuse →
            // handshake → synthetic load_scene → scene_ready → request_push → byte-identical
            // roundtrip). Briefly opens a Blender window; SKIPs when Blender is absent.
            case "--probe-bridge-e2e":
                BridgeProbes.RunE2eProbe();
                return true;
            // Blender bridge: Compress∘Decompress byte-identity over a district's vertex data —
            // the push-path fidelity gate.
            case "--probe-bridge-vertex":
                BridgeProbes.RunVertexProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Blender bridge: count-preserving apply chain (unchanged push byte-identical, minimal
            // diff on a one-vertex edit, requantization, apply/restore).
            case "--probe-bridge-resplit":
                BridgeProbes.RunResplitProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Blender bridge: pool write-back (unmodified fixpoint, dirty-only rewrites, and a full
            // push→Save→reload persistence cycle; extracted folder restored afterwards).
            case "--probe-bridge-pools":
                BridgeProbes.RunPoolsProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Blender bridge: topology rebuild (face deletion + subdivision with a brand-new vertex
            // → structurally valid LOD0, Save→reload survival, clean undo).
            case "--probe-bridge-rebuild":
                BridgeProbes.RunRebuildProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Blender bridge: object-level ops (world↔local re-localization incl. parented frames,
            // material reassignment via the rebuild).
            case "--probe-bridge-transform":
                BridgeProbes.RunTransformProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Blender bridge: new-object creation (synthetic cube → fresh frame object + buffers,
            // Save→reload survival, detach/reattach).
            case "--probe-bridge-newobj":
                BridgeProbes.RunNewObjectProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision: cooked-mesh scaler over the whole corpus — quantized tree bytes bit-identical,
            // vertices and coefficients moved by exactly s, root box lands on the scaled original.
            // Collision: modelCode census — how many shipped cooked meshes carry no serialized tree.
            case "--probe-collision-modelcode":
                FormatProbes.RunCollisionModelCodeProbe();
                return true;
            case "--probe-collision-scale":
                FormatProbes.RunCollisionScaleProbe();
                return true;
            // Collision pre-flight census: placement→hull self-containment (gates orphan sweeping), mesh-list
            // hash ordering (insert-sorted vs append), Unk4→FrameObjectCollision pairing (whether a hash
            // repoint must rewrite the frame side) and .col-per-archive uniqueness (save targeting).
            // Output: %TEMP%\illusion_collision_census.txt
            case "--probe-collision-census":
                FormatProbes.RunCollisionCensusProbe();
                return true;
            // Collision: gizmo scale preview — the scale reaches the render matrices through both build
            // paths, stays opt-in, and leaves no trace in the saved .col (it has nowhere to store one).
            case "--probe-collision-preview":
                SceneProbes.RunCollisionPreviewProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision: hull minting — deterministic derived identity, dedup, section carry-over,
            // orphan collection and .col round-trip (the layer a scaled placement will be built on).
            case "--probe-collision-mint":
                SceneProbes.RunCollisionMintProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision: applying a previewed resize to the file — mint + repoint + preview reset through the
            // real CollisionMintEdit, whole-file integrity after save, and a byte-identical undo.
            case "--probe-collision-scale-apply":
                SceneProbes.RunCollisionScaleApplyProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision cooking: is the PhysX runtime present (reports SKIPPED rather than failing when not,
            // since a machine without it is a supported configuration).
            case "--probe-collision-runtime":
                CookProbes.RunRuntimeProbe();
                return true;
            // Collision cooking: the 32-bit index widener, without needing a PhysX install — a shipped hull is
            // narrowed and widened back, and must come out byte-identical.
            case "--probe-collision-widen":
                CookProbes.RunWidenProbe();
                return true;
            // Collision cooking: the M2PhysX subprocess end to end — refusals by name, determinism, per-triangle
            // surfaces surviving the cook's reordering, and a tree-bearing mesh. SKIPs without the runtime.
            case "--probe-collision-cook":
                CookProbes.RunCookProbe();
                return true;
            // Collision: accepting a hull reshaped in Blender — surfaces resolved from material slots,
            // unusable triangles dropped, sections built, cooked and minted. SKIPs without the runtime.
            case "--probe-collision-shapepush":
                CookProbes.RunShapePushProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision: authoring a hull that never existed — the Shift+D path from Blender. SKIPs without
            // the runtime.
            case "--probe-collision-newhull":
                CookProbes.RunNewHullProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision: sweeping the hulls no placement references — exact removal set, placements untouched,
            // and an undo that restores their original positions in the mesh list, not just their presence.
            case "--probe-collision-orphan":
                SceneProbes.RunCollisionOrphanProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision: mesh-set invalidation — a hull added to the .col must invalidate the cached
            // decode, or its placements vanish from the overlay, picking and the selection highlight.
            case "--probe-collision-meshset":
                SceneProbes.RunCollisionMeshSetProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Blender bridge: collision hull export → .ilx → read-back (geometry + placement fields
            // bit-exact, kind="collision", transform-only push without faceMaterials still parses).
            case "--probe-bridge-collision":
                BridgeProbes.RunCollisionProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Blender bridge: live collision round trip against a real Blender — the addon must echo
            // kind="collision" on the way back, or the toolkit loses the move.
            case "--probe-bridge-collision-e2e":
                BridgeProbes.RunCollisionE2eProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // The native core (Mafia.Formats.dll): version/ABI handshake, 10 MB echo through the
            // {ptr,len} buffer protocol, readable errors, double-free refusal, and thread-local
            // error isolation under concurrent callers.
            case "--probe-native":
                NativeProbes.RunNativeProbe();
                return true;
            // Native core primitives on real game data: FNV-1 vs the living managed hasher,
            // XTEA unwrap into loadable archives (incl. partial tails), zlib both ways, the
            // oodle bind when this install carries oo2core.
            case "--probe-native-core":
                NativeProbes.RunCoreParityProbe();
                return true;
            // FrameNameTable: native re-save byte-identical to every .fnt on disk.
            case "--probe-native-fnt":
                NativeFrameProbes.RunFntParityProbe();
                return true;
            // FrameResource: the native generation is stable on every extracted .fr.
            case "--probe-native-fr":
                NativeFrameProbes.RunFrParityProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // Buffer pools: native re-save against every .ibp/.vbp on disk.
            case "--probe-native-pools":
                NativeFrameProbes.RunPoolParityProbe();
                return true;
            // SceneNode visibility aggregate: correctness after the O(1) cache, plus a container
            // eye-toggle cost on a district-sized tree.
            case "--probe-visperf":
                VisPerfProbes.RunVisPerfProbe();
                return true;
            // District load cost: wall time, managed allocation and GC pressure of the hierarchy
            // load the viewport performs. Args: [district] [passes].
            case "--probe-loadperf":
                LoadPerfProbes.RunLoadPerfProbe(
                    args.Length >= 2 ? args[1] : null,
                    args.Length >= 3 && int.TryParse(args[2], out int p) ? p : 2);
                return true;
            // Vertex decode dual-path: the narrow load-path channel decode must be bit-identical
            // to the full-fidelity wire decode. Optional arg filters by .fr path.
            case "--probe-native-vtx":
                NativeFrameProbes.RunVertexChannelProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // LOD capsule builder: mf_frames_rebuild_lod against the capsule-layer serialization
            // on a deterministic case matrix.
            case "--probe-native-lod":
                NativeFrameProbes.RunLodBuilderParityProbe();
                return true;
            // Material libraries: native re-save byte-identical to every .mtl on disk.
            case "--probe-native-mtl":
                NativeMaterialProbes.RunMtlParityProbe();
                return true;
            // Small formats: .ids/.act/.nav/.nov re-save fixpoint, .tra/cityareas/StreamMap
            // read smoke.
            case "--probe-native-misc":
                NativeMiscProbes.RunMiscParityProbe();
                return true;
            // The golden snapshot of the decoded neutral model (P6 gate 1): snap writes the
            // committed baseline, check diffs the current codecs against it.
            case "--probe-golden":
                NativeGoldenProbes.RunGoldenProbe(
                    args.Length >= 2 ? args[1] : "check", args.Length >= 3 ? args[2] : null);
                return true;
            // Generated-model cycle (D3): hostile model survives C#→native→C# bit-exactly,
            // the wire refuses garbage, and the committed generated files match a fresh
            // regeneration. Optional arg = repo root (needed when run out-of-tree).
            case "--probe-schema":
                SchemaProbes.RunSchemaProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // The embedded MCP server: a real client discovers and calls the tool over streamable
            // HTTP, and a busy port is reported rather than thrown.
            case "--probe-mcp":
                McpProbes.RunMcpProbe();
                return true;
            // Material editor: preview sphere generator, MTL catalog browse/edit/create/delete (in-memory
            // only), SetTextureFor file roundtrip on a TEMP copy of default.mtl, mesh-slot reassignment,
            // and the tile grid + editor window layout. Output: %TEMP%\illusion_material_editor.txt
            case "--probe-material-editor":
                MaterialEditorProbes.RunEditorProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Material editor GPU: two concurrent GPU stacks (map viewport + preview window) and the
            // sphere thumbnail renderer against a district's extracted textures.
            // Output: %TEMP%\illusion_material_gpu.txt + illusion_material_thumb.png
            case "--probe-material-gpu":
                MaterialEditorProbes.RunMaterialGpuProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            default:
                return false;
        }
    }
}
