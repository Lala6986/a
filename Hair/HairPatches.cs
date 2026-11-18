using System;
using ExposureUnnoticed2.Master.Hair;
using ExposureUnnoticed2.Object3D.Player.Scripts;
using ExposureUnnoticed2.Scripts.InGame;
using HarmonyLib;
using MyFirstPlugin.Core;

namespace MyFirstPlugin.Hair;

[HarmonyPatch(typeof(PlayerHairController))]
public class HairPatches
{
    [HarmonyPatch(nameof(PlayerHairController.ChangeHair))]
    [HarmonyPrefix]
    public static void PrefixChangeHair(ref HairType next, out int __state)
    {
        int hairId = (int)next;
        __state = -1;

        HairSpawner.Cleanup();

        if (HairManager.LoadedManifests.ContainsKey(hairId))
        {
            __state = hairId;
            next = HairType.Short;
        }
        else
        {
            HairSavePatches.LastRequestedHair = -1;
        }

        // The game is really showing the placeholder vanilla hair (Short) while a custom hair is
        // equipped. Tell it that, so it deactivates that hair when switching to something else.
        var pcd = GameState.PlayerCustomData;
        if (pcd != null && (int)pcd.CurrentHair >= 1000)
            pcd.CurrentHair = HairType.Short;
    }

    [HarmonyPatch(nameof(PlayerHairController.ChangeHair))]
    [HarmonyPostfix]
    public static void PostfixChangeHair(int __state)
    {
        if (__state <= 0) return;
        if (!HairManager.LoadedManifests.ContainsKey(__state)) return;

        try
        {
            HairSavePatches.LastRequestedHair = __state;
            HairSavePatches.CommitCustomHair(__state);

            if (!HairManager.HairBank.TryGetValue(__state, out var asset))
            {
                Plugin.Log.LogError($"Hair asset {__state} missing from bank.");
                return;
            }

            var facade = PlayerFacade.Instance;
            if (facade == null) { Plugin.Log.LogError("No facade."); return; }

            if (!HairSpawner.Spawn(asset, facade, out var err))
                Plugin.Log.LogError($"Spawn failed: {err}");
            else
                HairColorPatches.ApplyFromSave(__state);
        }
        catch (Exception ex) { Plugin.Log.LogError($"Postfix failed: {ex}"); }
    }

    public static void ReloadCurrentHair()
    {
        if (HairSpawner.Current == null) return;
        string[] parts = HairSpawner.Current.name.Split('_');
        if (parts.Length < 2 || !int.TryParse(parts[1], out int id)) return;
        if (!HairManager.LoadedManifests.ContainsKey(id)) return;

        Plugin.Log.LogInfo($"[HotReload] Reloading hair {id}");
        HairSpawner.Cleanup();
        var facade = PlayerFacade.Instance;
        if (facade == null) return;
        if (HairManager.HairBank.TryGetValue(id, out var asset))
            HairSpawner.Spawn(asset, facade, out _);
    }
}