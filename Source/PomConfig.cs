using BepInEx.Configuration;

namespace POMSix
{
    // Read by BepInEx ConfigurationManager via reflection (matched by type name) — entries tagged
    // with IsAdvanced only show when "Advanced Settings" is enabled in the F12 manager.
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? IsAdvanced;
    }

    public enum DisplacementMode { POM, Tessellation, Hybrid }

    public static class PomConfig
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<DisplacementMode> Mode;
        public static ConfigEntry<float> TessDisplacement;
        public static ConfigEntry<float> TriangleSize;
        public static ConfigEntry<bool> PatchCulling;
        public static ConfigEntry<float> Height;
        public static ConfigEntry<float> FadeStart;
        public static ConfigEntry<float> FadeDistance;
        public static ConfigEntry<int> Steps;
        public static ConfigEntry<bool> Qdm;
        public static ConfigEntry<bool> DerivedNormals;
        public static ConfigEntry<int> Smoothing;
        public static ConfigEntry<bool> Roads;
        public static ConfigEntry<float> RoadHeight;
        public static ConfigEntry<bool> RoadMask;
        public static ConfigEntry<float> ShadowStrength;
        public static ConfigEntry<float> AoStrength;
        public static ConfigEntry<float> ReliefBalance;
        public static ConfigEntry<float> GrazingDepth;
        public static ConfigEntry<float> HeightBlending;
        public static ConfigEntry<bool> DepthOffset;
        public static ConfigEntry<bool> BlendedMarch;
        public static ConfigEntry<bool> ReconDump;
        public static ConfigEntry<int> DebugView;

        public static void Bind(ConfigFile config)
        {
            var advanced = new ConfigurationManagerAttributes { IsAdvanced = true };

            Enabled = config.Bind("POM", "Enabled", true, "Parallax occlusion mapping on terrain.");
            Mode = config.Bind("POM", "Displacement Mode", DisplacementMode.POM,
                "POM = per-pixel parallax (cheapest, fakes depth inside the flat surface). Tessellation = the "
                + "terrain mesh is really subdivided and displaced near you (true silhouettes, but a mesh can "
                + "only hold shapes a few triangles wide, so fine detail is softer). Hybrid = both as ONE "
                + "surface: tessellation builds the big shapes and silhouettes, POM carves the fine detail the "
                + "mesh can't hold (heaviest, best). All use the same generated heights; roads keep their POM.");
            TessDisplacement = config.Bind("Tessellation", "Displacement", 0.15f, new ConfigDescription(
                "How deep the relief goes in Tessellation and Hybrid, in meters (in Hybrid this is the total: "
                + "geometry + POM carve; POM Height is unused there). Carved INTO the ground (stone tops stay "
                + "on the walkable surface), so feet and roads never sink into raised terrain.",
                new AcceptableValueRange<float>(0f, 0.3f)));
            TriangleSize = config.Bind("Tessellation", "Triangle Size", 20f, new ConfigDescription(
                "Target on-screen size of tessellated triangles, in pixels (Tessellation and Hybrid). Smaller = "
                + "denser mesh and finer geometric detail, more GPU. The height detail each vertex samples is "
                + "matched to this density, so the surface stays stable (no swimming) at any setting.",
                new AcceptableValueRange<float>(4f, 40f)));
            Height = config.Bind("POM", "Height", 0.045f, new ConfigDescription(
                "Displacement strength. The illusion of relief POM digs into the ground.",
                new AcceptableValueRange<float>(0f, 0.4f)));
            FadeStart = config.Bind("POM", "Fade Start", 15f, new ConfigDescription(
                "Distance where POM starts fading out. Main perf knob — POM costs per-pixel, x2 in VR.",
                new AcceptableValueRange<float>(1f, 100f)));
            FadeDistance = config.Bind("POM", "Fade Distance", 20f, new ConfigDescription(
                "Fade-out range past Fade Start (fully off at start + distance).",
                new AcceptableValueRange<float>(1f, 200f)));
            Steps = config.Bind("POM", "Steps", 32, new ConfigDescription(
                "Max ray-march steps. The full count is spent at GRAZING angles (ground-level views, "
                + "where rays cross the most texels); looking straight down uses about a third. "
                + "Higher = cleaner steep relief, more cost.",
                new AcceptableValueRange<int>(2, 32)));
            Smoothing = config.Bind("POM", "Smoothing", 0, new ConfigDescription(
                "Mip levels of blur on the derived height field. Rounds the relief into coarse shapes "
                + "(fine detail stays in the normal map); 0 = raw per-texel height (crunchy/spiky). In "
                + "Tessellation/Hybrid each level also adds half a mip of safety blur to the mesh heights.",
                new AcceptableValueRange<int>(0, 4)));
            // Defaults below are tuned in game.
            ShadowStrength = config.Bind("POM", "Self Shadow", 0.1f, new ConfigDescription(
                "Stones cast soft shadows into crevices along the sun/moon direction (Prism horizon "
                + "method on the height mips). 0 = off. Terrain only.",
                new AcceptableValueRange<float>(0f, 1f)));
            AoStrength = config.Bind("POM", "Height AO", 1.0f, new ConfigDescription(
                "Ambient occlusion from the height-field horizon — pits and gaps darken ambient light. "
                + "0 = off. Terrain only.",
                new AcceptableValueRange<float>(0f, 1f)));
            ReliefBalance = config.Bind("POM", "Relief Balance", 1.0f, new ConfigDescription(
                "How relief depth is shared between ground materials. 1 = relative depth measured from "
                + "each texture's own normal map (sand stays nearly flat, rubble goes deep); 0 = every "
                + "material gets the same depth. Live — no regeneration.",
                new AcceptableValueRange<float>(0f, 1f)));
            GrazingDepth = config.Bind("POM", "Grazing Depth", 0.7f, new ConfigDescription(
                "Relief depth when looking ALONG the ground (standing, crouched, prone). 0 = MicroSplat's "
                + "stock flattening (relief never deeper than seen from ~45°, so eye-level ground reads 3-10x "
                + "too shallow); 1 = physically correct parallax. If very low angles show banding or "
                + "swimming, raise Steps or lower this. Terrain POM + roads.",
                new AcceptableValueRange<float>(0f, 1f)));
            HeightBlending = config.Bind("POM", "Height Blending", 1.0f, new ConfigDescription(
                "Blend ground materials by their real heights instead of the game's linear cross-fade: "
                + "stones poke through dirt, sand fills the gaps between rocks. 0 = vanilla blending. "
                + "Fades back to vanilla with the POM fade distance, so the far terrain handoff stays seamless.",
                new AcceptableValueRange<float>(0f, 1f)));
            DepthOffset = config.Bind("POM", "Depth Offset", true,
                "Write the displaced surface depth, in the camera AND the shadow map. Objects sink into "
                + "pits instead of clipping at a flat line, sun shadows fall across the relief, and "
                + "reflections/AO/fog/upscaler reprojection all see the displaced ground (less POM "
                + "smearing in motion). Terrain only.");
            Roads = config.Bind("POM", "Roads", true,
                "Swap road/path/sidewalk materials to the POMSix road shader (POM marches BSG's own "
                + "authored height masks).");
            RoadMask = config.Bind("Roads", "Flatten Under Roads", true,
                "Fade the terrain carve (tessellation / POM depth) to nothing under and beside roads, from a "
                + "top-down mask of the road meshes built at raid load. Roads sit on level ground; carving rock "
                + "pits there left a depth cliff along every crumbled edge that ambient occlusion shaded as a wall.");
            RoadHeight = config.Bind("Roads", "Road Height", 0.008f, new ConfigDescription(
                "POM displacement for roads — separate from terrain (road UV density differs and the "
                + "height masks span their full range).",
                new AcceptableValueRange<float>(0f, 0.15f)));

            BlendedMarch = config.Bind("Advanced", "Blended March", true, new ConfigDescription(
                "March ONE surface made of both top ground layers blended by height. Off = the stock "
                + "path: each layer marched separately and the two results cross-faded, which doubles "
                + "or smears the relief at every material transition.", null, advanced));
            PatchCulling = config.Bind("Advanced", "Tessellation Patch Culling", true, new ConfigDescription(
                "Skip tessellating terrain patches outside the view (and each shadow cascade). Pure perf win; "
                + "turn off only to rule it out if ground ever goes missing at the screen edges.", null, advanced));
            Qdm = config.Bind("Advanced", "QDM Traversal", false, new ConfigDescription(
                "PERFORMANCE option: quadtree hierarchical marching — cheaper than the linear march "
                + "but visibly chunkier (point-sampled cells). Leave off for quality; turn on if POM "
                + "cost matters more. Terrain only; needs generated heights.", null, advanced));
            DerivedNormals = config.Bind("Advanced", "Height-Derived Normals", false, new ConfigDescription(
                "EXPERIMENTAL: shade the terrain with normals computed from the marched height field "
                + "instead of the normal maps — self-consistent with the displacement but loses "
                + "fine detail. Most setups look better with this OFF.", null, advanced));
            ReconDump = config.Bind("Advanced", "Recon Dump", false, new ConfigDescription(
                "Log every MicroSplat terrain's shader interface at raid load + dump debug textures.",
                null, advanced));
            DebugView = config.Bind("Advanced", "Debug View", 0, new ConfigDescription(
                "Paints POM internals on the ground. Terrain: 1=ray inputs (R=march strength, G=tangent "
                + "frame health, B=distance fade), 2=surface height the ray hit (white=top), 3=depth "
                + "offset (red=deeper). Roads: 1=vertex colors, 2=blend weights, 3=blended height.",
                new AcceptableValueRange<int>(0, 3), advanced));

            // Params/debug ride global shader constants (instant, no scan). Only the Enabled toggle
            // needs the scene scan (shader swap/restore) — keeps slider drags lag-free.
            config.SettingChanged += (_, e) =>
            {
                PomApplier.ApplyGlobals();
                if (e.ChangedSetting == Enabled || e.ChangedSetting == Mode) PomApplier.ReapplyAll();
                if (e.ChangedSetting == Roads) Plugin.RunRoadApply();
                else if (e.ChangedSetting == RoadMask) Plugin.RunRoadMask();
            };
        }
    }
}
