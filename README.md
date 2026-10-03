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

## Architecture plan

1. **v0 (current): recon.** At raid load, dump every `MicroSplatObject`: the `keywordSO` keyword list
   (= the generation-time feature set of BSG's shader), full shader property table, texture formats,
   and a `HasProperty("_POMParams")` check. Run a raid on 1–2 maps, read `BepInEx/LogOutput.log`.
2. **If POM is already compiled in:** enable it per-material (set `_POMParams`, distance fade) from the
   `OnMaterialSync` hook. Done.
3. **If not:** regenerate the shader in the Unity bundle project with MicroSplat (same version/modules,
   keyword list from recon, + parallax enabled), ship it as an AssetBundle (`pom` bundle, volfog/ssr
   pipeline), and hot-swap `matInstance.shader = pomShader` on every `OnMaterialSync` — identical
   property names mean every texture/param rebinds automatically. Season/quality swaps re-fire the
   event, so the swap survives them.
4. **VR:** nothing special — POM is a tangent-space material effect; MultiPass renders each eye with its
   own camera position so stereo parallax is correct by construction. Cost is ×2 → distance fade is the
   main perf knob.

## Build

```sh
dotnet build POMSix.csproj -c Debug   # auto-deploys to F:\SPT4.0\BepInEx\plugins\POMSix
```
