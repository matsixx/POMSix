using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace POMSix
{
    // Swaps every road/path/sidewalk material ('Custom/Vert Paint SoftCutout Decal' — the surface
    // shader recon identified 2026-07-19) to our POMSix/Road transcription. Same property names, so
    // textures/params rebind on the swap; POM marches heights generated from the road normal maps
    // (RoadHeights).
    // The renderer walk is CHUNKED over frames — Tarkov scenes have enormous renderer counts and
    // the original single-frame scan was a visible raid-start lag spike.
    public static class RoadPom
    {
        internal const string VanillaShaderName = "Custom/Vert Paint SoftCutout Decal";
        private const int RenderersPerFrame = 3000;
        private static readonly Dictionary<Material, Shader> _orig = new Dictionary<Material, Shader>();
        // Vanilla reads its ambient SH from a PER-FRAME global buffer (seen in the disassembly);
        // Unity fills our unity_SHAr per-renderer from light probes — slightly brighter ambient.
        // Probes OFF makes Unity feed the scene ambient probe = the frame-global source.
        private static readonly Dictionary<MeshRenderer, UnityEngine.Rendering.LightProbeUsage> _origProbes
            = new Dictionary<MeshRenderer, UnityEngine.Rendering.LightProbeUsage>();
        // Depth stamps: vanilla roads write no depth, so deferred lighting shaded them at the ground UNDER
        // them — with tessellation/PDO those are pits that catch the displaced rocks' shadows. Each road
        // renderer gets a second material (stamp copy of its road material) = the mesh drawn again,
        // depth only. Queued after all road + puddle decals so no road ever z-rejects another's color.
        private const int StampQueue = 2449; // last deferred slot before AlphaTest
        private static readonly Dictionary<Material, Material> _stamps = new Dictionary<Material, Material>();
        private static readonly List<MeshRenderer> _stamped = new List<MeshRenderer>();
        private static readonly HashSet<MeshRenderer> _roads = new HashSet<MeshRenderer>();   // every swapped road renderer
        private static bool _running;

        // Every road material carrying our shader (RoadHeights assigns their relief slices).
        public static ICollection<Material> Materials => _orig.Keys;

        private static Material Stamp(Material road)
        {
            if (_stamps.TryGetValue(road, out Material s) && s != null) return s;
            s = new Material(road) { name = road.name + " (POMSix depth)" };
            s.SetFloat("_POMSixStamp", 1f);
            s.SetFloat("_POMSixZWrite", 1f);
            s.SetFloat("_POMSixColorMask", 0f);
            s.EnableKeyword("POMSIX_STAMP");   // the depth-exporting variant (edge sink); the colour pass keeps rasterizer depth + Offset
            s.renderQueue = Mathf.Max(StampQueue, road.renderQueue + 1);
            _stamps[road] = s;
            return s;
        }

        public static IEnumerator ApplyRoutine()
        {
            if (_running) yield break;
            _running = true;
            if (!PomConfig.Roads.Value) { RestoreAll(); _running = false; yield break; }
            Shader road = PomApplier.RoadShader;
            if (road == null) { _running = false; yield break; }
            Shader.SetGlobalFloat("_POMSixRoadEnabled", 1f);
            // The depth stamp existed for ONE reason: the tessellation/PDO carve dug rock pits under the
            // ZWrite-Off road decals, so lighting (and AO) saw pits through the asphalt. The road mask now
            // flattens the carve under and beside roads at the source, so the stamp is obsolete there - and
            // harmful: it put the decal's floating plane (a few cm above the terrain) into the depth buffer,
            // a real step that AO drew as a contact line along every crumbled edge (vanilla has no road depth
            // at all). Stamps only when the mask can't run.
            bool stamp = !(PomConfig.RoadMask.Value && PomApplier.RoadMaskShader != null);

            MeshRenderer[] all = Resources.FindObjectsOfTypeAll<MeshRenderer>();
            int swapped = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (i % RenderersPerFrame == RenderersPerFrame - 1) yield return null;
                MeshRenderer r = all[i];
                if (r == null || !r.gameObject.scene.IsValid()) continue;
                // sharedMaterial (not sharedMaterials — that allocates an array per renderer); road
                // meshes are single-material per the recon.
                Material m = r.sharedMaterial;
                if (m == null || m.shader == null) continue;
                bool isVanilla = m.shader.name == VanillaShaderName;
                if (!isVanilla && m.shader != road) continue;
                _roads.Add(r);
                if (isVanilla)
                {
                    int queue = m.renderQueue;
                    _orig[m] = m.shader;
                    m.shader = road;
                    m.renderQueue = queue;
                    swapped++;
                }
                if (!_origProbes.ContainsKey(r))
                {
                    _origProbes[r] = r.lightProbeUsage;
                    r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                    // Extra materials redraw the LAST submesh — only safe on single-submesh roads.
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (stamp && m.renderQueue < 2500 && (r.isPartOfStaticBatch
                        || (mf != null && mf.sharedMesh != null && mf.sharedMesh.subMeshCount == 1)))
                    {
                        Material[] mats = r.sharedMaterials;
                        if (mats.Length == 1) { r.sharedMaterials = new[] { m, Stamp(m) }; _stamped.Add(r); }
                    }
                }
            }
            if (swapped > 0)
                Plugin.MyLog.LogInfo("[POMSix] Road shader swapped onto " + swapped + " materials ("
                    + (stamp ? _stamped.Count + " renderers depth-stamped)." : "no depth stamps: the road mask flattens the ground)."));
            RoadHeights.Request(_orig.Keys); // relief from the road normal maps (cached per texture)
            _running = false;
        }

        // Give every road its depth stamp (the pre-mask behaviour). Called when the road mask can't be used.
        public static void StampAll()
        {
            int added = 0;
            foreach (MeshRenderer r in _roads)
            {
                if (r == null || _stamped.Contains(r)) continue;
                Material m = r.sharedMaterial;
                if (m == null || m.shader != PomApplier.RoadShader || m.renderQueue >= 2500) continue;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (!(r.isPartOfStaticBatch || (mf != null && mf.sharedMesh != null && mf.sharedMesh.subMeshCount == 1))) continue;
                Material[] mats = r.sharedMaterials;
                if (mats.Length != 1) continue;
                r.sharedMaterials = new[] { m, Stamp(m) };
                _stamped.Add(r);
                added++;
            }
            if (added > 0) Plugin.MyLog.LogInfo("[POMSix] Depth stamps added to " + added + " road renderers (road mask unavailable).");
        }

        public static void RestoreAll()
        {
            _roads.Clear();
            Shader.SetGlobalFloat("_POMSixRoadEnabled", 0f);
            List<Material> dead = null;
            foreach (var kv in _orig)
            {
                if (kv.Key == null) { (dead ??= new List<Material>()).Add(kv.Key); continue; }
                int queue = kv.Key.renderQueue;
                kv.Key.shader = kv.Value;
                kv.Key.renderQueue = queue;
            }
            foreach (var kv in _origProbes)
                if (kv.Key != null) kv.Key.lightProbeUsage = kv.Value;
            _origProbes.Clear();
            foreach (MeshRenderer r in _stamped)
            {
                if (r == null) continue;
                Material[] mats = r.sharedMaterials;
                if (mats.Length == 2) r.sharedMaterials = new[] { mats[0] };
            }
            _stamped.Clear();
            foreach (Material s in _stamps.Values) if (s != null) Object.Destroy(s);
            _stamps.Clear();
            if (_orig.Count > 0) Plugin.MyLog.LogInfo("[POMSix] Road materials restored.");
            _orig.Clear();
        }
    }
}
