using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace POMSix
{
    // REAL POM height maps, derived from the game's own _NormalSAO arrays (FFT Poisson integration of
    // the tangent normals; periodic boundaries are exact for tiling textures), plus the QDM max-mip
    // pyramid. ALL heavy work runs off the main thread (readback decode, FFT, normalization, both mip
    // chains, cache IO); the GPU upload is staged ONE LAYER PER FRAME (a single-frame bind was a visible
    // raid-start hitch).
    //
    // v2 (2026-09) data-quality fixes, each a defect of v1:
    //  - 1024² R16 (was 512² R8): full source resolution, 65536 height levels — no terracing for the
    //    contact refinement to smear.
    //  - HIGH-PASS before the inverse FFT. Integrating slopes weights content by 1/frequency, so v1's
    //    height range was dominated by tile-scale bowls POM can't represent, squeezing the stones into a
    //    sliver of the 0..1 range.
    //  - Shared physical scale: v1 stretched every layer to 0..1 independently (sand got as much relief
    //    as rubble). Layers are still STORED normalized (full precision) but each layer's measured range
    //    vs the roughest layer is published as _POMSixLayerAmp, and the shader's POMSixRemap reapplies it
    //    (Relief Balance knob, live — no regeneration to tune).
    public static class HeightGen
    {
        public const int Res = 1024;
        private const byte CacheVersion = 2;
        private const float HighPassCycles = 3f; // relief below ~3 cycles per texture tile is suppressed
        private const int MaxAmpLayers = 32;     // matches the shader's _POMSixLayerAmp[32]

        public static bool PyramidReady;
        public static int PyramidTopLevel;

        private class Prepared
        {
            public string key;
            public int depth;
            public ushort[][][] avg;  // per layer: box-filtered mip chain, [0] = full res
            public ushort[][][] max;  // per layer: max-reduction mip chain, [0] = full res (QDM)
            public float[] ranges;    // per layer: measured relief range (raw integrated units)
            public bool save;
        }

        private static string _currentKey;
        private static Texture2DArray _tex, _texMax;
        private static ushort[][] _pendingL0;   // normalized level-0 heights arriving from workers
        private static float[] _pendingRanges;
        private static int _remaining;
        private static string _debugDir;
        private static volatile Prepared _prepared; // worker -> main handoff
        private static Prepared _uploading;
        private static int _uploadIdx;
        // At most a quarter of the cores integrate at once: this runs during raid load, next to the
        // game's own loading threads, and each 1024² layer holds ~20MB of FFT buffers while working.
        private static readonly SemaphoreSlim _workers =
            new SemaphoreSlim(Mathf.Max(1, System.Environment.ProcessorCount / 4));
        private static readonly object _pendingLock = new object();
        private static readonly float[] _amps = NewOnes();

        private static float[] NewOnes()
        {
            var a = new float[MaxAmpLayers];
            for (int i = 0; i < a.Length; i++) a[i] = 1f;
            return a;
        }

        // Unity locks a global array's size at its FIRST set — publish the full 32 slots at startup.
        public static void PublishAmps() => Shader.SetGlobalFloatArray("_POMSixLayerAmp", _amps);

        // Kicked from PomApplier after a successful swap. Dedupes per map+array.
        public static void Request(Material m)
        {
            if (!m.HasProperty("_NormalSAO")) return;
            if (!(m.GetTexture("_NormalSAO") is Texture2DArray normals)) return;

            string key = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
                + "_" + normals.name + "_" + normals.depth + "_" + Res + "_hp" + HighPassCycles;
            foreach (char c in Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
            if (key == _currentKey) return;
            _currentKey = key;
            PyramidReady = false;
            DeleteStaleCaches();

            if (File.Exists(CachePath(key)))
            {
                int depth = normals.depth;
                Task.Run(() => PrepareFromCache(key, depth));
                return;
            }

            Plugin.MyLog.LogInfo("[POMSix] Generating height maps from '" + normals.name + "' ("
                + normals.depth + " layers, " + Res + "x" + Res + " 16-bit) - first raid on this map/season only, cached after.");
            _debugDir = PomConfig.ReconDump.Value
                ? Path.Combine(BepInEx.Paths.PluginPath, "POMSix", "HeightCache", "debug") : null;
            if (_debugDir != null) Directory.CreateDirectory(_debugDir);

            _pendingL0 = new ushort[normals.depth][];
            _pendingRanges = new float[normals.depth];
            _remaining = normals.depth;
            for (int layer = 0; layer < normals.depth; layer++)
                ReadbackLayer(normals, layer, key);
        }

        private static void ReadbackLayer(Texture2DArray normals, int layer, string key)
        {
            RenderTexture rt = RenderTexture.GetTemporary(Res, Res, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            Graphics.Blit(normals, rt, layer, 0);
            int l = layer;
            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req =>
            {
                RenderTexture.ReleaseTemporary(rt);
                if (key != _currentKey) return; // map changed mid-flight
                if (req.hasError)
                {
                    Plugin.MyLog.LogError("[POMSix] Height readback failed on layer " + l);
                    _remaining = -1; // abort; luminance fallback stays active
                    return;
                }
                byte[] rgba = req.GetData<byte>().ToArray(); // copy off the native buffer for the worker
                Task.Run(() =>
                {
                    _workers.Wait();
                    ushort[] norm;
                    float range;
                    try { norm = IntegrateAndNormalize(rgba, Res, out range); }
                    finally { _workers.Release(); }
                    ushort[][] doneL0 = null;
                    float[] doneRanges = null;
                    lock (_pendingLock)
                    {
                        if (key != _currentKey || _pendingL0 == null) return;
                        _pendingL0[l] = norm;
                        _pendingRanges[l] = range;
                        _remaining--;
                        if (_remaining == 0)
                        {
                            doneL0 = _pendingL0; doneRanges = _pendingRanges;
                            _pendingL0 = null; _pendingRanges = null;
                        }
                    }
                    if (doneL0 != null) Prepare(doneL0, doneRanges, key, save: true);
                });
            });
        }

        // Worker thread: both mip chains per layer, then hand to the main thread.
        private static void Prepare(ushort[][] l0, float[] ranges, string key, bool save)
        {
            var p = new Prepared { key = key, depth = l0.Length, ranges = ranges, save = save };
            p.avg = new ushort[p.depth][][];
            p.max = new ushort[p.depth][][];
            for (int l = 0; l < p.depth; l++)
            {
                p.avg[l] = BuildChain(l0[l], false);
                p.max[l] = BuildChain(l0[l], true);
            }
            if (key == _currentKey) _prepared = p;
        }

        private static void PrepareFromCache(string key, int depth)
        {
            try
            {
                byte[] data = File.ReadAllBytes(CachePath(key));
                int header = 5 + 4 + depth * 4;
                bool valid = data.Length == header + depth * Res * Res * 2
                    && data[0] == 'P' && data[1] == 'O' && data[2] == 'M'
                    && data[3] == CacheVersion && data[4] == depth
                    && System.BitConverter.ToInt32(data, 5) == Res;
                if (!valid)
                {
                    Plugin.MyLog.LogError("[POMSix] Height cache invalid - deleting (regenerates next raid).");
                    File.Delete(CachePath(key));
                    _currentKey = null;
                    return;
                }
                var ranges = new float[depth];
                var l0 = new ushort[depth][];
                for (int l = 0; l < depth; l++)
                {
                    ranges[l] = System.BitConverter.ToSingle(data, 9 + l * 4);
                    l0[l] = new ushort[Res * Res];
                    System.Buffer.BlockCopy(data, header + l * Res * Res * 2, l0[l], 0, Res * Res * 2);
                }
                Prepare(l0, ranges, key, save: false);
            }
            catch (System.Exception ex)
            { Plugin.MyLog.LogError("[POMSix] Height cache load failed: " + ex.Message); }
        }

        // Mip chain [0..top]: box average (bilinear heights for march/shadow/AO/blend) or max
        // (conservative pyramid for QDM).
        private static ushort[][] BuildChain(ushort[] l0, bool takeMax)
        {
            var chain = new List<ushort[]> { l0 };
            ushort[] cur = l0;
            int size = Res;
            while (size > 1)
            {
                int ns = size >> 1;
                var nb = new ushort[ns * ns];
                for (int y = 0; y < ns; y++)
                    for (int x = 0; x < ns; x++)
                    {
                        int i0 = (y * 2) * size + x * 2, i1 = i0 + 1, i2 = i0 + size, i3 = i2 + 1;
                        if (takeMax)
                        {
                            ushort mv = cur[i0];
                            if (cur[i1] > mv) mv = cur[i1];
                            if (cur[i2] > mv) mv = cur[i2];
                            if (cur[i3] > mv) mv = cur[i3];
                            nb[y * ns + x] = mv;
                        }
                        else nb[y * ns + x] = (ushort)((cur[i0] + cur[i1] + cur[i2] + cur[i3] + 2) >> 2);
                    }
                chain.Add(nb);
                cur = nb; size = ns;
            }
            return chain.ToArray();
        }

        // Main thread, every frame: create textures, upload ONE layer (all mips) per frame, then bind.
        public static void Pump()
        {
            if (_uploading == null)
            {
                Prepared p = _prepared;
                if (p == null) return;
                _prepared = null;
                if (p.key != _currentKey) return;
                if (_tex != null) Object.Destroy(_tex);
                if (_texMax != null) Object.Destroy(_texMax);
                _tex = new Texture2DArray(Res, Res, p.depth, TextureFormat.R16, true, true)
                { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Repeat };
                // MAX pyramid: POINT filtered — bilinear on a max-mip breaks QDM's conservativity.
                _texMax = new Texture2DArray(Res, Res, p.depth, TextureFormat.R16, true, true)
                { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
                PyramidTopLevel = p.avg[0].Length - 1;
                _uploading = p;
                _uploadIdx = 0;
                return;
            }

            Prepared u = _uploading;
            if (u.key != _currentKey) { _uploading = null; return; }
            int layer = _uploadIdx;
            for (int mip = 0; mip < u.avg[layer].Length; mip++)
            {
                _tex.SetPixelData(u.avg[layer][mip], mip, layer);
                _texMax.SetPixelData(u.max[layer][mip], mip, layer);
            }
            if (_debugDir != null) DumpPng(u.avg[layer][0], layer);
            _uploadIdx++;
            if (_uploadIdx < u.depth) return;

            _tex.Apply(false, true);    // mips are ours (CPU-built off-thread) — no main-thread mipgen
            _texMax.Apply(false, true);
            PublishLayerAmps(u.ranges);
            Shader.SetGlobalTexture("_POMSixHeights", _tex);
            Shader.SetGlobalTexture("_POMSixHeightsMax", _texMax);
            Shader.SetGlobalFloat("_POMSixHasHeights", 1f);
            PyramidReady = true;
            PomApplier.ApplyGlobals();
            Plugin.MyLog.LogInfo("[POMSix] Heights bound (" + u.depth + " layers, " + Res + "x" + Res
                + " 16-bit, top mip " + PyramidTopLevel + ").");
            if (u.save)
            {
                string path = CachePath(u.key);
                ushort[][] l0 = new ushort[u.depth][];
                for (int l = 0; l < u.depth; l++) l0[l] = u.avg[l][0];
                float[] ranges = u.ranges;
                Task.Run(() => SaveCache(path, l0, ranges));
            }
            _debugDir = null;
            _uploading = null;
        }

        // Relief amplitude of each layer relative to the roughest one = the shared physical scale.
        private static void PublishLayerAmps(float[] ranges)
        {
            float maxRange = 1e-6f;
            foreach (float r in ranges) maxRange = Mathf.Max(maxRange, r);
            for (int i = 0; i < _amps.Length; i++)
                _amps[i] = i < ranges.Length ? Mathf.Clamp01(ranges[i] / maxRange) : 1f;
            PublishAmps();
            var sb = new System.Text.StringBuilder("[POMSix] Layer relief amplitudes (1 = roughest):");
            for (int i = 0; i < ranges.Length; i++) sb.Append(' ').Append(i).Append('=').Append(_amps[i].ToString("0.00"));
            Plugin.MyLog.LogInfo(sb.ToString());
        }

        //--- normal → height: FFT Poisson integration ----------------------------------------------

        private static ushort[] IntegrateAndNormalize(byte[] rgba, int n, out float range)
        {
            // _NormalSAO packing (from the generated shader): normal.x = A, normal.y = G (DXT5nm).
            var re = new float[n * n];   // gx, becomes Re(H)
            var im = new float[n * n];   // Im(Gx), becomes Im(H)
            var gy = new float[n * n];
            var gyIm = new float[n * n];
            for (int i = 0; i < n * n; i++)
            {
                float nx = rgba[i * 4 + 3] / 255f * 2f - 1f;
                float ny = rgba[i * 4 + 1] / 255f * 2f - 1f;
                float nz = Mathf.Sqrt(Mathf.Max(0.02f, 1f - nx * nx - ny * ny));
                re[i] = -nx / nz; // n ∝ (-dh/dx, -dh/dy, 1)
                gy[i] = -ny / nz;
            }
            Fft2D(re, im, n, false);
            Fft2D(gy, gyIm, n, false);

            // H(k) = (-i·wx·Gx - i·wy·Gy) / (wx² + wy²), DC = 0, then a SECOND-order high-pass
            // (r²/(r²+r0²))² with r in cycles per tile. First order was measured too gentle: integration
            // boosts a 1-cycle bowl ~16x relative to stone-scale content, a first-order cut only removes
            // ~10x, and the synthetic test still tracked the bowl (corr 0.90 bowl vs 0.44 stones).
            // Written in place: each index reads its four inputs before writing.
            float r0sq = HighPassCycles * HighPassCycles;
            for (int ky = 0; ky < n; ky++)
            {
                float kyc = ky <= n / 2 ? ky : ky - n;
                float wy = 2f * Mathf.PI * kyc / n;
                for (int kx = 0; kx < n; kx++)
                {
                    float kxc = kx <= n / 2 ? kx : kx - n;
                    float wx = 2f * Mathf.PI * kxc / n;
                    int i = ky * n + kx;
                    float denom = wx * wx + wy * wy;
                    if (denom < 1e-12f) { re[i] = 0f; im[i] = 0f; continue; }
                    float r2 = kxc * kxc + kyc * kyc;
                    float hp = r2 / (r2 + r0sq);
                    hp *= hp;
                    float gxRe = re[i], gxIm = im[i];
                    re[i] = (wx * gxIm + wy * gyIm[i]) / denom * hp;
                    im[i] = (-wx * gxRe - wy * gy[i]) / denom * hp;
                }
            }
            gy = null; gyIm = null;
            Fft2D(re, im, n, true);

            // 2nd/98th percentile via a histogram (min/max is hostage to single outlier texels, and
            // sorting a million floats per layer is wasted work).
            float mn = float.MaxValue, mx = float.MinValue;
            for (int i = 0; i < re.Length; i++) { float v = re[i]; if (v < mn) mn = v; if (v > mx) mx = v; }
            const int bins = 4096;
            var hist = new int[bins];
            float span = Mathf.Max(1e-9f, mx - mn);
            for (int i = 0; i < re.Length; i++) hist[(int)((re[i] - mn) / span * (bins - 1))]++;
            int loTarget = (int)(re.Length * 0.02f), hiTarget = (int)(re.Length * 0.98f), acc = 0, loBin = 0, hiBin = bins - 1;
            for (int b = 0; b < bins; b++)
            {
                if (acc <= loTarget) loBin = b;
                acc += hist[b];
                if (acc >= hiTarget) { hiBin = b; break; }
            }
            float lo = mn + span * loBin / (bins - 1);
            float hi = mn + span * hiBin / (bins - 1);
            range = Mathf.Max(1e-6f, hi - lo);

            var outH = new ushort[n * n];
            for (int i = 0; i < re.Length; i++)
                outH[i] = (ushort)(Mathf.Clamp01((re[i] - lo) / range) * 65535f + 0.5f);
            return outH;
        }

        // Iterative radix-2 Cooley-Tukey, rows then columns; inverse normalizes by 1/N per axis.
        private static void Fft2D(float[] re, float[] im, int n, bool inverse)
        {
            var tRe = new float[n];
            var tIm = new float[n];
            for (int row = 0; row < n; row++)
                Fft1D(re, im, row * n, 1, n, inverse, tRe, tIm);
            for (int col = 0; col < n; col++)
                Fft1D(re, im, col, n, n, inverse, tRe, tIm);
        }

        private static void Fft1D(float[] re, float[] im, int offset, int stride, int n, bool inverse,
            float[] tRe, float[] tIm)
        {
            for (int i = 0; i < n; i++) { tRe[i] = re[offset + i * stride]; tIm[i] = im[offset + i * stride]; }

            for (int i = 1, j = 0; i < n; i++) // bit-reversal permutation
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (tRe[i], tRe[j]) = (tRe[j], tRe[i]);
                    (tIm[i], tIm[j]) = (tIm[j], tIm[i]);
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                float ang = 2f * Mathf.PI / len * (inverse ? 1f : -1f);
                float wRe = Mathf.Cos(ang), wIm = Mathf.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float curRe = 1f, curIm = 0f;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        float xr = tRe[b] * curRe - tIm[b] * curIm;
                        float xi = tRe[b] * curIm + tIm[b] * curRe;
                        tRe[b] = tRe[a] - xr; tIm[b] = tIm[a] - xi;
                        tRe[a] += xr; tIm[a] += xi;
                        float nRe = curRe * wRe - curIm * wIm;
                        curIm = curRe * wIm + curIm * wRe;
                        curRe = nRe;
                    }
                }
            }
            float norm = inverse ? 1f / n : 1f;
            for (int i = 0; i < n; i++)
            {
                re[offset + i * stride] = tRe[i] * norm;
                im[offset + i * stride] = tIm[i] * norm;
            }
        }

        //--- disk cache ---------------------------------------------------------------------------
        // v2 layout: 'P','O','M', version, depth, int32 res, float32 range × depth, then level-0 R16
        // heights × depth. Mip chains are rebuilt on load (cheap, off-thread).

        private static string CacheDir => Path.Combine(BepInEx.Paths.PluginPath, "POMSix", "HeightCache");
        private static string CachePath(string key) => Path.Combine(CacheDir, key + ".bin");

        private static void SaveCache(string path, ushort[][] l0, float[] ranges)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                int depth = l0.Length, header = 5 + 4 + depth * 4;
                var data = new byte[header + depth * Res * Res * 2];
                data[0] = (byte)'P'; data[1] = (byte)'O'; data[2] = (byte)'M'; data[3] = CacheVersion; data[4] = (byte)depth;
                System.BitConverter.GetBytes(Res).CopyTo(data, 5);
                for (int l = 0; l < depth; l++)
                {
                    System.BitConverter.GetBytes(ranges[l]).CopyTo(data, 9 + l * 4);
                    System.Buffer.BlockCopy(l0[l], 0, data, header + l * Res * Res * 2, Res * Res * 2);
                }
                File.WriteAllBytes(path, data);
            }
            catch (System.Exception ex)
            { Plugin.MyLog.LogError("[POMSix] Height cache save failed: " + ex.Message); }
        }

        // v1 caches (512² R8, "_512.bin") can never be read again — reclaim the disk once.
        private static bool _staleChecked;
        private static void DeleteStaleCaches()
        {
            if (_staleChecked) return;
            _staleChecked = true;
            try
            {
                if (!Directory.Exists(CacheDir)) return;
                foreach (string f in Directory.GetFiles(CacheDir, "*_512.bin"))
                {
                    File.Delete(f);
                    Plugin.MyLog.LogInfo("[POMSix] Removed outdated v1 height cache " + Path.GetFileName(f));
                }
            }
            catch (System.Exception ex) { Plugin.MyLog.LogWarning("[POMSix] Stale cache cleanup skipped: " + ex.Message); }
        }

        private static void DumpPng(ushort[] r16, int layer)
        {
            var bytes = new byte[r16.Length];
            for (int i = 0; i < r16.Length; i++) bytes[i] = (byte)(r16[i] >> 8);
            var tex = new Texture2D(Res, Res, TextureFormat.R8, false);
            tex.SetPixelData(bytes, 0);
            tex.Apply(false, false);
            File.WriteAllBytes(Path.Combine(_debugDir, _currentKey + "_layer" + layer + ".png"), tex.EncodeToPNG());
            Object.Destroy(tex);
        }
    }
}
