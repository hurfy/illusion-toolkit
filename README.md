# Illusion Toolkit

Toolkit for Mafia II (Illusion Engine): a launcher that unpacks game resources and
a 3D map editor (WPF + Silk.NET / Direct3D 11) with its own CPU pipeline for reading SDS.

## Features

- **Launcher** — pick the game folder (`pc` or the install root), bulk-unpack all `.sds`
  into a `<game>\resources` mirror, enter the map editor.
- **Map editor** — a viewport with a free camera:
  - area catalog (open-world districts + interiors) from `cityareas.bin`;
  - seasons (summer/winter: `_z` district variants + `ground_zima`);
  - "Whole map" mode — camera streaming of districts by AREA zones from `city_univers`;
  - `city_crash` layer (Translokator table) via hardware instancing;
  - scene tree with search, proxy/snow-scene filters, and per-toggle visibility;
  - debug overlay of load zones, the game's panoramic sky (FreeRide.dds).
- **MCP server** — an MCP endpoint runs for as long as the toolkit is open, wherever you are in it
  (launcher, map editor, resource editor). It listens on `http://127.0.0.1:2010/mcp` — loopback
  only, no authorization — and the launcher's status bar shows its state on the left and its
  address on the right (click the address to copy it). Point a client at it with
  `claude mcp add --transport http illusion http://127.0.0.1:2010/mcp`. It currently serves one
  tool, `ping`, as the foundation for real ones; change the port with `"McpPort"` in settings.json.
