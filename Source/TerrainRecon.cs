using System.Collections.Generic;
using System.Text;
using JBooth.MicroSplat;
using UnityEngine;
using UnityEngine.Rendering;

namespace POMSix
{
    // v0 recon: one-shot dump of every MicroSplat terrain's shader interface, logged at raid load.
    // The keywordSO list is the generation-time feature set of BSG's MicroSplat shader — it decides
    // which modules the replacement POM shader must be generated with, and whether POM/tessellation
    // is ALREADY compiled in (in which case the mod may reduce to setting _POMParams). The texture
    // dump confirms per-layer height lives in the _Diffuse array's alpha (what POM displaces by).
    public static class TerrainRecon
    {
        private static readonly HashSet<int> _dumped = new HashSet<int>();

        public static void Dump(string sceneName)
        {
            MicroSplatObject[] objs = Resources.FindObjectsOfTypeAll<MicroSplatObject>();
            foreach (MicroSplatObject o in objs)
            {
                if (o == null || !_dumped.Add(o.GetInstanceID())) continue;
                try { DumpOne(o, sceneName); }
                catch (System.Exception ex) { Plugin.MyLog.LogError("[Recon] " + o.name + ": " + ex); }
            }
        }

        private static void DumpOne(MicroSplatObject o, string sceneName)
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("[Recon] ").Append(o.GetType().Name).Append(" '").Append(o.name)
              .Append("' scene=").Append(sceneName)
              .Append(" season=").Append(MicroSplatObject.currentSeason)
              .Append(" quality=").Append(MicroSplatObject.currentQuality).AppendLine();

            // The generation-time feature list — the single most important output.
            if (o.keywordSO != null)
                sb.Append("  keywords: ").Append(string.Join(" ", o.keywordSO.keywords)).AppendLine();
            else
                sb.AppendLine("  keywords: <null keywordSO>");

            sb.Append("  materialSets: high=").Append(o.templateMaterialHigh?.HasMaterials() ?? false)
              .Append(" normal=").Append(o.templateMaterialNormal?.HasMaterials() ?? false)
              .Append(" low=").Append(o.templateMaterialLow?.HasMaterials() ?? false)
              .Append(" propData=").Append(o.propData != null)
              .Append(" perPixelNormal=").Append(o.perPixelNormal != null)
              .Append(" blendMat=").Append(o.blendMat != null).AppendLine();

            if (o is MicroSplatTerrain t && t.terrain != null && t.terrain.terrainData != null)
            {
                TerrainData td = t.terrain.terrainData;
                sb.Append("  terrain: size=").Append(td.size)
                  .Append(" heightmapRes=").Append(td.heightmapResolution)
                  .Append(" alphamapRes=").Append(td.alphamapResolution)
                  .Append(" splatLayers=").Append(td.alphamapLayers)
                  .Append(" alphamapTextures=").Append(td.alphamapTextures?.Length ?? 0)
                  .Append(" drawInstanced=").Append(t.terrain.drawInstanced).AppendLine();
                // Who actually renders the ground: past basemapDistance Unity draws the BAKED basemap
                // (no per-pixel material work shows there); Tarkov's TerrainLod can swap the whole
                // terrain for a baked LOD mesh; a materialTemplate mismatch means our swapped material
                // isn't the one the terrain draws at all.
                TerrainLod lod = t.GetComponent<TerrainLod>();
                sb.Append("  renderPath: basemapDistance=").Append(t.terrain.basemapDistance)
                  .Append(" drawHeightmap=").Append(t.terrain.drawHeightmap)
                  .Append(" terrainEnabled=").Append(t.terrain.enabled)
                  .Append(" materialTemplate==matInstance=").Append(t.terrain.materialTemplate == o.matInstance)
                  .Append(" terrainLod=").Append(lod == null ? "none" : lod.TerrainIsVisible ? "terrainVisible" : "LOD-MESH-ACTIVE")
                  .AppendLine();
            }
            else if (o is MicroSplatMeshTerrain mt)
            {
                sb.Append("  meshTerrain: renderers=").Append(mt.meshTerrains?.Length ?? 0)
                  .Append(" controlTextures=").Append(mt.controlTextures?.Length ?? 0).AppendLine();
            }

            Material m = o.matInstance != null ? o.matInstance : o.templateMaterial;
            if (m == null && o.templateMaterialHigh != null)
                m = o.templateMaterialHigh.GetSeasonMaterial(MicroSplatObject.currentSeason);
            if (m == null) { sb.AppendLine("  material: <none resolved>"); Log(sb); return; }
            ProbeDiffuseAlpha(m);

