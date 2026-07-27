# M2PhysX.exe — the PhysX 2.8 triangle-mesh cooker

Mafia II stores collision as PhysX 2.8 *cooked* triangle meshes: vertices plus an OPCODE broadphase tree,
in a format produced by a closed NVIDIA library. Changing a hull's **shape** therefore means cooking it
again, and nothing in this repository can do that — an OPCODE tree written by us would be a structure the
game has never seen, verifiable only by playing it.

This 18 432-byte executable is a thin wrapper over the genuine PhysX 2.8 cooking library. It is the same
binary that produced every modded `.col` the game has accepted for years.

## Provenance

Taken verbatim from **Greavesy's MafiaToolkit** (`Mafia2Libs/libs/M2PhysX.exe`), MIT licensed — see
`LICENSE.MafiaToolkit`. Sources live in that repository under `M2FBX/M2PhysX/Source/` and contain no
NVIDIA code; they call `NxGetCookingLib` / `NxCookTriangleMesh` against the PhysX SDK.

**It cannot be rebuilt from that checkout.** The project references a `SDKs/` folder (PhysX 2.8 headers,
`PhysXLoader.lib`, `NxCooking.lib`) that is not present, and NVIDIA no longer distributes the PhysX 2.8
SDK. The binary is effectively irreplaceable, which is why it is committed here rather than referenced
from a path on one machine.

## Runtime requirement

The exe itself imports only `PhysXLoader.dll` plus the MSVC runtime. The loader then finds the *engine*
through the registry — `HKLM\SOFTWARE\WOW6432Node\AGEIA Technologies` → `PhysXCore Path` →
`Engine\v2.8.0\PhysXCore.dll` — **not** through `PATH`, and **not** from DLLs placed next to the exe
(measured: local copies of the engine are ignored). So cooking requires NVIDIA's freely distributed
*PhysX System Software*, with the legacy **v2.8.0** engine present.

No NVIDIA binaries are bundled here. When the runtime is missing the toolkit says so and disables hull
shape editing; everything else keeps working.

## Interface

```
M2PhysX.exe -CookTriangleMesh      <in.bin> <out.bin>
M2PhysX.exe -MultiCookTriangleMesh <in.bin> <out.bin>
```

Input model: `u32 numVertices`, `float3[]`, `u32 numIndices` (= triangles × 3), `u32[]`,
`u32 numMaterialIds` (= triangles), `u16[]` (one raw PhysX surface id per triangle). The batch form
prefixes a `u32` model count and concatenates models; its output does the same with the cooked blobs.

## Traps, all measured

- **Exit code 0 does not mean success.** A failed cook (no triangles, fully degenerate geometry) also
  exits 0, prints `Failed to cook TriangleMesh`, and leaves a **zero-byte** output file. Success must be
  read from the output: non-empty, `NXS\x01MESH` magic, and it must decode.
- Running it with no arguments crashes — `argv[1]` is read before `argc` is checked.
- It picks the **narrowest** index width the vertex count allows (8-bit up to 255 vertices, 16-bit up to
  65535). Shipped Mafia II data is always 32-bit, so cooked output must be widened before use.
- Output is byte-deterministic: identical input yields identical bytes, single and batch alike.
