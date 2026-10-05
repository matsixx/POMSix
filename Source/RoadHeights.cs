using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace POMSix
{
    // Road relief heights, generated from each road layer's own normal map the way the terrain's are
    // (HeightGen: FFT Poisson integration, high-pass, normalised, relief range measured). BSG's _Heights
    // mask stays what it is in vanilla - the layer blend - but as relief it was uneven between layers and
    // spiky. One array slice per unique normal map; every road material gets its three slice indices and
    // each layer's measured relief as a fraction of its texture tile, which the shader turns into depth.
    // Cached per texture, so later raids and other maps that reuse a road texture load from disk.
    public static class RoadHeights
    {
        public const int Res = 512;       // heights are smooth by construction; the fine detail stays in the normal map
        private const int MaxSlices = 64; // ~0.7 MB each
        private const byte CacheVersion = 1;
        private static readonly string[] BumpProps = { "_BumpMap0", "_BumpMap1", "_BumpMap2" };

        private class Job
        {
            public int[] ids;
            public string[] names;
            public ushort[][] l0;       // normalised heights per slice (null = failed: flat)
            public float[] ranges;      // relief span per slice, in texels at Res
            public float[] levels;      // ground level per slice: the median height, 0..1
            public ushort[][][] chains; // mip chains, built off-thread
            public int remaining;
        }

        private static Job _job;                // the set being generated
        private static volatile Job _ready;     // worker -> main handoff
        private static Job _uploading;
        private static int _uploadIdx;
        private static Texture2DArray _uploadTex;
        private static Texture2DArray _tex;
        private static readonly Dictionary<int, int> _slice = new Dictionary<int, int>(); // normal map instance id -> slice
        private static float[] _tileRange = new float[0];                                 // per slice: relief / tile
        private static float[] _level = new float[0];                                     // per slice: ground level 0..1

        private static string CacheDir => Path.Combine(BepInEx.Paths.PluginPath, "POMSix", "HeightCache", "roads");

        // Called after every road scan with all road materials. Starts generation when the map's set of
        // road normal maps isn't bound yet; otherwise just (re)assigns the per-material properties.
        public static void Request(ICollection<Material> mats)
        {
            if (!PomApplier.RoadHeightsSupported) return;
            var texes = new List<Texture>();
            var ids = new List<int>();
            var seen = new HashSet<int>();
            foreach (Material m in mats)
            {
                if (m == null) continue;
                foreach (string p in BumpProps)
                {
                    if (!m.HasProperty(p)) continue;
                    Texture t = m.GetTexture(p);
                    if (!(t is Texture2D) || !seen.Add(t.GetInstanceID()) || texes.Count >= MaxSlices) continue;
                    texes.Add(t);
                    ids.Add(t.GetInstanceID());
                }
            }
            if (texes.Count == 0) return;

            bool bound = _tex != null;
            if (bound) foreach (int id in ids) if (!_slice.ContainsKey(id)) { bound = false; break; }
            if (bound) { Assign(mats); return; }
            if (_job != null && SameSet(_job.ids, ids)) return;

            int n = texes.Count;
            var job = new Job { ids = ids.ToArray(), names = new string[n], l0 = new ushort[n][], ranges = new float[n], remaining = n };
            _job = job;
            Directory.CreateDirectory(CacheDir);
            int fresh = 0;
            for (int i = 0; i < n; i++)
            {
                Texture t = texes[i];
                job.names[i] = t.name;
                string key = t.name + "_" + t.width + "x" + t.height + "_" + Res + "_hp" + HeightGen.HighPassCycles;
                foreach (char c in Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
                string path = Path.Combine(CacheDir, key + ".bin");
                int slot = i;
                if (File.Exists(path)) Task.Run(() => LoadCached(job, slot, path));
                else { fresh++; Readback(job, slot, t, path); }
            }
            if (fresh > 0)
                Plugin.MyLog.LogInfo("[POMSix] Generating road heights from " + fresh + " road normal maps ("
                    + (n - fresh) + " cached) - first time each texture is seen only.");
        }

        private static bool SameSet(int[] a, List<int> b)
        {
            if (a.Length != b.Count) return false;
            var set = new HashSet<int>(a);
            foreach (int id in b) if (!set.Contains(id)) return false;
            return true;
        }

        private static void Readback(Job job, int slot, Texture tex, string path)
        {
            // A minifying blit reads the matching mip, so a 2k/4k normal map arrives box-filtered.
            RenderTexture rt = RenderTexture.GetTemporary(Res, Res, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(tex, rt);
            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req =>
            {
                RenderTexture.ReleaseTemporary(rt);
                if (req.hasError)
                {
                    Plugin.MyLog.LogError("[POMSix] Road normal map readback failed: " + job.names[slot] + " (that layer stays flat)");
                    Done(job, slot, null, 0f);
                    return;
                }
                byte[] rgba = req.GetData<byte>().ToArray(); // copy off the native buffer for the worker
                Task.Run(() =>
                {
                    ushort[] h = null;
                    float range = 0f;
                    HeightGen.Workers.Wait();
                    try { h = HeightGen.IntegrateAndNormalize(rgba, Res, true, out range); }
                    catch (System.Exception ex) { Plugin.MyLog.LogError("[POMSix] Road height generation failed: " + ex.Message); }
                    finally { HeightGen.Workers.Release(); }
                    if (h != null) SaveCache(path, h, range);
                    Done(job, slot, h, range);
                });
            });
        }

        // Cache layout: 'P','O','M','R', version, int32 res, float32 range, then the R16 heights.
        private static void LoadCached(Job job, int slot, string path)
        {
            ushort[] h = null;
            float range = 0f;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                const int header = 13;
                if (data.Length == header + Res * Res * 2 && data[0] == 'P' && data[1] == 'O' && data[2] == 'M'
                    && data[3] == 'R' && data[4] == CacheVersion && System.BitConverter.ToInt32(data, 5) == Res)
                {
                    range = System.BitConverter.ToSingle(data, 9);
                    h = new ushort[Res * Res];
                    System.Buffer.BlockCopy(data, header, h, 0, Res * Res * 2);
                }
                else File.Delete(path); // regenerates next raid
            }
            catch (System.Exception ex) { Plugin.MyLog.LogError("[POMSix] Road height cache load failed: " + ex.Message); }
            Done(job, slot, h, range);
        }

        private static void SaveCache(string path, ushort[] h, float range)
        {
            try
            {
                var data = new byte[13 + h.Length * 2];
                data[0] = (byte)'P'; data[1] = (byte)'O'; data[2] = (byte)'M'; data[3] = (byte)'R'; data[4] = CacheVersion;
                System.BitConverter.GetBytes(Res).CopyTo(data, 5);
                System.BitConverter.GetBytes(range).CopyTo(data, 9);
                System.Buffer.BlockCopy(h, 0, data, 13, h.Length * 2);
                File.WriteAllBytes(path, data);
            }
            catch (System.Exception ex) { Plugin.MyLog.LogError("[POMSix] Road height cache save failed: " + ex.Message); }
        }

        private static void Done(Job job, int slot, ushort[] h, float range)
        {
            bool last;
            lock (job)
            {
                job.l0[slot] = h;
                job.ranges[slot] = h != null ? range : 0f;
                last = --job.remaining == 0;
            }
            if (!last) return;
            Task.Run(() =>
            {
                int n = job.l0.Length;
                job.chains = new ushort[n][][];
                job.levels = new float[n];
                for (int i = 0; i < n; i++)
                {
                    ushort[] l0 = job.l0[i];
                    if (l0 == null) // flat: everything at the top
                    {
                        l0 = new ushort[Res * Res];
                        for (int k = 0; k < l0.Length; k++) l0[k] = ushort.MaxValue;
                        job.levels[i] = 1f;
                    }
                    else job.levels[i] = Median(l0);
                    job.chains[i] = HeightGen.BuildChain(l0, false, Res);
                }
                _ready = job;
            });
        }

        // A layer's ground level: where most of its surface sits. Layers are lined up there, so what rises
        // above it (stones) stands on the neighbouring layer instead of ending flush with it.
        private static float Median(ushort[] h)
        {
            var hist = new int[4096];
            for (int i = 0; i < h.Length; i++) hist[h[i] >> 4]++;
            int half = h.Length / 2, acc = 0;
            for (int b = 0; b < hist.Length; b++) { acc += hist[b]; if (acc >= half) return (b + 0.5f) / hist.Length; }
            return 1f;
        }

        // Main thread, every frame: one slice (all mips) per frame, then bind and hand out the indices.
        public static void Pump()
        {
            if (_uploading == null)
            {
                Job r = _ready;
                if (r == null) return;
                _ready = null;
                if (r != _job) return; // superseded by a newer set
                _uploadTex = new Texture2DArray(Res, Res, r.ids.Length, TextureFormat.R16, true, true)
                { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Repeat };
                _uploading = r;
                _uploadIdx = 0;
                return;
            }

            Job u = _uploading;
            if (u != _job) { Object.Destroy(_uploadTex); _uploadTex = null; _uploading = null; return; }
            for (int mip = 0; mip < u.chains[_uploadIdx].Length; mip++)
                _uploadTex.SetPixelData(u.chains[_uploadIdx][mip], mip, _uploadIdx);
            if (++_uploadIdx < u.ids.Length) return;

            _uploadTex.Apply(false, true); // mips are ours (built off-thread)
            if (_tex != null) Object.Destroy(_tex);
            _tex = _uploadTex;
            _uploadTex = null;
            _slice.Clear();
            _tileRange = new float[u.ids.Length];
            _level = u.levels;
            float lo = float.MaxValue, hi = 0f;
            for (int i = 0; i < u.ids.Length; i++)
            {
                _slice[u.ids[i]] = i;
                _tileRange[i] = u.ranges[i] / Res;
                if (u.l0[i] != null) { lo = Mathf.Min(lo, _tileRange[i]); hi = Mathf.Max(hi, _tileRange[i]); }
            }
            Shader.SetGlobalTexture("_POMSixRoadHeights", _tex);
            _uploading = null;
            _job = null;
            Assign(RoadPom.Materials);
            Plugin.MyLog.LogInfo("[POMSix] Road heights bound (" + u.ids.Length + " normal maps, " + Res + "x" + Res
                + " 16-bit). Relief the normal maps describe: " + (lo * 100f).ToString("0.00") + "% to "
                + (hi * 100f).ToString("0.00") + "% of a texture tile.");
            if (PomConfig.ReconDump.Value)
            {
                var sb = new StringBuilder("[RoadRecon] road relief per normal map (% of its tile @ ground level):");
                for (int i = 0; i < u.names.Length; i++)
                    sb.Append(' ').Append(u.names[i]).Append('=').Append((_tileRange[i] * 100f).ToString("0.00")).Append('@').Append(_level[i].ToString("0.00"));
                Plugin.MyLog.LogInfo(sb.ToString());
            }
        }

        // Slice index, measured relief and ground level of each layer, per material. Layers whose normal map has no slice
        // stay flat; a material with none keeps road POM off (w = 0).
        public static void Assign(IEnumerable<Material> mats)
        {
            if (_tex == null) return;
            foreach (Material m in mats)
            {
                if (m == null) continue;
                Vector4 layers = new Vector4(-1f, -1f, -1f, 0f), ranges = Vector4.zero, levels = new Vector4(1f, 1f, 1f, 0f);
                for (int n = 0; n < 3; n++)
                {
                    if (!m.HasProperty(BumpProps[n])) continue;
                    Texture t = m.GetTexture(BumpProps[n]);
                    if (t == null || !_slice.TryGetValue(t.GetInstanceID(), out int s)) continue;
                    layers[n] = s;
                    ranges[n] = _tileRange[s];
                    levels[n] = _level[s];
                    layers.w = 1f;
                }
                m.SetVector("_POMSixRoadLayers", layers);
                m.SetVector("_POMSixRoadRanges", ranges);
                m.SetVector("_POMSixRoadLevels", levels);
            }
        }
    }
}
