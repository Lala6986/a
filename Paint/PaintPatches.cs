using System;
using ExposureUnnoticed2.Master.BodyPaint;
using ExposureUnnoticed2.Object3D.Player.Scripts;
using ExposureUnnoticed2.Scripts.InGame;
using HarmonyLib;
using MyFirstPlugin.Paint;
using UnityEngine;

namespace MyFirstPlugin.Paint;

/// <summary>Adds our custom paints to MBodyPaint.dict (UI + shop enumerate this).</summary>
[HarmonyPatch(typeof(MBodyPaint), "CheckInitialize")]
public class PaintMasterPatches
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        try { PaintManager.InjectRecords(); }
        catch (Exception ex) { Plugin.Log.LogError($"[Paint] Master postfix failed: {ex}"); }
    }

    /// <summary>Re-inject after a hot reload (the dict already exists then).</summary>
    internal static void Refresh()
    {
        try
        {
            PaintManager.InjectRecords();
            PaintManager.RefreshUi();
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[Paint] Refresh failed: {ex.Message}"); }
    }
}

/// <summary>Maps a custom BodyPaintType to our material (clone of the vanilla base material).</summary>
[HarmonyPatch(typeof(PlayerBodyMaterialController), "TransToMaterialAsset")]
public class PaintMaterialPatches
{
    [HarmonyPrefix]
    public static bool Prefix(PlayerBodyMaterialController __instance, BodyPaintType type, ref Material __result)
    {
        try
        {
            int id = (int)type;

            // If the game asks for a vanilla type, check if we have a proxy pending
            if (id < PaintManager.CustomIdStart)
            {
                int pending = PaintManager.PendingCustomId;
                if (pending == 0) return true; // No proxy pending, let the game do its thing

                PaintManager.PendingCustomId = 0; // Consume it

                var pendingAsset = PaintManager.Get(pending);
                if (pendingAsset == null) return true;

                var pendingMat = PaintManager.GetOrCreateMaterial(pendingAsset, __instance);
                if (pendingMat == null) return true;

                __result = pendingMat;
                Plugin.Log.LogInfo($"[Paint] Proxy: Replaced {type} with '{pendingMat.name}'");
                return false; // Skip original method, use our material
            }

            // Direct custom ID lookup (fallback)
            var asset = PaintManager.Get(id);
            if (asset == null) return true;

            var mat = PaintManager.GetOrCreateMaterial(asset, __instance);
            if (mat == null) return true;

            __result = mat;
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[Paint] Material prefix failed for {(int)type}: {ex}");
            return true;
        }
    }
}

/// <summary>
/// ApplyFromGameState only iterates the vanilla BodyPaintType values, so custom 1000+ types
/// are never turned into a material. While a custom paint is active, temporarily mark
/// PaintManager.ProxyType as active so the game itself runs TransToMaterialAsset for it,
/// and let PaintMaterialPatches return our material for that one call.
/// </summary>
[HarmonyPatch(typeof(PlayerBodyMaterialController), "ApplyFromGameState")]
public class PaintApplyDirectPatches
{
    [HarmonyPostfix]
    public static void Postfix(PlayerBodyMaterialController __instance)
    {
        try
        {
            // We use direct injection because the game skips updates if it thinks nothing changed.
            PaintManager.SyncBodyPaint(__instance);
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[Paint] Apply direct postfix: {ex.Message}"); }
    }
}

/// <summary>Custom paints are mod content: always "owned", so the checkmark/shop works.</summary>
[HarmonyPatch(typeof(GameStateFacade), nameof(GameStateFacade.IsBringBodyPaint))]
public class PaintOwnershipPatches
{
    [HarmonyPostfix]
    public static void Postfix(BodyPaintType type, ref bool __result)
    {
        if ((int)type >= PaintManager.CustomIdStart) __result = true;
    }
}