            Shader sh = m.shader;
            sb.Append("  material '").Append(m.name).Append("' shader '").Append(sh != null ? sh.name : "<null>")
              .Append("' queue=").Append(m.renderQueue).Append(" passes=").Append(m.passCount).AppendLine();
            sb.Append("  matKeywords: ").Append(string.Join(" ", m.shaderKeywords)).AppendLine();

            // The zero-cost shortcut check: if the shipped shader already declares the parallax/tess
            // module properties, POM is compiled in and may just need enabling.
            sb.Append("  POM check: _POMSixCanary=").Append(m.HasProperty("_POMSixCanary"))
              .Append(" _POMParams=").Append(m.HasProperty("_POMParams"))
              .Append(" _TessData2=").Append(m.HasProperty("_TessData2")).AppendLine();
            sb.Append("  POMSix globals: params=").Append(Shader.GetGlobalVector("_POMSixParams"))
              .Append(" debug=").Append(Shader.GetGlobalFloat("_POMSixDebug")).AppendLine();

            AppendShaderProperties(sb, m, sh);
            Log(sb);
        }

        private static void AppendShaderProperties(StringBuilder sb, Material m, Shader sh)
        {
            if (sh == null) return;
            int n = sh.GetPropertyCount();
            sb.Append("  shader properties (").Append(n).AppendLine("):");
            for (int i = 0; i < n; i++)
            {
                string pn = sh.GetPropertyName(i);
                ShaderPropertyType pt = sh.GetPropertyType(i);
                sb.Append("    ").Append(pn).Append(" [").Append(pt).Append("] ");
                // GetPropertyName reads asset metadata; HasProperty is native-backed. A property in
                // the table but not the native side = broken/stripped compiled shader data — say so
                // instead of letting the strict getters below spam Unity errors.
                if (!m.HasProperty(pn)) { sb.AppendLine("<in table but NOT in native material>"); continue; }
                switch (pt)
                {
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        sb.Append("= ").Append(m.GetFloat(pn)); break;
                    case ShaderPropertyType.Vector:
                        sb.Append("= ").Append(m.GetVector(pn)); break;
                    case ShaderPropertyType.Color:
                        sb.Append("= ").Append(m.GetColor(pn)); break;
                    case ShaderPropertyType.Texture:
                        AppendTex(sb, m.GetTexture(pn)); break;
                }
                sb.AppendLine();
            }
        }

        // Road/path recon: roads are mesh strips draped over the terrain (RoadsTerrainAligner), with
        // their own materials — find them by name and dump ONE representative material per unique
        // shader, so a single raid tells us what a road-POM shader must reproduce.
        private static readonly HashSet<string> _roadShadersSeen = new HashSet<string>();
        private static readonly string[] _roadWords =
            { "road", "asphalt", "path", "sidewalk", "pavement", "trail", "kerb", "curb" };

        public static void DumpRoads(string sceneName)
        {
            var counts = new Dictionary<string, int>();
            foreach (MeshRenderer r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                if (r == null || !r.gameObject.scene.IsValid()) continue;
                Material m = r.sharedMaterial;
                if (m == null || m.shader == null) continue;
                string names = (r.name + "|" + m.name).ToLowerInvariant();
                bool hit = false;
                foreach (string w in _roadWords)
                    if (names.Contains(w)) { hit = true; break; }
                if (!hit) continue;

                string shaderName = m.shader.name;
                counts.TryGetValue(shaderName, out int c);
                counts[shaderName] = c + 1;

                if (!_roadShadersSeen.Add(shaderName)) continue;
                StringBuilder sb = new StringBuilder(2048);
                sb.Append("[RoadRecon] '").Append(r.name).Append("' scene=").Append(sceneName)
                  .Append(" material '").Append(m.name).Append("' shader '").Append(shaderName)
                  .Append("' queue=").Append(m.renderQueue).Append(" passes=").Append(m.passCount).AppendLine();
                sb.Append("  matKeywords: ").Append(string.Join(" ", m.shaderKeywords)).AppendLine();
                AppendShaderProperties(sb, m, m.shader);
                Log(sb);
            }
            if (counts.Count > 0 && _countsScenes.Add(sceneName))
            {
                StringBuilder sb = new StringBuilder(512);
                sb.AppendLine("[RoadRecon] renderer counts by shader:");
                foreach (var kv in counts)
                    sb.Append("    ").Append(kv.Key).Append(" x").Append(kv.Value).AppendLine();
                Log(sb);
            }
            DumpRoadTextures();
        }

        private static readonly HashSet<string> _countsScenes = new HashSet<string>();

