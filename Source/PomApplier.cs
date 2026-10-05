using System.Collections.Generic;
using System.IO;
using JBooth.MicroSplat;
using UnityEngine;

namespace POMSix
{
    // Swaps the game's generated MicroSplat terrain shader for our regenerated copy with POM enabled.
    // The two shaders share the same MicroSplat feature set, so every property name is identical and
    // the textures/params rebind automatically on the swap — only the POM params are ours to set.
    // Applied from the Sync() postfixes, so streamed-in slices and season/quality re-syncs (which
    // rebuild matInstance from the season's templateMaterial) all re-swap without extra bookkeeping.
    public static class PomApplier
    {
        private static Shader _pomShader;
        private static Shader _tessShader;   // POMSix/TerrainTess — optional bundle entry
        private static Shader _hybridShader; // POMSix/TerrainHybrid — tessellation + POM residual, optional
        public static Shader RoadShader; // POMSix/Road, loaded from the same bundle (used by RoadPom)
        public static Shader RoadMaskShader; // Hidden/POMSix/RoadMask (RoadMask.cs top-down coverage)
        // The road shader takes relief from generated heights (RoadHeights). A pom bundle built before
        // that marches BSG's mask at this fixed uv-space depth instead (the old Road Height).
        public static bool RoadHeightsSupported;
        private const float LegacyRoadHeight = 0.01f;
        private static bool _triedLoad;
        private static bool _pomBroken, _tessBroken, _hybridBroken, _warnedMissing;
        // BSG's height-blend contrast, read off the ORIGINAL terrain material (reads work; only writes
        // never reach the terrain draw). Tessellation's vertex blend uses it so the displaced geometry
        // follows the same layer transitions the fragment shades.
        private static float _terrainContrast = 0.5f;
        // Original shader per material instance id, so disabling POM restores vanilla live (A/B).
        private static readonly Dictionary<int, Shader> _origShaders = new Dictionary<int, Shader>();
        // THE key discovery (2026-07-19): BSG ships terrain with basemapDistance=0 — the ENTIRE
        // terrain renders through Unity's baked-basemap path and the material's pixel shader never
        // draws AT ALL (why every material/global/debug change was invisible). Stock MicroSplat sets
        // basemapDistance = POM fadeStart+fadeDistance itself in-editor; we do the same at runtime:
        // near ground renders the real material (POM lives), the distance stays on the cheap bake.
        private static readonly Dictionary<Terrain, float> _origBasemap = new Dictionary<Terrain, float>();

        private static bool IsOurs(Shader s) => s != null && (s == _pomShader || s == _tessShader || s == _hybridShader);

        private static string Canary(Shader s) =>
            s == _hybridShader ? "_POMSixHybridCanary" : s == _tessShader ? "_POMSixTessCanary" : "_POMSixCanary";

        // The terrain shader the current Displacement Mode wants, falling back to POM when the
        // tessellation/hybrid shader isn't in the bundle or failed its canary check.
        private static Shader TargetShader()
        {
            DisplacementMode mode = PomConfig.Mode.Value;
            if (mode != DisplacementMode.POM)
            {
                bool hybrid = mode == DisplacementMode.Hybrid;
                Shader want = hybrid ? _hybridShader : _tessShader;
                if (want != null && !(hybrid ? _hybridBroken : _tessBroken)) return want;
                if (!_warnedMissing)
                {
                    _warnedMissing = true;
                    Plugin.MyLog.LogWarning("[POMSix] Displacement Mode = " + mode + ", but the pom bundle has no "
                        + "usable " + mode + " shader — using POM. Generate the tessellation variant in Unity and "
                        + "rebuild the bundle (Hybrid is built from it automatically).");
                }
            }
            return _pomBroken ? null : _pomShader;
        }

