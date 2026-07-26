# Illusion Toolkit

A map editor and resource toolkit for **Mafia II** and **Mafia II: Definitive Edition** — the
Illusion Engine games. It unpacks the game's SDS archives, streams whole districts into a D3D11
viewport, and edits what it finds there: object transforms and properties, materials, collision
hulls, entire meshes through a live Blender bridge — then packs it all back into archives the game
accepts.

Everything it writes is verified against the retail data: the format layer round-trips real game
files byte-for-byte, and a golden snapshot of every archive in the installation guards against
silent drift.

> **You need your own copy of Mafia II.** No game content is distributed here.

---

## Requirements

| | |
|---|---|
| OS | Windows x64 (WPF + Direct3D 11) |
| Runtime | .NET SDK **10.0.301** or newer in the same major version (`global.json`, `rollForward: latestFeature`) |
| Native runtime | **Microsoft Visual C++ 2015–2022 x64 redistributable** — the bundled `native/Mafia.Formats.dll` imports `MSVCP140.dll` / `VCRUNTIME140.dll` |
| GPU | any Direct3D 11 capable adapter |
| Game | Mafia II (classic) or Mafia II: Definitive Edition, installed |

Optional, per feature:

- **Blender 4.2+** — for the mesh bridge (auto-detected; override with `BlenderPath` in settings).
- **NVIDIA PhysX System Software with the legacy v2.8.0 engine** — only needed to change a
  collision hull's *shape*. Without it, hull re-cooking is disabled and everything else works.
- **Mafia II: DE** archives additionally need the game's own `oo2core_8_win64.dll` (Oodle). It is
  read from your install; it is proprietary and is not distributed here.

---

## Getting started

```powershell
git clone https://github.com/hurfy/illusion-toolkit
cd illusion-toolkit
dotnet build Illusion.slnx
dotnet run --project src/Illusion
```

Nothing beyond the .NET SDK is required to build: the native core ships as a prebuilt DLL in
`native/`.

On first run the launcher asks for the game folder — point it at the install root or its `pc`
folder — and unpacks every `.sds` into a `<game>\resources` mirror. That mirror is what the editor
reads and writes; the original archives are only touched when you press **Build**, and always with
a timestamped backup first.

Settings live in `%LOCALAPPDATA%\Illusion\settings.json`: `GamePath`, `BlenderPath`,
`BridgeAutoPush`, `McpPort`, `SuppressBuildNotice`.

---

## What it does

### Launcher

Pick the game folder, bulk-unpack all archives with a live progress bar, then enter the map editor.
The last path is remembered. *(A "Resource Editor" tile exists but is a disabled stub.)*

### Viewport and streaming

- Area catalog of open-world districts and interiors, from `cityareas.bin`.
- **Whole map** mode — districts stream in and out by AREA zones as the camera moves.
- **Seasons** — winter loads the `_z` district variants and `ground_zima`.
- Additive overlay toggles that never reload the scene: `city_crash` instance layer (hardware
  instancing), collision hulls, `.nov` AI navigation graph and AI-mesh, `.nav` path objects
  (cover / vault-over markers), and district load zones.
- Four shading modes: **Render** (textures + normal/specular maps), **Material Preview** (diffuse
  only, the default), **Solid**, **Wireframe**.
- Optional filters for proxy scenes, embedded proxy meshes and snow-only geometry.
- **Play** launches the game; **Multiplayer** launches M2Online when it is installed.

### Scene tree

Search-as-you-type with automatic expansion to matches, per-node visibility eyes, type-keyed icons
(archive, scene, mesh, model, light, camera, collision, navigation), and live counters for loaded
files, meshes and polygons. FPS, draw calls and drawn instances sit in the status bar.

### Editing

- Click to select in the viewport or the tree; **Ctrl+click** to multi-select.
- **Move / Rotate / Scale** gizmos with **Shift** to snap (1 unit, 15°, 0.1); a floating panel with
  editable X/Y/Z appears for whatever you just changed.
