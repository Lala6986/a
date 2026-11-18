using System;
using ExposureUnnoticed2.Master.Hair;
using ExposureUnnoticed2.Scripts.InGame;
using HarmonyLib;

namespace MyFirstPlugin.Hair;

[HarmonyPatch(typeof(GameStateData))]
public class GameStatePatches
{
    [HarmonyPatch(MethodType.Constructor)]
    [HarmonyPostfix]
    public static void PostfixGameStateDataCtor(GameStateData __instance)
    {
        try
        {
            if (__instance == null || __instance.HairTypeList == null) return;

            // 1. SANITIZE: Remove orphaned IDs (mods that were deleted)
            for (int i = __instance.HairTypeList.Count - 1; i >= 0; i--)
            {
                HairType id = __instance.HairTypeList[i];

                // If the ID is not in the game's master dict (meaning mod is missing)
                if (MHair.dict != null && !MHair.dict.ContainsKey(id))
                {
                    Plugin.Log.LogWarning($"Removing orphaned hair ID {id} from save file (mod deleted).");
                    __instance.HairTypeList.RemoveAt(i);
                }
            }

            // 2. INJECT: Add back our valid custom hairs
            foreach (int id in HairManager.LoadedManifests.Keys)
            {
                HairType customId = (HairType)id;
                if (!__instance.HairTypeList.Contains(customId))
                {
                    __instance.HairTypeList.Add(customId);
                    Plugin.Log.LogInfo($"Unlocked custom hair ID {id} in save data.");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Save sanitizer failed: {ex}");
        }
    }
}