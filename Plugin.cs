using System.IO;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MyFirstPlugin.Core;
using MyFirstPlugin.Hair;
using MyFirstPlugin.Paint;

namespace MyFirstPlugin;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log;

    public override void Load()
    {
        Log = base.Log;
        Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} loaded. START");

        HairManager.LoadCustomHairManifests();
        PaintManager.LoadCustomPaints();

        Harmony.CreateAndPatchAll(typeof(HairPatches));
        Harmony.CreateAndPatchAll(typeof(HairMasterPatches));
        Harmony.CreateAndPatchAll(typeof(GameStatePatches));
        Harmony.CreateAndPatchAll(typeof(HairSavePatches));
        Harmony.CreateAndPatchAll(typeof(HairColorPatches));
        Harmony.CreateAndPatchAll(typeof(ReferenceExporter));
        Harmony.CreateAndPatchAll(typeof(PaintMasterPatches));
        Harmony.CreateAndPatchAll(typeof(PaintMaterialPatches));
        Harmony.CreateAndPatchAll(typeof(PaintOwnershipPatches));
        Harmony.CreateAndPatchAll(typeof(PaintApplyDirectPatches));
        Harmony.CreateAndPatchAll(typeof(PaintDbgSetActive));
        Harmony.CreateAndPatchAll(typeof(PaintDbgIsBring));
        Harmony.CreateAndPatchAll(typeof(PaintDbgIsEquip));
        Harmony.CreateAndPatchAll(typeof(PaintDbgSetup));
        Harmony.CreateAndPatchAll(typeof(PaintDbgApply));
        Harmony.CreateAndPatchAll(typeof(PaintDbgTrans));

        // F5 hot reloader
        AddComponent<DevReloaderBehaviour>();

        // Fresh probe file this session
        try { File.Delete(Path.Combine(Paths.BepInExRootPath, "hair_adjuster_probe.txt")); }
        catch { }

        Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} loaded. END");
    }
}