- **Blender bridge** — select meshes and press **Tab**: they open in a live Blender session
  (launched automatically with a zero-install addon) with materials and textures; edit freely
  and push back with the addon's *Push to Illusion* button (or automatically on leaving Edit
  Mode). Supported round-trips: vertex/UV/normal edits, full topology changes (LOD0 rebuild),
  object transforms, per-face material re-assignment, object deletion, and brand-new objects
  (assign them a game material first). While a session is open, non-edited meshes render
  ghosted, unselectable (the mode is modal, like Blender's own edit mode) and the title shows
  the edited count; **Tab again** (or Esc) leaves the mode — everything un-ghosts and the
  bridge objects despawn from the Blender scene (Blender itself stays up for the next Tab). Untouched geometry round-trips byte-exactly;
  Save/Build persist pushed edits into the game archives. Requires Blender 4.2+ (auto-detected;
  override with `"BlenderPath"` in settings.json). Lower LODs and collision keep their old
  shape after a topology change — those pipelines don't exist yet.

## Solution structure

```
Illusion.slnx
├── src/Illusion.Domain/        format-neutral domain model: geometry data, scene ports
│                                  (ISceneSource/IFrameNode/ISceneDocument), undo/redo
│                                  (EditHistory/IEditAction), TransformMath  [net10.0]
├── src/Illusion.Formats/       Mafia II / Mafia II DE file formats: the editable DTO model plus a
│   │                              thin facade over the native core — byte-level parsing/serialization
│   │                              lives in the native core (see THIRD-PARTY-NOTICE.txt). UI-free,
│   │                              no globals, nullable-annotated, folder == namespace  [net10.0]
│   ├── IO/ Hashing/ Compression/  endian streams, FNV (a lookup primitive kept in C#), oodle bind
│   ├── Archive/ + Handlers/       SdsArchive (open/extract/pack) + resource-handler registry
│   ├── Frames/ + ObjectTypes/     FrameResource object graph (frame objects + frame resources)
│   │   + Resources/
│   ├── Geometry/ Mathematics/     buffer pools, packed types, AABB — the packing plan itself
│   │                                 comes from the core (mf_vtx_layout)
│   ├── Materials/ + Versions/     material libraries v57 (M2) / v58 (M2 DE)
│   ├── Translokator/              city_crash instance tables
│   ├── StreamMap/ CityAreas/      StreamMapa.bin timeline, cityareas.bin streaming table
│   ├── ItemDesc/ Collisions/      typed format models (.ids/.col/.act/.nav/.nov and the rest of
│   │   Actors/ Navigation/ …         the format universe — byte-exact round-trip in the core)
│   ├── ResourceFormats/           script/sound/table/texture/XML resource payloads
│   └── Native/                    interop with the native core (NativeMethods, MfBuffer,
│                                     NativeFormats facade)
├── native/Mafia.Formats.dll    the native core (C++20, flat C ABI: mf_* exports, {ptr,len}
│                                  buffers). Every byte-level codec of every game format lives
│                                  in there; this repository consumes it, it does not build it
├── src/Illusion.Bridge/        Blender bridge core: .ilx exchange container, NDJSON control
│                                  protocol, session discovery, weld/split geometry mapping
│                                  (depends on Domain only)  [net10.0]
├── src/Illusion.Mcp/           embedded MCP server: a loopback Kestrel endpoint that exposes the
│                                  toolkit to MCP clients; tools reach live application state
│                                  through DI + IUiThreadMarshal (depends on Domain only)  [net10.0]
├── src/Illusion.Assets/        bridge: Formats → Domain  [net10.0]
│   ├── Sds/                       mesh/hierarchy loading, repack (SdsWriter), bulk unpack
│   ├── Adapters/                  frame/document adapters onto the Domain ports
│   ├── Bridge/                    Blender bridge ⇄ Formats: mesh export, push apply
│   │                                 (count-preserving + topology rebuild), object factory,
│   │                                 pool write-back
│   └── World/                     map/area/stream catalogs over the parsed game data
├── src/Illusion.Rendering/     reusable D3D11 pipeline (Silk.NET) + WPF host  [net10.0-windows]
│   ├── Gpu/ Shaders/ Passes/      device/buffers/meshes, shader family, render passes
│   ├── Scene/ Textures/           camera, frustum, picking, DDS texture library
│   ├── Gizmos/                    gizmo contracts + transform math
│   └── Controls/                  ViewportControl (D3DImage host), gizmo overlays
├── src/Illusion/               WPF shell (.exe) + composition root  [net10.0-windows]
│   ├── Views/                     windows (launcher, map editor, dialogs)
│   ├── ViewModels/                property-tab view-model (SelectionViewModel)
│   ├── Scene/                     scene-tree view-model (SceneNode + search)
│   ├── Viewport/                  D3DImageHost + collaborators (tree, catalogs, streamer,
│   │                                 selection, transform editing, geometry editing, persistence)
│   ├── Bridge/                    Blender session controller, blender.exe locator/launcher
│   ├── BlenderAddon/              the Python addon shipped beside the exe (zero-install,
│   │                                 injected via BLENDER_USER_SCRIPTS)
│   └── Diagnostics/ + Probes/     headless probes (--probe-*), split by area
└── tools/mf-schema-gen/        boundary generator: one schema file → C++ structs/io, C# DTOs/io
                                   and probe comparators (the generated files are committed)
```

Dependency direction: `Domain` ← `Formats` ← `Assets` ← `Illusion` (shell);
`Rendering` sits beside `Formats` on top of `Domain`. The UI talks to scenes
through the Domain ports — only the `Assets` bridge (and the probes) touches `Formats`.
Every project follows folder == namespace, one type per file.

Supported games: Mafia II (classic) and Mafia II: Definitive Edition — both SDS v19;
the DE adds `.sds.patch` files, Oodle-compressed blocks (read) and material library v58.
The write path always produces classic-compatible zlib archives.

The game path is set in the launcher and saved to `%LOCALAPPDATA%\Illusion\settings.json`.

## Build

Requires .NET SDK 10 (see `global.json`) and Windows x64 (WPF, D3D11, native Oodle).

```powershell
dotnet build Illusion.slnx
dotnet run --project src/Illusion
```

The native core reaches the output one of two ways, picked automatically:

| Mode | When | Needs |
|---|---|---|
| **Prebuilt** (default here) | the shipped `native/Mafia.Formats.dll` is used as is | nothing beyond the .NET SDK |
| **Source** | the core's sources are at hand (they are not part of this repository) | Visual Studio with "Desktop development with C++" + the bundled CMake/Ninja; `Illusion.Formats.csproj` then drives cmake, so `dotnet build` stays the only command |

Working on the C# side needs nothing else: the core in `native/` is the released build, and every this toolkit reads and writes goes through it. Sources, when someone has them, are looked for `MfCoreSource` (property or environment variable), then `src/Mafia.Formats`, then
`../illusion-core/src/Mafia.Formats`. Force either path with `-p:MfCoreMode=Source|Prebuilt`.

A directory named by `MfCoreSource` is validated before cmake runs — marker files, the CMake project
name, and `MF_ABI_REV` against the facade's `ExpectedAbiRev` — and a failure is an error rather than a
silent fall back to the prebuilt DLL. At run time the loader repeats the revision check against
whatever `Mafia.Formats.dll` is actually beside the application, so a stale core is refused with an
explanation instead of surfacing later as a missing export.

## Headless probes

Diagnose load chains without the UI; reports are written to `%TEMP%\illusion_*.txt`.
The game path from settings is used (open the game in the launcher once first).

| Argument | What it checks |
|---|---|
| `--probe-sds [path.sds]` | reading a single SDS: meshes, vertices, bounds, textures |
| `--probe-roundtrip [filter]` | regression net: every archive re-serializes identically + FrameResource generations are byte-equal |
| `--probe-extract [filter]` | extraction parity against reference extracted folders |
| `--probe-formats <ids\|col\|act\|nav\|nov>` | typed format ports: bulk-parse + byte-exact round-trip across the install |
| `--probe-save` | end-to-end save: edit transform → save FrameResource → reread → restore → pack a temp SDS |
| `--probe-backup` | build/backup flow: versioned backups next to the repacked archive |
| `--probe-restore` | restore-from-backup: strict backup listing, atomic archive swap, extracted-mirror delete |
| `--probe-map` | location catalog (Location × Season) |
| `--probe-areas` | AREA boxes (load zones) from `city_univers` |
| `--probe-stream` | streaming zones and district lookup by position |
| `--probe-scenes [district]` | a district's scenes and their categories (proxy/snow/normal) |
| `--probe-flags [district]` | FrameNameTable flags: link flags→objects (100% by name) + flag×proxy/snow cross-tab |
| `--probe-flagtree [district]` | how proxy/snow-flagged objects group under scene folders (+cascade check) |
| `--probe-crash [winter]` | the `city_crash` chain: Translokator → instances |
| `--probe-async` | background district loading: chunked crash instances, deferred GPU attach |
| `--probe-streammap` | StreamMapa.bin catalog (script/cutscene timeline) |
| `--probe-gpu` | GPU smoke test: shader compilation + instanced draw |
| `--probe-modes` | render modes (Render/MaterialPreview/Solid/Wireframe) |
| `--probe-select` | selection math: ray-picking + gizmo transform ops + Euler round-trip |
| `--probe-outline` | selection outline renderer |
| `--probe-gizmo` | move/rotate/scale gizmo interactions + snapping |
| `--probe-edit` | undo/redo history over object edits |
| `--probe-panel` | editable transform panel round-trip |
| `--probe-dialog` | AppDialog confirm/result component |
| `--probe-ui` | UI smoke: the Vector3Box control loads + its copy/paste format round-trips |
| `--probe-mcp` | embedded MCP server: a real client discovers and calls the tool over streamable HTTP; a busy port is reported, not thrown |
| `--probe-native` | native core handshake: version/ABI match, 10 MB echo through the buffer protocol, readable errors, double-free refusal, thread-local error isolation |
| `--probe-native-core` | native core primitives on real data: FNV-1 32/64 against the retained C# hash helpers; XTEA unwrap of wrapped archives (incl. partial tails) yields a loadable archive; zlib both ways; the oodle shim binds the game's oo2core when present |
| `--probe-schema [repoRoot]` | generated-model cycle: hostile model survives C#→native→C# bit-exactly, wire strictness, committed generated code matches a fresh regeneration |
| `--probe-golden <snap\|check> <repoRoot>` | golden snapshot: hash the decoded neutral model of every archive/format file in the install; `snap` writes `docs/golden-snapshot.txt`, `check` re-verifies it — the cross-run judge for mirrored read/write bugs |
| `--probe-native-fnt` | FrameNameTable: every extracted `.fnt` parses and re-saves byte-identically (on-disk fixpoint) |
| `--probe-native-fr [filter]` | FrameResource: every extracted `.fr` regenerates byte-for-byte through the native pipeline (on-disk fixpoint) + generation stability |
| `--probe-native-pools` | buffer pools: every extracted `.ibp`/`.vbp` round-trips byte-identically (on-disk fixpoint) |
| `--probe-native-vtx` | vertex codec: the narrow viewport-channel decode matches the full decode bit-for-bit, and recompression is byte-identical, across every extracted buffer |
| `--probe-native-lod` | LOD builder: the OPCODE/split capsules still hash to their pinned bytes (400-case matrix + the slotless placeholder) and survive a FrameResource write/read |
| `--probe-native-mtl` | material libraries: read/write parity + on-disk fixpoint across v57 (M2) / v58 (M2 DE) |
| `--probe-native-misc` | small formats (`.ids`/`.act`/`.nav`/`.nov`/`.tra`/cityareas/streammap): bulk parse + byte-exact round-trip across the install |

## Viewport controls

RMB — look around · WASD — fly · Shift — speed up · Space/Ctrl (E/Q) — up/down.
Camera position and speed are editable in the bottom panel.

## Third-party code

The file-format knowledge in the native core shipped as `native/Mafia.Formats.dll`
originates in a rewritten derivative of the
[MafiaToolkit](https://github.com/Greavesy1899/MafiaToolkit) parser, with the
ItemDesc/Collision/Actors/NAV parsers ported from the MafiaToolkitV2 specs.
Details are in `THIRD-PARTY-NOTICE.txt` (repo root). For Oodle archives at
runtime (Mafia II DE), the native core binds `oo2core_8_win64.dll` from the game
folder (proprietary; not included in the repository).
