using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

// Offline POMSix shader check — no Unity editor needed.
//   check [shaderPath] [--nopatch]   patch the MicroSplat-generated shader with PomShaderPatcher (unless
//                                    --nopatch: compile an already-patched file as-is) and compile every
//                                    pass × keyword variant × stage with fxc. Exit code 1 on any error.
//   repro <expectedPatchedShader>     patch the generated shader and diff against a known output.
// Recipe per memory note unity-shader-offline-fxc-check: pass source with #pragma lines stripped,
// /Gec REQUIRED (Unity's backwards-compat flag), SHADER_API_D3D11, target<5 → *_4_0 profiles.
static class Program
{
    const string Fxc = @"C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\fxc.exe";
    const string CgIncludes = @"C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Data\CGIncludes";
    const string Generated = @"F:\Unity Game Project\tarkov stuff\Assets\MicroSplatData\MicroSplat.shader";
    const string FuncPom = @"F:\Unity Game Project\tarkov stuff\Packages\com.jbooth.microsplat.tessellation\Scripts\Editor\microsplat_func_pom.txt";

    // Keyword sets per LightMode — the variants that matter in Tarkov (drawInstanced terrain, HDR,
    // shadowmask MRT, both shadow caster paths).
    static readonly Dictionary<string, string[][]> Variants = new Dictionary<string, string[][]>
    {
        ["ForwardBase"] = new[] { new[] { "DIRECTIONAL", "LIGHTPROBE_SH" }, new[] { "DIRECTIONAL", "LIGHTPROBE_SH", "SHADOWS_SCREEN", "INSTANCING_ON" } },
        ["ForwardAdd"] = new[] { new[] { "POINT" }, new[] { "SPOT", "SHADOWS_DEPTH" } },
        ["Deferred"] = new[] { new string[0], new[] { "INSTANCING_ON" }, new[] { "INSTANCING_ON", "UNITY_HDR_ON", "LIGHTPROBE_SH" },
                               new[] { "INSTANCING_ON", "UNITY_HDR_ON", "SHADOWS_SHADOWMASK", "LIGHTPROBE_SH" } },
        ["ShadowCaster"] = new[] { new[] { "SHADOWS_DEPTH" }, new[] { "SHADOWS_DEPTH", "INSTANCING_ON" }, new[] { "SHADOWS_CUBE" } },
        ["Meta"] = new[] { new string[0] },
    };

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "check";
        string work = Path.Combine(Path.GetTempPath(), "pomshadercheck");
        Directory.CreateDirectory(work);

        if (mode == "repro")
        {
            string patched = PomShaderPatcher.Apply(File.ReadAllText(Generated), Console.WriteLine, out string err);
            if (patched == null) { Console.WriteLine("PATCH FAILED: " + err); return 1; }
            string expected = File.ReadAllText(args[1]);
            string a = patched.Replace("\r\n", "\n"), b = expected.Replace("\r\n", "\n");
            if (a == b) { Console.WriteLine("REPRO OK: patcher output is identical to " + args[1]); return 0; }
            int i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            int line = a.Substring(0, i).Split('\n').Length;
            Console.WriteLine("REPRO DIFF at char " + i + " (line " + line + "):");
            Console.WriteLine("  patcher : " + Snip(a, i));
            Console.WriteLine("  expected: " + Snip(b, i));
            return 1;
        }

        string shaderPath = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Generated;
        bool noPatch = Array.IndexOf(args, "--nopatch") >= 0;
        string src = File.ReadAllText(shaderPath);
        // --hybrid: shaderPath is the TESSELLATION source; build the tess+POM hybrid from it first.
        if (Array.IndexOf(args, "--hybrid") >= 0)
        {
            src = PomShaderPatcher.SynthesizeHybrid(src, File.ReadAllText(FuncPom), out string herr);
            if (src == null) { Console.WriteLine("HYBRID SYNTHESIS FAILED: " + herr); return 1; }
        }
        if (!noPatch)
        {
            src = PomShaderPatcher.Apply(src, Console.WriteLine, out string err, out var variant);
            if (src == null) { Console.WriteLine("PATCH FAILED: " + err); return 1; }
            File.WriteAllText(Path.Combine(work, PomShaderPatcher.PatchedFileName(variant)), src);
        }

