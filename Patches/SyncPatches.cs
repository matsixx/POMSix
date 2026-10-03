using HarmonyLib;
using JBooth.MicroSplat;

namespace POMSix.Patches
{
    // Sync() is the single funnel every terrain material application passes through — first spawn,
    // streamed-in slices, season swaps, quality swaps. Postfixing it re-applies our shader everywhere
    // with no scene bookkeeping. (MicroSplatMeshTerrain = mesh-ground maps, global namespace.)
    [HarmonyPatch(typeof(MicroSplatTerrain), nameof(MicroSplatTerrain.Sync))]
    internal static class TerrainSyncPatch
    {
        private static void Postfix(MicroSplatTerrain __instance) => PomApplier.Apply(__instance);
    }

    [HarmonyPatch(typeof(MicroSplatMeshTerrain), nameof(MicroSplatMeshTerrain.Sync))]
    internal static class MeshTerrainSyncPatch
    {
        private static void Postfix(MicroSplatMeshTerrain __instance) => PomApplier.Apply(__instance);
    }
}