        public static void Apply(MicroSplatObject o)
        {
            if (o == null) return;
            Material m = o.matInstance;
            if (m == null || m.shader == null) return;
            if (!PomConfig.Enabled.Value) { Restore(o); return; }
            // Game terrain shaders are "MicroSplat/..."; already-swapped materials carry one of ours.
            if (!IsOurs(m.shader) && !m.shader.name.StartsWith("MicroSplat/")) return;
            LoadShader();
            Shader target = TargetShader();
            if (target == null) return;

            if (m.shader != target)
            {
                int queue = m.renderQueue; // the swap resets the queue to the new shader's default
                Shader previous = m.shader;
                if (!IsOurs(previous))
                {
                    _origShaders[m.GetInstanceID()] = previous;
                    if (m.HasProperty("_Contrast")) _terrainContrast = m.GetFloat("_Contrast");
                }
                m.shader = target;
                m.renderQueue = queue;
                // Native sanity check on the canary property. A variant-stripped or miscompiled
                // bundle shader has no native property data in the player (learned 2026-07-19:
                // default bundle builds strip the INSTANCING_ON variants Tarkov's drawInstanced
                // terrain renders through → invisible terrain). Revert so a bad bundle fails safe.
                string kind = target == _hybridShader ? "Hybrid" : target == _tessShader ? "Tessellation" : "POM";
                if (!m.HasProperty(Canary(target)))
                {
                    m.shader = previous;
                    m.renderQueue = queue;
                    if (target == _hybridShader) _hybridBroken = true;
                    else if (target == _tessShader) _tessBroken = true;
                    else _pomBroken = true;
                    Plugin.MyLog.LogError("[POMSix] " + kind + " shader has no usable compiled data in-game (shader "
                        + "stripping at bundle build?). Reverted '" + o.name + "' — rebuild the pom bundle with the "
                        + "current BuildPomBundle script.");
                    if (target != _pomShader) Apply(o); // retry with the POM fallback (terminates: flags only flip once)
                    return;
                }
                Plugin.MyLog.LogInfo("[POMSix] " + kind + " shader swapped onto '" + o.name + "'");
            }
            if (o is MicroSplatTerrain mt && mt.terrain != null)
            {
                if (!_origBasemap.ContainsKey(mt.terrain))
                    _origBasemap[mt.terrain] = mt.terrain.basemapDistance;
                mt.terrain.basemapDistance = PomConfig.FadeStart.Value + PomConfig.FadeDistance.Value;
            }
            ApplyGlobals();
            HeightGen.Request(m); // real heights from this map's normal arrays (cached after first raid)
        }

