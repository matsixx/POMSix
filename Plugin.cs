using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace POMSix
{
    [BepInPlugin("com.matsix.pomsix", "POMSix", "1.0.1")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource MyLog;
        public static Plugin Instance;

        public static void RunRoadApply()
        {
            if (Instance != null) Instance.StartCoroutine(RoadPom.ApplyRoutine());
        }
        public static void RunRoadMask()
        {
            if (Instance == null) return;
            RoadPom.RestoreAll();   // whether roads get depth stamps depends on the mask setting
            Instance.StartCoroutine(RoadsThenMask());
        }
        private static IEnumerator RoadsThenMask()
        {
            yield return Instance.StartCoroutine(RoadPom.ApplyRoutine());
            yield return Instance.StartCoroutine(RoadMask.BuildRoutine());
        }

        private void Awake()
        {
            Instance = this;
            MyLog = Logger;
            MyLog.LogInfo("POMSix loaded");
            PomConfig.Bind(Config);
            HeightGen.PublishAmps(); // fixes the global array's size (32) before anything else sets it
            new Harmony("com.matsix.pomsix").PatchAll();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        // Terrain (and Streets' mesh ground) streams in after the scene "loads", so a few passes over
        // the first seconds catch late arrivals — same pattern as FogSix's raid-load scans. The recon
        // dedupes per instance, so repeat passes only log what's new.
        private Coroutine _loadScan;
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_loadScan != null) StopCoroutine(_loadScan);
            _loadScan = StartCoroutine(RaidLoadScans(scene.name));
        }

        // Height-map generation finishes on worker threads; the upload must happen main-thread.
        // Sun direction re-published per frame (TOD moves it through the raid). The main camera
        // position feeds POM's distance fade inside the SHADOW CASTER pass, where the shader's own
        // camera is the light (w = 0 → the shader falls back to its own camera distance).
        private void Update()
        {
            HeightGen.Pump();
            RoadHeights.Pump();
            SunTracker.UpdateGlobal();
            Camera cam = Camera.main;
            Shader.SetGlobalVector("_POMSixMainCamPos", cam != null
                ? new Vector4(cam.transform.position.x, cam.transform.position.y, cam.transform.position.z, 1f)
                : Vector4.zero);
            PomApplier.UpdateCameraGlobals(cam); // tessellation density follows FOV/resolution (ADS zoom, VR)
        }

        private IEnumerator RaidLoadScans(string sceneName)
        {
            // Two passes (2s catches the loaded map, 15s the stragglers) — the old four-pass schedule
            // multiplied the scan cost into a visible raid-start spike. Terrain re-syncs are covered
            // by the Sync postfixes regardless.
            float[] delays = { 2f, 13f };
            foreach (float d in delays)
            {
                yield return new WaitForSeconds(d);
                if (PomConfig.ReconDump.Value)
                {
                    TerrainRecon.Dump(sceneName);
                    TerrainRecon.DumpRoads(sceneName);
                }
                PomApplier.ReapplyAll();                     // cheap: few MicroSplat objects
                yield return StartCoroutine(RoadPom.ApplyRoutine()); // chunked renderer walk
                yield return StartCoroutine(RoadMask.BuildRoutine()); // top-down road coverage for the carve
                SunTracker.Rescan();
            }
            _loadScan = null;
        }
    }
}