        // The road surface shader ('Custom/Vert Paint SoftCutout Decal', identified 2026-07-19) has a
        // _Heights mask BSG authored for its ALPHA_HEIGHT layer blending. PNG-dump it (+ one layer's
        // diffuse/normal) so we can see whether it's real per-layer height data POM can march
        // directly — the best possible height source, no generation needed.
        private const string RoadShaderName = "Custom/Vert Paint SoftCutout Decal";
        private static readonly HashSet<int> _roadTexDumped = new HashSet<int>();

        private static readonly HashSet<int> _roadMatsLogged = new HashSet<int>();

        private static void DumpRoadTextures()
        {
            StringBuilder sb = null;
            foreach (MeshRenderer r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                if (r == null || !r.gameObject.scene.IsValid()) continue;
                Material m = r.sharedMaterial;
                if (m == null || m.shader == null) continue;
                bool vanilla = m.shader.name == RoadShaderName;
                bool swapped = PomApplier.RoadShader != null && m.shader == PomApplier.RoadShader;
                if (!vanilla && !swapped) continue;
                SaveTexPng(m, "_Heights");
                SaveTexPng(m, "_MainTex0");
                SaveTexPng(m, "_BumpMap0");
                // Which texture sits in which layer, per material — needed to decode the vertex-paint
                // convention (v1 rendered the wrong layer on road bodies).
                if (_roadMatsLogged.Add(m.GetInstanceID()))
                {
                    sb ??= new StringBuilder(1024).AppendLine("[RoadRecon] material layers:");
                    sb.Append("    '").Append(m.name).Append("': L0=").Append(TexName(m, "_MainTex0"))
                      .Append(" L1=").Append(TexName(m, "_MainTex1"))
                      .Append(" L2=").Append(TexName(m, "_MainTex2"))
                      .Append(" heights=").Append(TexName(m, "_Heights"))
                      // Tilings: road POM depth is in layer 0's uv units, so these set each layer's relief.
                      .Append(" tiling L0=").Append(Tiling(m, "_MainTex0")).Append(" L1=").Append(Tiling(m, "_MainTex1"))
                      .Append(" L2=").Append(Tiling(m, "_MainTex2")).Append(" heightsA=").Append(Tiling(m, "_Heights"))
                      .Append(" blend=").Append(m.HasProperty("_BlendStrength") ? m.GetFloat("_BlendStrength").ToString("F2") : "?")
                      .AppendLine();
                }
            }
            if (sb != null) Log(sb);
        }

        private static string Tiling(Material m, string prop)
        {
            if (!m.HasProperty(prop)) return "?";
            Vector2 s = m.GetTextureScale(prop);
            return "(" + s.x.ToString("0.##") + "," + s.y.ToString("0.##") + ")";
        }

