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

            Shader sh = m.shader;
            sb.Append("  material '").Append(m.name).Append("' shader '").Append(sh != null ? sh.name : "<null>")
              .Append("' queue=").Append(m.renderQueue).Append(" passes=").Append(m.passCount).AppendLine();
            sb.Append("  matKeywords: ").Append(string.Join(" ", m.shaderKeywords)).AppendLine();

            // The zero-cost shortcut check: if the shipped shader already declares the parallax/tess
            // module properties, POM is compiled in and may just need enabling.
            sb.Append("  POM check: _POMParams=").Append(m.HasProperty("_POMParams"))
              .Append(" _ParallaxParams=").Append(m.HasProperty("_ParallaxParams"))
              .Append(" _TessData1=").Append(m.HasProperty("_TessData1"))
              .Append(" _TessData2=").Append(m.HasProperty("_TessData2")).AppendLine();

            if (sh != null)
            {
                int n = sh.GetPropertyCount();
                sb.Append("  shader properties (").Append(n).AppendLine("):");
                for (int i = 0; i < n; i++)
                {
                    string pn = sh.GetPropertyName(i);
                    ShaderPropertyType pt = sh.GetPropertyType(i);
                    sb.Append("    ").Append(pn).Append(" [").Append(pt).Append("] ");
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
            Log(sb);
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
    }
}
