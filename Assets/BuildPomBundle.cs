using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Builds the POMSix terrain shaders (POM and/or Tessellation) + the road shader into the `pom`
// AssetBundle and deploys it into the game.
//
// MicroSplat bakes POM vs Tessellation into the shader at generation time, so the two variants are
// kept as captured SOURCE snapshots (Assets/POMSix/Source/*.txt — TextAssets, never compiled by Unity):
//   1. whatever MicroSplat currently generated in Assets/MicroSplatData is detected (POM or Tessellation)
//      and captured into its slot, overwriting that slot;
//   2. every captured slot is patched by PomShaderPatcher (also run offline by
//      F:\SPT-POM\Tools\pomshadercheck, which compiles every pass/stage with fxc), imported and validated;
//   3. all of them + the road shader go into one bundle — the plugin swaps between them at runtime.
// So: generate POM once, build; switch MicroSplat to Tessellation, build again — both stay in the bundle.
// The Hybrid (tessellation + POM residual) is synthesized from the Tessellation capture automatically.
public static class BuildPomBundle
{
    const string PatchedDir = "Assets/POMSix";
    const string SourceDir = PatchedDir + "/Source";
    const string RoadShaderPath = PatchedDir + "/POMSixRoad.shader";
    // The live install is SPT 4.1 (moved 2026-09-14). A stale path here silently ships the bundle to an
    // install nobody runs — the stale-DLL trap that already cost debugging rounds.
    const string GameDst = @"F:\SPT4.1\BepInEx\plugins\POMSix\Assets\pom";

    const string FuncPomPath = "Packages/com.jbooth.microsplat.tessellation/Scripts/Editor/microsplat_func_pom.txt";

    static string SourcePath(PomShaderPatcher.Variant v) => SourceDir + (
        v == PomShaderPatcher.Variant.Pom ? "/MicroSplat_POM.txt"
        : v == PomShaderPatcher.Variant.Tess ? "/MicroSplat_Tess.txt" : "/MicroSplat_Hybrid.txt");

    // Hybrid: a captured tess+POM generation if one exists, else synthesized from the tessellation capture
    // + MicroSplat's POM module text (identical to what the generator emits with both features on).
    static string LoadSource(PomShaderPatcher.Variant v)
    {
        string path = SourcePath(v);
        if (File.Exists(path)) return File.ReadAllText(path);
        if (v != PomShaderPatcher.Variant.Hybrid || !File.Exists(SourcePath(PomShaderPatcher.Variant.Tess))) return null;
        string funcPom = Path.GetFullPath(FuncPomPath);
        if (!File.Exists(funcPom)) { Debug.LogWarning("[POMSix] Hybrid skipped - MicroSplat POM module text not found at " + FuncPomPath); return null; }
        string hybrid = PomShaderPatcher.SynthesizeHybrid(File.ReadAllText(SourcePath(PomShaderPatcher.Variant.Tess)),
            File.ReadAllText(funcPom), out string error);
        if (hybrid == null) Debug.LogError("[POMSix] Hybrid synthesis: " + error);
        return hybrid;
    }

    [MenuItem("Tools/POMSix/Build POM Bundle")]
    public static void Build()
    {
        // 1. Capture the current MicroSplat generation into its variant slot.
        string[] generated = Directory.GetFiles("Assets/MicroSplatData", "*.shader", SearchOption.AllDirectories)
            .Where(p => !Path.GetFileNameWithoutExtension(p).EndsWith("_Base")).ToArray();
        if (generated.Length != 1)
        {
            Debug.LogError("[POMSix] Expected exactly 1 non-Base shader in Assets/MicroSplatData, found "
                + generated.Length + ":\n" + string.Join("\n", generated));
            return;
        }
        string gen = File.ReadAllText(generated[0]);
        if (!PomShaderPatcher.TryDetect(gen, out var current, out string detectError))
        {
            Debug.LogError("[POMSix] " + detectError);
            return;
        }
        Directory.CreateDirectory(SourceDir);
        File.WriteAllText(SourcePath(current), gen);
        Debug.Log("[POMSix] Captured the current MicroSplat generation as the " + current + " source.");

        // 2. Patch + validate every captured variant.
        var assets = new List<string>();
        foreach (var variant in new[] { PomShaderPatcher.Variant.Pom, PomShaderPatcher.Variant.Tess, PomShaderPatcher.Variant.Hybrid })
        {
            string source = LoadSource(variant);
            if (source == null)
            {
                Debug.LogWarning("[POMSix] No " + variant + " source captured yet - the bundle won't include it. "
                    + "Generate that variant in MicroSplat and build again to add it (Hybrid comes from the Tessellation one).");
                continue;
            }
            string patched = PomShaderPatcher.Apply(source, Debug.Log, out string error, out var detected);
            if (patched == null) { Debug.LogError("[POMSix] " + variant + ": " + error + " NOT bundling."); return; }
            if (detected != variant) { Debug.LogError("[POMSix] " + variant + " source holds a " + detected + " shader. NOT bundling."); return; }

            string path = PatchedDir + "/" + PomShaderPatcher.PatchedFileName(variant);
            File.WriteAllText(path, patched);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            // ShaderHasError can lag the import; isSupported catches the "All subshaders removed" case.
            if (shader == null || ShaderUtil.ShaderHasError(shader) || !shader.isSupported)
            {
                Debug.LogError("[POMSix] Patched " + variant + " shader failed to compile - select " + path
                    + " and check the inspector. NOT bundling.");
                return;
            }
            assets.Add(path);
        }
        if (assets.Count == 0) { Debug.LogError("[POMSix] No terrain shader to bundle."); return; }

        var roadShader = AssetDatabase.LoadAssetAtPath<Shader>(RoadShaderPath);
        if (roadShader == null || ShaderUtil.ShaderHasError(roadShader) || !roadShader.isSupported)
        {
            Debug.LogError("[POMSix] Road shader missing or failed to compile - check " + RoadShaderPath);
            return;
        }
        assets.Add(RoadShaderPath);

        // 3. One bundle with everything.
        string outDir = "AssetBundleBuild";
        Directory.CreateDirectory(outDir);
        var build = new AssetBundleBuild { assetBundleName = "pom", assetNames = assets.ToArray() };
        // ForceRebuild: the incremental cache ignores Graphics Settings (shader stripping) changes.
        var manifest = BuildPipeline.BuildAssetBundles(outDir, new[] { build },
            BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows64);
        if (manifest == null) { Debug.LogError("[POMSix] Bundle build failed."); return; }

        Directory.CreateDirectory(Path.GetDirectoryName(GameDst));
        File.Copy(Path.Combine(outDir, "pom"), GameDst, true);

        long size = new FileInfo(GameDst).Length;
        string sizeStr = (size / 1024f / 1024f).ToString("0.00") + " MB";
        if (size < 1024 * 1024)
            Debug.LogWarning("[POMSix] Bundle is only " + sizeStr + " - that smells like stripped variants (Graphics "
                + "Settings > Shader Stripping: Instancing Keep All, Lightmap/Fog Custom-all). Deployed anyway to " + GameDst);
        else
            Debug.Log("[POMSix] Built pom bundle (" + sizeStr + ": " + string.Join(", ", assets.Select(Path.GetFileName))
                + ") -> " + GameDst);
    }
}
