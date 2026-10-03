using System.Collections.Generic;
using UnityEngine;

namespace POMSix
{
    // Publishes the active directional light's direction (TOD sun by day, moon by night) as
    // _POMSixSunDir for the terrain self-shadow horizon test. Scan once at raid load (FogSix
    // zero-poll pattern); per frame just pick the enabled brightest and set the global.
    public static class SunTracker
    {
        private static readonly List<Light> _directionals = new List<Light>();

        public static void Rescan()
        {
            _directionals.Clear();
            foreach (Light l in Object.FindObjectsOfType<Light>())
                if (l.type == LightType.Directional) _directionals.Add(l);
        }

        public static void UpdateGlobal()
        {
            Light best = null;
            float bi = 0f;
            for (int i = 0; i < _directionals.Count; i++)
            {
                Light l = _directionals[i];
                if (l == null || !l.enabled || !l.gameObject.activeInHierarchy) continue;
                if (l.intensity > bi) { bi = l.intensity; best = l; }
            }
            if (best != null)
                Shader.SetGlobalVector("_POMSixSunDir", -best.transform.forward);
        }
    }
}
