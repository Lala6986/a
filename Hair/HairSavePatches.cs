using System;
using ExposureUnnoticed2.Master.Hair;
using ExposureUnnoticed2.ObjectUI.ClosetMenu.CustomizeMenu;
using ExposureUnnoticed2.Scripts.InGame;
using HarmonyLib;
using UnityEngine;

namespace MyFirstPlugin.Hair;

public static class HairSavePatches
{
    /// <summary>Tracks the last custom hair ID the player requested, so Loadout.save keeps it.</summary>
    public static int LastRequestedHair = -1;

    /// <summary>Make sure every custom hair has a customize entry, and drop IDs of deleted mods.</summary>
    public static void EnsureCustomHairData()
    {
        try
        {
            var pcd = GameState.PlayerCustomData;
            if (pcd == null) return;

            var dict = pcd.HairCustomizeDataDict;
            if (dict != null && MHair.dict != null)
            {
                foreach (int id in HairManager.LoadedManifests.Keys)
                {
                    var t = (HairType)id;
                    if (dict.ContainsKey(t)) continue;
                    if (!MHair.dict.ContainsKey(t)) continue;

                    var def = MHair.dict[t].DefaultValue;
                    if (def == null) continue;
                    dict[t] = new MHairRecord.HairCustomizeData(def);
                }

                // Clamp pattern ids to the files that actually exist (a pattern_N.png may be removed).
                foreach (int id in HairManager.LoadedManifests.Keys)
                {
                    var t = (HairType)id;
                    if (!dict.TryGetValue(t, out var d)) continue;
                    if (!HairManager.HairBank.TryGetValue(id, out var asset)) continue;

                    int max = Mathf.Max(0, HairTextureLoader.GetPatternCount(asset) - 1);
                    if (d.TextureId < 0 || d.TextureId > max)
                    {
                        Plugin.Log.LogWarning($"[Hair] Saved pattern {d.TextureId} for hair {id} is unavailable; resetting to 0.");
                        d.TextureId = 0;
                    }
                }
            }

            if ((int)pcd.CurrentHair >= 1000 && MHair.dict != null && !MHair.dict.ContainsKey(pcd.CurrentHair))
            {
                Plugin.Log.LogWarning($"Saved hair {(int)pcd.CurrentHair} no longer exists, resetting to Short.");
                pcd.CurrentHair = HairType.Short;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"EnsureCustomHairData failed: {ex}");
        }
    }

    /// <summary>Called right after PlayerHairController.ChangeHair ran with the swapped vanilla ID.</summary>
    public static void CommitCustomHair(int id)
    {
        try
        {
            var pcd = GameState.PlayerCustomData;
            if (pcd == null) return;

            EnsureCustomHairData();
            pcd.CurrentHair = (HairType)id;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"CommitCustomHair failed: {ex}");
        }
    }

    // Before the closet builds its hair UI, make sure the data exists for custom hairs
    [HarmonyPatch(typeof(CustomizeMenuView), nameof(CustomizeMenuView.Show))]
    [HarmonyPrefix]
    public static void ClosetShowPre() => EnsureCustomHairData();

    // Force the loadout save to use our custom hair ID
    [HarmonyPatch(typeof(LoadoutCustomizeData), nameof(LoadoutCustomizeData.SaveCurrentCustomize))]
    [HarmonyPostfix]
    public static void LoadoutSaveFix(LoadoutCustomizeData __instance)
    {
        if (LastRequestedHair < 0) return;
        if (!HairManager.LoadedManifests.ContainsKey(LastRequestedHair)) return;

        __instance.CurrentHair = (HairType)LastRequestedHair;
        if (__instance.HairCustomize != null)
            __instance.HairCustomize.HairType = (HairType)LastRequestedHair;
    }
}