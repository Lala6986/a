using System;
using System.IO;
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
            }

            if (Input.GetKeyDown(KeyCode.F5))
            {
                Plugin.Log.LogWarning("=== HOT RELOADING HAIR ===");
                HairManager.LoadCustomHairManifests();
                HairPatches.ReloadCurrentHair();

                PaintManager.LoadCustomPaints();
                PaintMasterPatches.Refresh();
            }

            if (Input.GetKeyDown(KeyCode.F9))
            {
                Plugin.Log.LogWarning("=== F9 PRESSED: RUNNING REFERENCE EXPORT ===");
                ReferenceExporter.RunExportNow();
            }

            if (Input.GetKeyDown(KeyCode.F6))
            {
                Plugin.Log.LogWarning("=== F6 PRESSED: RESET CUSTOM PAINT PURCHASES ===");
                PaintManager.DevFreeMode();
            }

            if (Input.GetKeyDown(KeyCode.F7))
            {
                Plugin.Log.LogWarning("=== F7 PRESSED: PAINT MATERIAL / RENDERER DUMP ===");
                PaintManager.DevDumpMaterials();
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                Plugin.Log.LogWarning("=== F8 PRESSED: DUMP VANILLA BODY-PAINT TEXTURES ===");
                PaintManager.DumpVanillaPaintTextures();
            }

            if (Input.GetKeyDown(KeyCode.F10))
            {
                Plugin.Log.LogWarning("=== F10 PRESSED: FORCE VANILLA PAINT (DEBUG) ===");
                PaintManager.DevForceVanilla();
            }
        }
    }
}