using System;
using BepInEx;
using UnityEngine;
using MyFirstPlugin.Hair;
using MyFirstPlugin.Paint;

namespace MyFirstPlugin.Core
{
    public class DevReloaderBehaviour : MonoBehaviour
    {
        public DevReloaderBehaviour(IntPtr ptr) : base(ptr) { }

        private float _nextEnforce;

        public void Update()
        {
            if (Time.unscaledTime >= _nextEnforce)
            {
                _nextEnforce = Time.unscaledTime + 0.25f;
                HairSpawner.EnforceHidden();
                EffectManager.Enforce();
            }

            if (Input.GetKeyDown(KeyCode.F5))
            {
                Plugin.Log.LogWarning("=== F5: HOT RELOAD ===");
                HairManager.LoadCustomHairManifests();
                HairPatches.ReloadCurrentHair();
                PaintManager.LoadCustomPaints();
                PaintMasterPatches.Refresh();
                EffectManager.ForceRebuild();
            }

            if (Input.GetKeyDown(KeyCode.F9))
            {
                Plugin.Log.LogWarning("=== F9: REFERENCE EXPORT ===");
                ReferenceExporter.RunExportNow();
            }

            if (Input.GetKeyDown(KeyCode.F11))
            {
                Plugin.Log.LogWarning("=== F11: DUMP NEARBY RENDERERS (5m) ===");
                EffectManager.DumpNearby(5f);
            }
        }
    }
}