        int failures = 0, compiles = 0;
        var passRx = new Regex("\"LightMode\"\\s*=\\s*\"(\\w+)\"");
        int searchFrom = 0, passIdx = 0;
        while (true)
        {
            int cg = src.IndexOf("CGPROGRAM", searchFrom, StringComparison.Ordinal);
            if (cg < 0) break;
            int end = src.IndexOf("ENDCG", cg, StringComparison.Ordinal);
            int passStart = src.LastIndexOf("Pass", cg, StringComparison.Ordinal);
            var lm = passRx.Match(src.Substring(passStart, cg - passStart));
            string lightMode = lm.Success ? lm.Groups[1].Value : "Unknown";
            string body = src.Substring(cg + "CGPROGRAM".Length, end - cg - "CGPROGRAM".Length);
            searchFrom = end + 5;

            string vs = Pragma(body, "vertex"), ps = Pragma(body, "fragment");
            string hs = Pragma(body, "hull"), ds = Pragma(body, "domain"); // tessellation shaders only
            string target = Pragma(body, "target") ?? "3.0";
            string stripped = Regex.Replace(body, @"^[ \t]*#pragma[^\n]*", "", RegexOptions.Multiline);
            string hlsl = Path.Combine(work, "pass" + passIdx + "_" + lightMode + ".hlsl");
            File.WriteAllText(hlsl, stripped);

            if (!Variants.TryGetValue(lightMode, out var sets)) sets = new[] { new string[0] };
            foreach (var kw in sets)
            {
                bool inst = Array.IndexOf(kw, "INSTANCING_ON") >= 0;
                string tgt = inst && target == "3.0" ? "35" : target.Replace(".", "");
                // Tessellation (target 4.6, hull/domain stages) needs the SM5 profiles.
                double tv = double.Parse(target, System.Globalization.CultureInfo.InvariantCulture);
                string prof = tv >= 4.6 ? "5_0" : "4_0";
                var stages = new List<(string, string)> { (vs, "vs"), (ps, "ps") };
                if (hs != null) stages.Add((hs, "hs"));
                if (ds != null) stages.Add((ds, "ds"));
                foreach (var (entry, stage) in stages)
                {
                    compiles++;
                    var (ok, output) = Compile(hlsl, stage + "_" + prof, entry, tgt, kw, work);
                    string label = "pass" + passIdx + " " + lightMode + " " + stage + " [" + string.Join(" ", kw) + "]";
                    int warns = Regex.Matches(output, "warning X").Count;
                    if (ok) Console.WriteLine("  OK   " + label + (warns > 0 ? "  (" + warns + " warnings)" : ""));
                    else
                    {
                        failures++;
                        Console.WriteLine("  FAIL " + label);
                        foreach (string l in output.Split('\n'))
                            if (l.Contains("error")) Console.WriteLine("       " + l.Trim());
                    }
                }
            }
            passIdx++;
        }
        Console.WriteLine(failures == 0 ? "ALL " + compiles + " COMPILES PASSED" : failures + " of " + compiles + " COMPILES FAILED");
        return failures == 0 ? 0 : 1;
    }

    static string Pragma(string body, string name)
    {
        var m = Regex.Match(body, @"#pragma\s+" + name + @"\s+(\S+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    static (bool, string) Compile(string hlsl, string profile, string entry, string target, string[] kw, string work)
    {
        var psi = new ProcessStartInfo(Fxc)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        // UNITY_PBS_USE_BRDF1 + the speccube defines come from Unity's graphics TIER settings (PC high
        // tier), not the shader — without BRDF1, UnityPBSLighting.cginc hits its '#error something broke
        // in auto-choosing BRDF' on every pass (found calibrating against the known-good shipped shader).
        foreach (string a in new[] { "/nologo", "/T", profile, "/E", entry, "/I", CgIncludes,
                                     "/D", "SHADER_API_D3D11=1", "/D", "SHADER_TARGET=" + target,
                                     "/D", "UNITY_PBS_USE_BRDF1=1", "/D", "UNITY_SPECCUBE_BOX_PROJECTION=1",
                                     "/D", "UNITY_SPECCUBE_BLENDING=1", "/Gec",
                                     "/Fo", Path.Combine(work, "out.cso") })
            psi.ArgumentList.Add(a);
        foreach (string k in kw) { psi.ArgumentList.Add("/D"); psi.ArgumentList.Add(k + "=1"); }
        psi.ArgumentList.Add(hlsl);
        using var p = Process.Start(psi);
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode == 0, o);
    }

    static string Snip(string s, int i) => s.Substring(Math.Max(0, i - 40), Math.Min(120, s.Length - Math.Max(0, i - 40))).Replace("\n", "\\n");
}