        // The game's terrain draw consumes shader ASSIGNMENT but not live material property edits
        // (2026-07-19: swap visible, every material SetVector/SetFloat a no-op) — so ALL knobs ride
        // global shader constants, the same pattern FogSix/SSRSix/CloudSix use on game shaders.
        // _POMSixParams = (height, fadeStart, fadeDistance, maxSteps); no material anywhere carries
        // these names, so the globals always bind.
        public static void ApplyGlobals()
        {
            Shader.SetGlobalVector("_POMSixParams", new Vector4(PomConfig.Height.Value,
                PomConfig.FadeStart.Value, PomConfig.FadeDistance.Value, PomConfig.Steps.Value));
            Shader.SetGlobalFloat("_POMSixDebug", PomConfig.DebugView.Value);
            // Gradient scale = 2^mips: the POM height samples read that many mips blurrier.
            Shader.SetGlobalFloat("_POMSixSmooth", Mathf.Pow(2f, PomConfig.Smoothing.Value));
            // QDM: (res, topMipLevel, startLevel, enabled). Enabled only once the pyramid exists —
            // the traversal reads _POMSixHeightsMax directly, there is no luminance fallback for it.
            // Start 3 levels below the top (8x8 cells per tile at any resolution).
            Shader.SetGlobalVector("_POMSixQdmParams", new Vector4(HeightGen.Res, HeightGen.PyramidTopLevel,
                Mathf.Max(0, HeightGen.PyramidTopLevel - 3), PomConfig.Qdm.Value && HeightGen.PyramidReady ? 1f : 0f));
            Shader.SetGlobalFloat("_POMSixNormalMode", PomConfig.DerivedNormals.Value ? 1f : 0f);
            // (relief balance, blended march, material height blending, -) and pixel depth offset.
            Shader.SetGlobalVector("_POMSixQuality", new Vector4(PomConfig.ReliefBalance.Value,
                PomConfig.BlendedMarch.Value ? 1f : 0f, PomConfig.HeightBlending.Value, PomConfig.GrazingDepth.Value));
            Shader.SetGlobalVector("_POMSixDepthParams", new Vector4(PomConfig.DepthOffset.Value ? 1f : 0f, 0f, 0f, 0f));
            // Shared fade (material height blending in all variants) + tessellation:
            // _POMSixTessData1 = (-, displacement m, -, -) — factors and vertex mips now come from
            // _POMSixTessParams (per frame, UpdateCameraGlobals);
            // _POMSixTessData2 = (full-tess distance, zero-tess distance, blend contrast, up bias 0 = along
            // the terrain normal, so relief pushes out of slopes instead of straight down).
            float fadeStart = PomConfig.FadeStart.Value, fadeEnd = fadeStart + PomConfig.FadeDistance.Value;
            Shader.SetGlobalVector("_POMSixFade", new Vector4(fadeStart, PomConfig.FadeDistance.Value, 0f, 0f));
            Shader.SetGlobalVector("_POMSixTessData1", new Vector4(0f, PomConfig.TessDisplacement.Value, 0f, 0f));
            Shader.SetGlobalVector("_POMSixTessData2", new Vector4(fadeStart, fadeEnd, _terrainContrast, 0f));
            // Road depth-stamp edge sink = how far the ground beside a road can be carved below it (the
            // tessellation carve, or the PDO pit in POM mode) + the decal's own float above the terrain.
            float carve = PomConfig.Mode.Value != DisplacementMode.POM ? PomConfig.TessDisplacement.Value
                        : (PomConfig.DepthOffset.Value ? PomConfig.Height.Value : 0f);
            Shader.SetGlobalFloat("_POMSixRoadSink", carve + 0.02f);
            // Roads share the terrain's fades/steps. x is only read by bundles built before generated road
            // heights (a uv-space depth for the old mask march); the current shader takes _POMSixRoadRelief.
            Shader.SetGlobalVector("_POMSixRoadParams", new Vector4(LegacyRoadHeight,
                PomConfig.FadeStart.Value, PomConfig.FadeDistance.Value, PomConfig.Steps.Value));
            Shader.SetGlobalVector("_POMSixRoadRelief", new Vector4(PomConfig.RoadRelief.Value,
                PomConfig.RoadMaxDepth.Value, PomConfig.RoadGroundAlign.Value, 0f));
            // Terrain self-shadow + horizon AO strengths (0 = the shader skips the loops).
            Shader.SetGlobalVector("_POMSixShadowParams", new Vector4(PomConfig.ShadowStrength.Value,
                PomConfig.AoStrength.Value, 0f, 0f));
            // Fade knobs also bound the real-material region (cheap loop, prune dead terrains).
            if (PomConfig.Enabled.Value)
            {
                List<Terrain> dead = null;
                foreach (var kv in _origBasemap)
                {
                    if (kv.Key == null) { (dead ??= new List<Terrain>()).Add(kv.Key); continue; }
                    kv.Key.basemapDistance = PomConfig.FadeStart.Value + PomConfig.FadeDistance.Value;
                }
                if (dead != null) foreach (Terrain t in dead) _origBasemap.Remove(t);
            }
        }

