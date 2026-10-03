using BepInEx;
using BepInEx.Logging;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace POMSix
{
    [BepInPlugin("com.matsix.pomsix", "POMSix", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource MyLog;

        private void Awake()
        {
            MyLog = Logger;
            MyLog.LogInfo("POMSix loaded (recon build)");
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

        private IEnumerator RaidLoadScans(string sceneName)
        {
            float[] delays = { 2f, 6f, 15f, 30f };
            foreach (float d in delays)
            {
                yield return new WaitForSeconds(d);
                TerrainRecon.Dump(sceneName);
            }
            _loadScan = null;
        }
    }
}