- **Undo/redo** across everything, shared with the Material Editor window.
- **Delete** and **Duplicate** objects and collision placements as single undoable actions.
  *Duplicate currently supports static single-mesh objects; other object types are skipped.*
- **Reparent** an object anywhere in the hierarchy, with its own subtree excluded so cycles are
  impossible.
- **Property panel** — position/rotation/scale, the object name (rewritten into the FrameNameTable
  on save), frame-table flags, and type-specific fields for meshes, models, lights, cameras,
  joints, dummies, sectors and more.
- **Import** (`Ctrl+I`) reads glTF (`.glb`/`.gltf`) into a chosen loaded archive; meshes named
  `COL_*` become collision hulls, the rest become render meshes, and missing game materials can be
  created automatically.

### Materials

A tile grid of sphere-preview thumbnails per object, and a full editor window: browse and search a
library, create, rename (the FNV64 hash is re-derived), delete, assign to a mesh slot, edit texture
slots with live thumbnails, and edit every known shader parameter — including ones the material
does not carry yet. The preview sphere is rendered with the map's real textures and sky.

### Collisions

Hulls render as a translucent overlay and behave like ordinary objects: select, move, rotate,
delete, duplicate. **Scaling** re-cooks a real PhysX triangle mesh through the vendored
`tools/M2PhysX/M2PhysX.exe`; if the cooker is unavailable or refuses, the hull snaps back and
nothing is written. **Remove unused hulls** sweeps hulls no placement references. Authoring a
brand-new hull shape happens in Blender and comes back through the bridge.

### Blender bridge

Select meshes and/or collision placements and press **Tab**: they open in a live Blender session
(launched automatically, zero-install addon) with materials and textures. Edit freely and push back
with *Push to Illusion*, or automatically on leaving Edit Mode. One push can carry geometry edits,
full topology rebuilds, transforms, deletions, new objects and reshaped or brand-new collision
hulls in a single undoable batch. While a session is open the rest of the scene renders ghosted and
unselectable — **Tab** again or **Esc** leaves.

Limits worth knowing: untouched geometry round-trips bit-exactly (that is how a real reshape is
told apart from an untouched one); a topology rebuild leaves lower LODs and collision with the old
shape; collision placements refuse scale and mirror pushes (resize the hull with the toolkit's own
gizmo instead); up to 128 collision placements per press.

### Saving, building, backups

**Ctrl+S** writes edited FrameResources back to the extracted mirror. **Build** repacks every
edited archive into its `.sds`, creating a timestamped versioned backup first; archives are packed
independently, so one failure (the game holding a file open, say) does not block the rest.
**Restore Backup** rolls a single archive back to an earlier version — replacing both the live
`.sds` and its extracted mirror — from the File menu, the tree's context menu or the viewport's.
*(Material-library edits are not covered by the backup flow.)*

### MCP server

An MCP endpoint runs for the lifetime of the application at `http://127.0.0.1:2010/mcp` — loopback
only, no authorization — with its live status in the launcher's status bar. Point a client at it
with `claude mcp add --transport http illusion http://127.0.0.1:2010/mcp`. It currently serves a
single `ping` tool as the foundation for real ones; change the port with `McpPort` in settings.

---

## Controls

| Input | Action |
|---|---|
| **Middle mouse drag** | look around |
| **W A S D** | fly forward/back, strafe |
| Camera speed field (status bar) | flight speed in units/s, default 100 |
| **Left click** / **Ctrl+left click** | select / add to selection |
| **Shift** during a gizmo drag | snap to 1 unit · 15° · 0.1 |
| **Ctrl+Z** / **Ctrl+Shift+Z** | undo / redo |
| **Del** · **Ctrl+D** | delete · duplicate |
| **Ctrl+S** · **Ctrl+I** | save · import glTF |
| **Tab** · **Esc** | open / leave a Blender bridge session |
| Navigation ball (top-right) | click snaps the camera to an axis, drag orbits |

There is no vertical-movement key and no speed modifier: gain altitude by pitching while flying,
and set the speed in the status bar. Camera position is editable and paste-friendly there too.

---

