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

## Unity-side checklist (regenerating the shader with POM)

Unity project: `F:\Unity Game Project\tarkov stuff` (2022.3.22f1, built-in RP). MicroSplat installed as
UPM packages: `com.jbooth.microsplat.core` **3.9.0** + `com.jbooth.microsplat.tessellation` — the SAME
version line as BSG's runtime (their tess module reports "3.9"), so generated property/PropTex-row
layouts match the game exactly.

1. Terrain with 12 layers → add `MicroSplatTerrain` component → **"Convert to MicroSplat"** (creates
   `Assets/MicroSplatData/` with material, keywords asset, texture array config).
2. Material features: tess module **Parallax = POM**, **Displacement Mode = None**. In Per Texture
   Properties, nudge **UV Scale**, **Brightness**, **Contrast** once each (auto-enables
   `_PERTEXUVSCALEOFFSET/_PERTEXBRIGHTNESS/_PERTEXCONTRAST`).
   Do NOT touch per-texture **Parallax Height** (`_PERTEXPARALLAX` reads a PropTex row BSG never
   authored → garbage multipliers) or per-tex Height Offset/Contrast (same reason, row 10.5).
3. Conversion defaults add `_BRANCHSAMPLES _BRANCHSAMPLESAGR` — BSG doesn't have them; turn Branch
   Sampling off for parity (possible later perf experiment).
4. Verify `MicroSplat_keywords.asset` = the 7 game keywords + `_POM`, nothing else.
5. **Shader stripping (CRITICAL — invisible-terrain trap, hit 2026-07-19):** Edit → Project Settings →
   Graphics → Shader Stripping: **Instancing Variants = Keep All**, **Lightmap Modes = Custom (all
   checked)**, **Fog Modes = Custom (all checked)**. The instanced-terrain vertex path (the heightmap
   fetch that gives the terrain its shape) only exists in the INSTANCING_ON variants, and the default
   build strips them (no material in the bundle) — result was a 90KB bundle from a 1MB shader, an
   empty native property table, and invisible terrain. Expect the fixed bundle to be multi-MB.
   `PomApplier` fail-safes this: a post-swap native `HasProperty` check reverts a broken bundle.
6. **Tools → POMSix → Build POM Bundle** ([Assets/BuildPomBundle.cs](Assets/BuildPomBundle.cs), lives in
   the Unity project's `Assets/Editor/`) — auto-finds the generated shader (ignores `*_Base`), builds
   the `pom` bundle, deploys to `F:\SPT4.0\BepInEx\plugins\POMSix\Assets\pom`.

`_POMParams` packing (VERIFIED from module source): `(height, fadeStart, fadeDistance, maxSteps)` —
fade is `1-saturate((dist-y)/z)`, steps lerp 4→w by view angle. `PomApplier.ApplyParams` is final.

**Height source (probed in-game 2026-07-19): the game's `_Diffuse` alpha is FLAT** (~11-15/255,
std ~1-3 on every layer — BSG never baked height maps; even their height blending degenerates to
weight blending). Stock POM therefore shows nothing. The build script POST-PATCHES the generated
shader: `SampleHeightsPOM0/1` derive height from `sqrt(dot(albedo.rgb, luma))` (perceptual
luminance — bright pebbles high, dark crevices low) instead of `.a`. Splat blending is left
untouched (vanilla look). The patched copy lives at `Assets/POMSix/POMSix_Terrain.shader`
(`Shader "POMSix/Terrain"`) and is what gets bundled — regenerate + rebuild re-applies the patch.
Recon lesson: AsyncGPUReadback can't read BC3 — blit each layer (depth-slice Blit) to an
uncompressed RT first.

Editor preview note: with no height maps assigned in the texture array config, POM shows nothing in
the editor (flat alpha) — that's fine, the GAME's arrays carry real heights. Assign a height texture
to one layer only if you want to eyeball it in-editor.

Open: recon Woods + Streets (mesh ground = `MicroSplatMeshTerrain`, may need the Mesh Terrains module
and a different keyword set) — run a raid there with `Debug/Recon Dump` on and check the log.

## Build

```sh
dotnet build POMSix.csproj -c Debug   # auto-deploys to F:\SPT4.0\BepInEx\plugins\POMSix
```