        // Per-channel range of a road height mask (R/G/B = the three layers' heights): the 5th..95th
        // percentile span is the relief each layer actually gets, and "coarse" is how much of its
        // variation sits in features wider than 16 px (broad mottle that POM turns into mounds).
        private static string HeightStats(byte[] px, int w, int h)
        {
            StringBuilder sb = new StringBuilder(160);
            int bw = w / 16, bh = h / 16;
            for (int c = 0; c < 3; c++)
            {
                int[] hist = new int[256];
                double sum = 0, sum2 = 0;
                double[] block = new double[Mathf.Max(bw * bh, 1)];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int v = px[(y * w + x) * 4 + c];
                        hist[v]++; sum += v; sum2 += (double)v * v;
                        if (x / 16 < bw && y / 16 < bh) block[(y / 16) * bw + x / 16] += v / 256.0;
                    }
                int n = w * h, lo = 0, hi = 255, acc = 0;
                for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= n * 0.05) { lo = i; break; } }
                acc = 0;
                for (int i = 255; i >= 0; i--) { acc += hist[i]; if (acc >= n * 0.05) { hi = i; break; } }
                double mean = sum / n, var = sum2 / n - mean * mean, bsum = 0, bsum2 = 0;
                foreach (double b in block) { bsum += b; bsum2 += b * b; }
                double bmean = bsum / block.Length, bvar = bsum2 / block.Length - bmean * bmean;
                sb.Append(" ").Append("RGB"[c]).Append("=").Append((lo / 255f).ToString("F2")).Append("..")
                  .Append((hi / 255f).ToString("F2")).Append(" coarse ")
                  .Append(var > 1e-6 ? (100.0 * bvar / var).ToString("F0") : "0").Append("%");
            }
            return sb.ToString();
        }

        private static string TexName(Material m, string prop)
        {
            if (!m.HasProperty(prop)) return "<n/a>";
            Texture t = m.GetTexture(prop);
            return t == null ? "<null>" : t.name;
        }

        private static void SaveTexPng(Material m, string prop)
        {
            if (!m.HasProperty(prop)) return;
            Texture tex = m.GetTexture(prop);
            if (tex == null || !_roadTexDumped.Add(tex.GetInstanceID())) return;
            string name = tex.name;
            int w = Mathf.Min(tex.width, 1024), h = Mathf.Min(tex.height, 1024);
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            Graphics.Blit(tex, rt);
            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req =>
            {
                RenderTexture.ReleaseTemporary(rt);
                if (req.hasError) { Plugin.MyLog.LogError("[RoadRecon] readback failed: " + name); return; }
                byte[] px = req.GetData<byte>().ToArray();
                if (prop == "_Heights")
                    Plugin.MyLog.LogInfo("[RoadRecon] height mask '" + name + "' range per layer:" + HeightStats(px, w, h));
                var t2 = new Texture2D(w, h, TextureFormat.RGBA32, false);
                t2.SetPixelData(px, 0);
                t2.Apply(false, false);
                string dir = System.IO.Path.Combine(BepInEx.Paths.PluginPath, "POMSix", "HeightCache", "debug", "roads");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, prop + "_" + name + ".png"), t2.EncodeToPNG());
                Object.Destroy(t2);
                Plugin.MyLog.LogInfo("[RoadRecon] dumped " + prop + " '" + name + "' to HeightCache/debug/roads");
            });
        }

        private static void AppendTex(StringBuilder sb, Texture tex)
        {
            if (tex == null) { sb.Append("= <null>"); return; }
            sb.Append("= ").Append(tex.name).Append(" (").Append(tex.GetType().Name)
              .Append(" ").Append(tex.width).Append("x").Append(tex.height);
            if (tex is Texture2DArray arr) sb.Append("x").Append(arr.depth);
            sb.Append(" ").Append(tex.graphicsFormat).Append(" mips=").Append(tex.mipmapCount).Append(")");
        }

        private static void Log(StringBuilder sb) => Plugin.MyLog.LogInfo(sb.ToString());

        // POM marches the _Diffuse array's ALPHA as the heightfield. If BSG built their arrays without
        // height maps, that channel is near-constant and POM physically cannot show relief no matter
        // the params — this readback decodes a small mip of every layer and logs alpha statistics so
        // one raid settles it. stddev ~0 = flat (no height data); a real height map is ~40-70 stddev.
        private static readonly HashSet<int> _probedTex = new HashSet<int>();

        private static void ProbeDiffuseAlpha(Material m)
        {
            if (!m.HasProperty("_Diffuse")) return;
            if (!(m.GetTexture("_Diffuse") is Texture2DArray arr) || !_probedTex.Add(arr.GetInstanceID()))
                return;
            // BC3 can't be async-read directly (hit in-game 2026-07-19) — decode on the GPU instead:
            // blit each layer (depth-slice Blit overload) into a small uncompressed RT, read that back.
            const int size = 64;
            int depth = arr.depth;
            string texName = arr.name;
            string[] results = new string[depth];
            int pending = depth;
            for (int layer = 0; layer < depth; layer++)
            {
                int l = layer;
                RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(arr, rt, l, 0);
                AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req =>
                {
                    RenderTexture.ReleaseTemporary(rt);
                    if (req.hasError) results[l] = "layer " + l + ": <readback error>";
                    else
                    {
                        var data = req.GetData<byte>();
                        int n = size * size;
                        int min = 255, max = 0; double sum = 0, sumSq = 0;
                        for (int i = 0; i < n; i++)
                        {
                            byte a = data[i * 4 + 3];
                            if (a < min) min = a; if (a > max) max = a;
                            sum += a; sumSq += (double)a * a;
                        }
                        double mean = sum / n, std = System.Math.Sqrt(System.Math.Max(0, sumSq / n - mean * mean));
                        results[l] = "layer " + l + ": min=" + min + " max=" + max
                            + " mean=" + mean.ToString("0.0") + " std=" + std.ToString("0.0");
                    }
                    if (--pending == 0)
                    {
                        StringBuilder sb = new StringBuilder(1024);
                        sb.Append("[Recon] _Diffuse '").Append(texName).AppendLine("' alpha (=POM height) per layer:");
                        foreach (string r in results) sb.Append("    ").AppendLine(r);
                        Plugin.MyLog.LogInfo(sb.ToString());
                    }
                });
            }
        }
    }
}