## How it is put together

```
Illusion.slnx
├── src/Illusion.Domain/      format-neutral domain model: geometry, scene ports
│                               (ISceneSource/IFrameNode/ISceneDocument), undo/redo, transform math
├── src/Illusion.Formats/     the editable model of every game format plus the facade over the
│                               native core — DTOs and P/Invoke, no parsing of its own
├── src/Illusion.Rendering/   reusable D3D11 pipeline (Silk.NET) and the WPF viewport host
├── src/Illusion.Assets/      the bridge from Formats to Domain: mesh/hierarchy loading, repacking,
│                               world catalogs, Blender push/export
├── src/Illusion.Bridge/      Blender exchange container (.ilx) and NDJSON control protocol
├── src/Illusion.Mcp/         the embedded MCP endpoint (loopback Kestrel)
├── src/Illusion/             WPF shell, viewport host, editors, dialogs, headless probes
├── native/Mafia.Formats.dll  the native core — every byte-level codec, shipped as a binary
├── tools/M2PhysX/            the PhysX 2.8 triangle-mesh cooker (see its own README)
└── docs/golden-snapshot.txt  hashes of every decoded archive and format file in the installation
```

Project references: `Domain` and `Formats` depend on nothing in-repo; `Bridge`, `Mcp` and
`Rendering` each depend on `Domain`; `Assets` depends on `Domain`, `Formats` and `Bridge`; the
`Illusion` shell depends on all six. Every project follows folder == namespace, one type per file.

Both games are SDS v19. DE adds `.sds.patch` files, Oodle-compressed blocks (read) and material
library v58; the write path always produces classic-compatible zlib archives. Console (big-endian)
archives are not supported.

### The native core

Every byte-level codec lives in a separate C++20 core behind a flat C ABI (`mf_*` exports,
`{ptr,len}` buffers): the SDS container and its resource envelopes, FrameResource, materials,
collisions, navigation and the rest — **35 formats, 31 of them read/write and byte-exact**.
**This repository consumes that core; it does not build it.** Its sources are not part of this
repository.

| Mode | When | Needs |
|---|---|---|
| **Prebuilt** (the default) | `native/Mafia.Formats.dll` is used as shipped | nothing beyond the .NET SDK |
| **Source** | the core's sources are present beside this repo or named by `MfCoreSource` | Visual Studio with "Desktop development with C++" and its bundled CMake/Ninja; `dotnet build` then drives CMake itself |

Force either with `-p:MfCoreMode=Source|Prebuilt`. In Source mode the core is validated before
CMake runs — marker files, the CMake project name, and its `MF_ABI_REV` against the facade's
`ExpectedAbiRev` — and a bad `MfCoreSource` is an error, never a silent fallback. At run time the
loader repeats the revision check against whichever DLL is actually beside the application, so a
stale core is refused with an explanation rather than surfacing later as a missing export.

---

## Verification

There is no mock data anywhere: the toolkit verifies itself against a real installation. Headless
probes run as `Illusion.exe --probe-<name>` and write their report to `%TEMP%\illusion_*.txt`
(open the game in the launcher once first, so the path is known).