        // Tessellation density rides the main camera's projection: target vertex spacing grows linearly
        // with distance so triangles stay Triangle Size px on screen (pixelHeight = the eye texture in VR).
        // _POMSixTessParams = (spacing per meter of distance, min spacing m, patch culling, mip bias).
        private const float MinSpacing = 0.02f; // below ~2cm the triangle count explodes under your feet
        public static void UpdateCameraGlobals(Camera cam)
        {
            float height = cam != null ? cam.pixelHeight : Screen.height;
            float fov = cam != null ? cam.fieldOfView : 60f;
            float projScale = Mathf.Max(1f, height) / (2f * Mathf.Tan(0.5f * fov * Mathf.Deg2Rad));
            Shader.SetGlobalVector("_POMSixTessParams", new Vector4(PomConfig.TriangleSize.Value / projScale,
                MinSpacing, PomConfig.PatchCulling.Value ? 1f : 0f, 0.5f * PomConfig.Smoothing.Value));
        }

        private static void Restore(MicroSplatObject o)
        {
            Material m = o.matInstance;
            if (m == null || _pomShader == null) return;
            if (o is MicroSplatTerrain mt && mt.terrain != null
                && _origBasemap.TryGetValue(mt.terrain, out float dist))
                mt.terrain.basemapDistance = dist;
            if (!IsOurs(m.shader)) return;
            if (_origShaders.TryGetValue(m.GetInstanceID(), out Shader orig) && orig != null)
            {
                int queue = m.renderQueue;
                m.shader = orig;
                m.renderQueue = queue;
                Plugin.MyLog.LogInfo("[POMSix] Restored game shader on '" + m.name + "'");
            }
        }

        // Enabled toggled (or a late scan pass): hit every live MicroSplat object. Only runs on
        // config edits and raid-load scans, never per-frame.
        public static void ReapplyAll()
        {
            foreach (MicroSplatObject o in Resources.FindObjectsOfTypeAll<MicroSplatObject>())
                Apply(o);
        }

        private static void LoadShader()
        {
            if (_pomShader != null || _triedLoad) return;
            _triedLoad = true;
            string bundlePath = Path.Combine(BepInEx.Paths.PluginPath, "POMSix", "Assets", "pom");
            if (!File.Exists(bundlePath))
            {
                Plugin.MyLog.LogInfo("[POMSix] No POM shader bundle at " + bundlePath + " — recon-only mode.");
                return;
            }
            AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                Plugin.MyLog.LogError("[POMSix] Failed to load AssetBundle at " + bundlePath);
                return;
            }
            Shader[] shaders = bundle.LoadAllAssets<Shader>();
            foreach (Shader s in shaders) // pick each shader by its canary property
            {
                if (_pomShader == null && s.FindPropertyIndex("_POMSixCanary") >= 0) _pomShader = s;
                if (_tessShader == null && s.FindPropertyIndex("_POMSixTessCanary") >= 0) _tessShader = s;
                if (_hybridShader == null && s.FindPropertyIndex("_POMSixHybridCanary") >= 0) _hybridShader = s;
                if (RoadShader == null && s.FindPropertyIndex("_POMSixRoadCanary") >= 0) RoadShader = s;
                if (RoadMaskShader == null && s.FindPropertyIndex("_POMSixRoadMaskCanary") >= 0) RoadMaskShader = s;
            }
            Plugin.MyLog.LogInfo("[POMSix] Bundle shaders: POM=" + Describe(_pomShader) + ", Tessellation="
                + Describe(_tessShader) + ", Hybrid=" + Describe(_hybridShader) + ", Road=" + Describe(RoadShader)
                + ", RoadMask=" + Describe(RoadMaskShader));
            if (_pomShader == null) Plugin.MyLog.LogError("[POMSix] No POM terrain shader in the pom bundle.");
            RoadHeightsSupported = RoadShader != null && RoadShader.FindPropertyIndex("_POMSixRoadLayers") >= 0;
            if (RoadShader != null && !RoadHeightsSupported)
                Plugin.MyLog.LogWarning("[POMSix] The pom bundle's road shader predates generated road heights: "
                    + "roads keep the old mask relief (Road Relief has no effect) until the bundle is rebuilt.");
            bundle.Unload(false); // keep the shader in memory
        }

        private static string Describe(Shader s) => s == null ? "absent" : s.name + (s.isSupported ? "" : " (NOT SUPPORTED)");
    }
}
