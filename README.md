# POMSix

Parallax occlusion mapping for SPT terrain — flatscreen + VR. BepInEx 5 plugin, FogSix-style project.

## How Tarkov terrain works (recon findings, 2026-07-19)

- All terrain is **MicroSplat** (`JBooth.MicroSplat.Core.dll` in Managed — decompile THAT, not Assembly-CSharp).
  Unity `Terrain` maps use `MicroSplatTerrain`, mesh ground (Streets-style) uses `MicroSplatMeshTerrain`.
- BSG customized it with seasons: each terrain holds 6 season materials × 3 quality tiers (`MaterialsSet`),
  picked by the statics `MicroSplatObject.currentSeason/currentQuality`, all applied through `Sync()`.
- `Sync()` instantiates `templateMaterial` onto `terrain.materialTemplate` / `meshRenderer.sharedMaterial`
  and fires the **public `OnMaterialSync(Material)` event** — our re-apply hook, no Harmony needed.
- MicroSplat packs **per-layer height in the `_Diffuse` Texture2DArray alpha** — the POM height data
  already ships with the game.
- BSG's runtime declares `_POMParams`/`_ParallaxParams`/`_TessData2` property IDs → they own the
  MicroSplat **Tessellation & Parallax module**. Whether the shipped generated shaders have POM compiled
  in (vs generated without it) is what the v0 recon answers.

## Recon results (Customs, 2026-07-19) — the spec for the replacement shader

POM is NOT compiled into the shipped shader (`_POMParams`/`_ParallaxParams`/`_TessData1/2` all absent)
→ we regenerate it. The good news: the terrain shader is nearly minimal MicroSplat.

- Shader `MicroSplat/Slice_1_1` (shared by every terrain slice on the map), queue 1900, 4 passes.
- Keywords (the FULL generation feature set):
  `_MICROSPLAT _MICROTERRAIN _USEGRADMIP _PERTEXUVSCALEOFFSET _PERTEXBRIGHTNESS _PERTEXCONTRAST _MSRENDERLOOP_SURFACESHADER`
  — no streams/snow/proctex/global/anti-tile/triplanar. `_USEGRADMIP` = tex2Dgrad sampling (exactly
  what POM's UV offsets need to stay mip-correct).
- Properties (12): `_Control0..3` (12 splat layers over 3 live controls + black 4th), `_Diffuse`
  (Texture2DArray 1024²×12 **DXT5 → per-layer height in alpha confirmed**), `_NormalSAO` (DXT5 array),
  `_PerTexProps` (32×32 float PropTex), `_TerrainHolesTexture`, `_PerPixelNormal` (terrain normal RT —
  terrain is drawInstanced), `_Contrast` (height-blend contrast, 0.492), `_UVScale` (233.33), `_MainTex`.
- Game Unity version: **2022.3.43f1** (from globalgamemanagers).

## Architecture

1. **Runtime (DONE, in this repo):** Harmony postfix on `MicroSplatTerrain.Sync` +
   `MicroSplatMeshTerrain.Sync` — the single funnel every material application passes through (spawn,
   streamed slices, season/quality swaps) — swaps `matInstance.shader` to the bundle shader
   (renderQueue preserved) and sets the POM params from config. Identical property names mean every
   texture/param rebinds automatically on the swap. Missing bundle = recon-only mode, logged.
2. **Shader (TODO, Unity project):** regenerate the same shader with MicroSplat + POM — see checklist.
3. **VR:** nothing special — POM is a tangent-space material effect; MultiPass renders each eye with its
   own camera position so stereo parallax is correct by construction. Cost is ×2 → distance fade is the
   main perf knob.

## Build

```sh
dotnet build POMSix.csproj -c Debug   # auto-deploys to F:\SPT4.0\BepInEx\plugins\POMSix
```