| Probe | What it proves |
|---|---|
| `--probe-native` | core handshake: ABI/version match, a 10 MB round trip through the buffer protocol, readable errors, double-free refusal, thread-local error isolation |
| `--probe-native-core` | core primitives on real data: FNV parity, XTEA unwrap of wrapped archives, zlib both ways, the Oodle bind when the game provides it |
| `--probe-native-fr` | every extracted FrameResource is generation-stable: write → re-read → write again produces identical bytes |
| `--probe-native-fnt` | every extracted FrameNameTable re-saves byte-identically to the file on disk |
| `--probe-native-vtx` | the packed-vertex codec: channel decode matches the full decode bit-for-bit and recompression is byte-identical |
| `--probe-native-mtl` | material libraries v57/v58: read/write parity plus an on-disk fixpoint |
| `--probe-native-misc` | the small formats (`.ids`, `.act`, `.nav`, `.nov`, `.tra`, city tables) round-trip byte-exact across the install |
| `--probe-formats` | typed format ports in bulk, with a byte-exact round trip per file |
| `--probe-collision-*` | the collision chain: decode, materials, scaling, widening, cooking, editing, picking |
| `--probe-golden snap\|check` | hashes the decoded model of every archive and format file in the install — the cross-run judge for mirrored read/write bugs |
| `--probe-save` / `--probe-backup` / `--probe-restore` | the full persistence path: edit → save → repack → back up → restore |
| `--probe-bridge-*` | the Blender exchange: payloads, welding, topology rebuilds, transforms, new objects, collisions |
| `--probe-select` / `-edit` / `-properties` / `-duplicate` | editing math, undo history, the property panel, deep copies |
| `--probe-mcp` | a real MCP client discovers and calls the tool over streamable HTTP |
| `--probe-schema` | the generated boundary model survives a hostile round trip; skipped when the private core is not beside the tree |

That is a selection: `ProbeRunner.cs` dispatches **90** of them, including the whole
`--probe-collision-*` and `--probe-bridge-*` families, the import and reparent flows, packing
checks, and performance gates.

Two practical notes. A probe writes its verdict **into the report, not the exit code** — the
process always exits cleanly, so a CI wrapper has to read the text (some probes report
`RESULT: PASS`, the newer ones a `[PASS]`/`[FAIL]` line per check plus a summary). And probes that
need something you may not have — Blender, the PhysX cooking runtime, a DE-only Oodle DLL — report
a clean SKIP rather than a failure.

---

## Known limits

- The Resource Editor tile is a stub.
- Duplicating frame objects covers static single-mesh objects only.
- A topology rebuild does not regenerate lower LODs or collision for that object.
- `.sds.patch`, `.tra`, `cityareas.bin` and `StreamMap*.bin` are read-only.
- Console (big-endian) archives are refused.
- Material-library edits are outside the backup/restore flow.
- The MCP server exposes only `ping` so far.
- Navigation overlays (`.nav`, `.nov`) are view-only.

---

## Contributing

Issues and pull requests are welcome for everything in this repository: the shell, the viewport and
renderer, the editing flows, the domain model, the probes.

**Byte-level format bugs cannot be fixed from here.** Every codec lives in the closed native core,
so a wrong field, a failed round trip or an unsupported format is a bug report, not a patch — the
most useful report names the file, what the toolkit did with it, and the probe output that shows
it. If a change needs a matching core change, say so in the issue and it will be handled on that
side; the ABI revision is bumped in lockstep and a mismatched pair refuses to load by design.

Please keep the house style: English only, folder == namespace, one type per file, no warnings
(the build treats them as errors, including unused usings and stale doc references), and
`dotnet format` clean.

---

## License

This repository is MIT licensed — see [LICENSE](LICENSE).

---

## Credits

The format work this toolkit is built on:

- **[MafiaToolkit](https://github.com/Greavesy1899/MafiaToolkit)** by Greavesy (MIT) — the parser
  the native core was rewritten from, the specifications behind the ItemDesc, Collision, Actors and
  NAV codecs, and the origin of `tools/M2PhysX/M2PhysX.exe`, the PhysX 2.8 cooker vendored here
  (see [its README](tools/M2PhysX/README.md)); it carries no NVIDIA code and needs NVIDIA's own
  PhysX runtime installed to work.
- **Gibbed.Illusion / Gibbed.Mafia2** by Rick Gibbed (zlib) — the earliest work on these formats.
- **OPCODE** by Pierre Terdiman — the collision trees; the cooked-mesh layout mirrors NVIDIA
  PhysX 2.x.
- **[zlib](https://zlib.net/)** by Jean-loup Gailly and Mark Adler — compiled into the native core.
- **[Silk.NET](https://github.com/dotnet/Silk.NET)** for Direct3D 11 and the
  **[ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)** for the MCP
  endpoint.

Oodle (`oo2core_8_win64.dll`, Mafia II DE only) is proprietary and is not distributed here — it is
loaded from your own game installation.
