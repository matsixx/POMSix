using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace POMSix
{
    // Top-down road coverage mask: every road/path decal mesh drawn once from above into a world-aligned
    // R8 texture at raid load. The terrain shaders read it (POMSixRoadFlat) and fade the carve - tessellation
    // displacement and POM depth - to zero under and beside roads. Roads sit on compacted, level ground;
    // carving rock pits there put a depth cliff along every crumbled asphalt edge (asphalt at the road plane,
    // the pit floor through each hole), which screen-space AO shaded as a wall and lighting as pits.
    public static class RoadMask
    {
        private const float TexelMeters = 0.25f;
        private const int MaxRes = 4096;
        private const float Margin = 4f;
        private const int RenderersPerFrame = 3000;

        private static RenderTexture _rt;
        private static readonly Dictionary<Material, Material> _maskMats = new Dictionary<Material, Material>();
        private static readonly List<MeshRenderer> _roads = new List<MeshRenderer>();
        private static bool _running;

        public static IEnumerator BuildRoutine()
        {
            if (_running) yield break;
            _running = true;
            if (!PomConfig.RoadMask.Value || PomApplier.RoadMaskShader == null) { Clear(); _running = false; yield break; }

            Shader ours = PomApplier.RoadShader;
            _roads.Clear();
            MeshRenderer[] all = Resources.FindObjectsOfTypeAll<MeshRenderer>();
            for (int i = 0; i < all.Length; i++)
            {
                if (i % RenderersPerFrame == RenderersPerFrame - 1) yield return null;
                MeshRenderer r = all[i];
                if (r == null || !r.gameObject.scene.IsValid()) continue;
                Material m = r.sharedMaterial;
                if (m == null || m.shader == null) continue;
                if (m.shader.name != RoadPom.VanillaShaderName && m.shader != ours) continue;
                _roads.Add(r);
            }
            Build();
            _running = false;
        }

        private static void Build()
        {
            if (_roads.Count == 0) { Clear(); return; }

            Bounds b = _roads[0].bounds;
            for (int i = 1; i < _roads.Count; i++) b.Encapsulate(_roads[i].bounds);
            float minX = b.min.x - Margin, maxX = b.max.x + Margin;
            float minZ = b.min.z - Margin, maxZ = b.max.z + Margin;
            float sizeX = maxX - minX, sizeZ = maxZ - minZ;
            int w = Mathf.Clamp(Mathf.CeilToInt(sizeX / TexelMeters), 64, MaxRes);
            int h = Mathf.Clamp(Mathf.CeilToInt(sizeZ / TexelMeters), 64, MaxRes);

            if (_rt == null || _rt.width != w || _rt.height != h)
            {
                Release();
                _rt = new RenderTexture(w, h, 0, RenderTextureFormat.R8)
                {
                    name = "POMSix road mask",
                    useMipMap = true,          // the shader reads a coarse mip: a ~1 m ramp at the edges
                    autoGenerateMips = true,
                    filterMode = FilterMode.Trilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                _rt.Create();
            }

            // Orthographic top-down view: camera x = world x, camera y = world z, looking straight down.
            float top = b.max.y + 10f, depth = (b.max.y - b.min.y) + 20f;
            Vector3 pos = new Vector3((minX + maxX) * 0.5f, top, (minZ + maxZ) * 0.5f);
            Quaternion rot = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(pos, rot, Vector3.one).inverse;
            Matrix4x4 proj = Matrix4x4.Ortho(-sizeX * 0.5f, sizeX * 0.5f, -sizeZ * 0.5f, sizeZ * 0.5f, 0.1f, depth);

            CommandBuffer cb = new CommandBuffer { name = "POMSix road mask" };
            cb.SetRenderTarget(_rt);
            cb.ClearRenderTarget(false, true, Color.clear);
            cb.SetViewProjectionMatrices(view, GL.GetGPUProjectionMatrix(proj, true));
            int drawn = 0;
            for (int i = 0; i < _roads.Count; i++)
            {
                MeshRenderer r = _roads[i];
                if (r == null) continue;
                Material mm = MaskMaterial(r.sharedMaterial);
                if (mm == null) continue;
                cb.DrawRenderer(r, mm, 0, 0);
                drawn++;
            }
            cb.GenerateMips(_rt);   // the shader reads mip 2; an ungenerated mip is zero = "no road anywhere"
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();

            // Verify by reading the mask back: coverage, and WHICH orientation puts the roads where the
            // meshes are (the top-down render's axis/flip conventions are checked, not assumed).
            Vector4 st = new Vector4(1f / sizeX, 1f / sizeZ, -minX / sizeX, -minZ / sizeZ);
            if (!Verify(w, h, minX, minZ, sizeX, sizeZ, ref st))
            {
                Clear();
                RoadPom.StampAll();   // no usable mask: the depth stamps keep the pits out of the lighting
                return;
            }
            Shader.SetGlobalTexture("_POMSixRoadMask", _rt);
            Shader.SetGlobalVector("_POMSixRoadMaskST", st);
            Shader.SetGlobalFloat("_POMSixRoadMaskOn", 1f);
            Plugin.MyLog.LogInfo("[POMSix] Road mask: " + drawn + " road meshes over " + sizeX.ToString("0") + "x"
                + sizeZ.ToString("0") + " m -> " + w + "x" + h + " (carve flattened under/beside roads)");
        }

        // Reads the mask back (one-time, at load) and scores the four axis-flip candidates by the mask
        // value under each road renderer's centre; adopts the winner. Returns false if the mask is empty.
        private static bool Verify(int w, int h, float minX, float minZ, float sizeX, float sizeZ, ref Vector4 st)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _rt;
            Texture2D tex = new Texture2D(w, h, TextureFormat.R8, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false);
            RenderTexture.active = prev;
            byte[] px = tex.GetRawTextureData();
            Object.Destroy(tex);

            long covered = 0;
            for (int i = 0; i < px.Length; i++) if (px[i] > 64) covered++;
            float coverage = px.Length > 0 ? (float)covered / px.Length : 0f;
            if (covered == 0)
            {
                Plugin.MyLog.LogWarning("[POMSix] Road mask came back EMPTY (nothing drawn) - using depth stamps instead.");
                return false;
            }

            float[] score = new float[4];
            int n = 0;
            int step = Mathf.Max(1, _roads.Count / 96);
            for (int i = 0; i < _roads.Count; i += step)
            {
                MeshRenderer r = _roads[i];
                if (r == null) continue;
                Vector3 c = r.bounds.center;
                float u = (c.x - minX) / sizeX, v = (c.z - minZ) / sizeZ;
                for (int k = 0; k < 4; k++)
                {
                    float uu = (k & 1) != 0 ? 1f - u : u, vv = (k & 2) != 0 ? 1f - v : v;
                    int x = Mathf.Clamp((int)(uu * w), 0, w - 1), y = Mathf.Clamp((int)(vv * h), 0, h - 1);
                    score[k] += px[y * w + x] / 255f;
                }
                n++;
            }
            int best = 0;
            for (int k = 1; k < 4; k++) if (score[k] > score[best]) best = k;
            if ((best & 1) != 0) { st.x = -st.x; st.z = 1f - st.z; }   // u -> 1 - u
            if ((best & 2) != 0) { st.y = -st.y; st.w = 1f - st.w; }   // v -> 1 - v
            Plugin.MyLog.LogInfo("[POMSix] Road mask check: coverage " + (coverage * 100f).ToString("0.0") + "%, road-centre hit "
                + (n > 0 ? (score[best] / n * 100f).ToString("0") : "?") + "% with orientation " + best
                + " (flipU=" + ((best & 1) != 0) + " flipV=" + ((best & 2) != 0) + "; others: "
                + string.Join(" ", System.Array.ConvertAll(score, s => n > 0 ? (s / n * 100f).ToString("0") : "?")) + ")");
            if (n > 0 && score[best] / n < 0.3f)
                Plugin.MyLog.LogWarning("[POMSix] Road mask: roads are NOT where the mask says in any orientation - check the log above.");
            return true;
        }

        // The mask shader samples the road's own alpha mask, so each road material gets a copy of the
        // mask shader carrying that material's _Heights / _Heights_ST / _AlphaStrength.
        private static Material MaskMaterial(Material road)
        {
            if (road == null) return null;
            if (_maskMats.TryGetValue(road, out Material m) && m != null) return m;
            m = new Material(PomApplier.RoadMaskShader) { name = road.name + " (POMSix mask)" };
            m.CopyPropertiesFromMaterial(road);
            _maskMats[road] = m;
            return m;
        }

        public static void Clear()
        {
            Shader.SetGlobalFloat("_POMSixRoadMaskOn", 0f);
            Release();
        }

        private static void Release()
        {
            if (_rt == null) return;
            _rt.Release();
            Object.Destroy(_rt);
            _rt = null;
        }
    }
}
