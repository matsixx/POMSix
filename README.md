# POMSix

Real depth for Tarkov's ground. POMSix adds parallax occlusion mapping to the terrain in Single Player Tarkov,
so stones, gravel and rubble read as actual relief instead of flat textures. Works on flatscreen and in VR.

Support my work on Ko-fi: https://ko-fi.com/matsix

Tarkov's terrain never shipped with height maps, so there was nothing for parallax to work with. POMSix builds
real height maps from the game's own normal maps when a raid loads. It then swaps the terrain onto a version
of the game's own MicroSplat shader with parallax added. The look of the ground stays vanilla; it just gains depth.

## Features

- **Parallax relief** on all of the game's MicroSplat terrain, fading out with distance so it only costs where you can see it.
- **Height maps made from the game's own textures**, generated in the background and cached per map.
- **Self-shadowing:** stones cast soft shadows into the gaps along the sun or moon direction.
- **Height AO:** pits and crevices darken ambient light.
- **Height blending:** stones poke through dirt and sand fills the gaps, instead of the game's flat cross-fade between ground materials.
- **Depth offset:** objects sink into the relief instead of clipping at a flat line, and sun shadows fall across it.
- **Road relief:** roads, paths and sidewalks get parallax from heights generated out of their own normal maps, the same way the terrain does. The terrain carve is flattened under roads so their edges stay clean.
- **Three displacement modes:** POM (default), Tessellation and Hybrid.
- **Correct in VR:** each eye gets its own parallax.

## Requirements

- SPT 4.1. Other versions are untested.

## Install

1. Extract the release into your SPT folder. You should end up with:
   ```
   BepInEx/plugins/POMSix/POMSix.dll
   BepInEx/plugins/POMSix/Assets/pom
   ```
2. Launch the game.

The first raid on each map builds that map's height maps in the background. The ground uses a rougher
stand-in until they're ready, then sharpens. The result is cached in `BepInEx/plugins/POMSix/HeightCache/`,
at about 25 MB per map, so later raids load it straight away. Delete that folder to regenerate the maps.

## Settings

Open the in-game configuration manager (F12) or edit `BepInEx/config/com.matsix.pomsix.cfg`.
Every setting applies live, mid-raid.

| Setting | Default | What it does |
|---|---|---|
| Enabled | On | Turning it off restores the game's own terrain shader. |
| Displacement Mode | POM | POM, Tessellation or Hybrid. See below. |
| Height | 0.045 | How deep the relief goes. |
| Fade Start / Fade Distance | 15 / 20 | Where the effect starts fading and how far the fade runs. **The main performance setting.** |
| Steps | 32 | Ray-march quality. Matters most when looking along the ground. |
| Smoothing | 0 | Rounds the relief into softer shapes. |
| Self Shadow | 0.1 | Strength of the stone shadows. 0 is off. |
| Height AO | 1 | Strength of the crevice darkening. 0 is off. |
| Relief Balance | 1 | 1 gives each material its own depth (sand stays flat, rubble goes deep). 0 gives all materials the same depth. |
| Grazing Depth | 0.7 | Relief depth when looking along the ground. 1 is physically correct. Lower it if very low angles show banding. |
| Height Blending | 1 | Blends ground materials by height. 0 is the vanilla blend. |
| Depth Offset | On | Lets objects, shadows, fog, AO and reflections see the displaced ground. |
| Roads | On | Parallax on roads, paths and sidewalks. |
| Flatten Under Roads | On | Flattens the tessellated terrain under and beside roads (Tessellation and Hybrid). POM's visible relief is unchanged. |
| Road Relief | 1 | Strength of the road relief. 1 is the depth the road textures' own normal maps describe; higher exaggerates. |

The Advanced section has performance and debug options, such as a cheaper quadtree march and debug views.

### Displacement modes

- **POM** (recommended): per-pixel parallax. The cheapest mode and the cleanest look.
- **Tessellation:** the terrain mesh is really subdivided and displaced near you, giving true silhouettes. Fine detail is softer, and it costs more.
- **Hybrid:** tessellation builds the large shapes and POM carves the fine detail. The heaviest mode.

Tessellation and Hybrid have their own Displacement and Triangle Size settings. They can show terrain
relief shading through road edges on some maps, which POM doesn't.

## Performance

POM costs per pixel, and VR renders every pixel twice. If you need frames back, lower **Fade Start** and
**Fade Distance** first, then **Steps**. Self Shadow and Height AO can each be set to 0 to skip their work.

## Compatibility

- Works with SPT-VR and alongside my other graphics mods. With Depth Offset on, fog, ambient occlusion and reflections follow the displaced ground.
- Only the game's MicroSplat ground and road materials are touched. Everything else renders as normal.

## Building from source

```sh
dotnet build POMSix.csproj -c Release
```

The project references the game's DLLs from a local `libs/` folder that isn't in the repository. Copy
these into it from your SPT install's `EscapeFromTarkov_Data/Managed` and `BepInEx/core` folders:

- `Assembly-CSharp.dll`
- `JBooth.MicroSplat.Core.dll`
- `0Harmony.dll`
- `BepInEx.dll`
- `UnityEngine.dll`
- `UnityEngine.CoreModule.dll`
- `UnityEngine.AssetBundleModule.dll`
- `UnityEngine.ImageConversionModule.dll`
- `UnityEngine.TerrainModule.dll`

The terrain and road shaders ship compiled in the `pom` bundle in each release. Their source isn't part of
this repository.

## Credits

- MicroSplat by Jason Booth, the terrain system Tarkov uses. POMSix builds on its Tessellation & Parallax module.

## License

The code in this repository is licensed under the GNU General Public License v3.0. See `LICENSE.txt`.
