using System;
using System.Collections.Generic;
using ExposureUnnoticed2.Master.BodyPaint;
using ExposureUnnoticed2.Object3D.Player.Scripts;
using ExposureUnnoticed2.Scripts.InGame;
using HarmonyLib;
using MyFirstPlugin.Paint;
using UnityEngine;

namespace MyFirstPlugin.Paint;

/// <summary>Temporary diagnostics to find where the vanilla paint flow drops custom IDs.</summary>
internal static class PaintDebug
{
    public static readonly HashSet<string> Logged = new();

    public static void Once(string key, string message)
    {
        if (Logged.Add(key)) Plugin.Log.LogInfo(message);
    }
}

[HarmonyPatch(typeof(GameStateFacade), nameof(GameStateFacade.SetActiveBodyPaint))]
public class PaintDbgSetActive
{
    [HarmonyPostfix]
    public static void Postfix(BodyPaintType type, bool isActive)
    {
        try
        {
            var pcd = GameState.PlayerCustomData;
            var dict = pcd != null ? pcd.BodyPaintTypeDict : null;
            bool has = dict != null && dict.ContainsKey(type);
            bool val = has && dict[type];
            Plugin.Log.LogInfo($"[PaintDbg] SetActiveBodyPaint type={(int)type} active={isActive} dictHas={has} dictVal={val}");
            PaintManager.DumpPaintState($"after SetActive {(int)type}");
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[PaintDbg] SetActive postfix: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(GameStateFacade), nameof(GameStateFacade.IsBringBodyPaint))]
public class PaintDbgIsBring
{
    [HarmonyPostfix]
    public static void Postfix(BodyPaintType type, ref bool __result)
    {
        try
        {
            if ((int)type < PaintManager.CustomIdStart) return;
            PaintDebug.Once($"bring{(int)type}", $"[PaintDbg] IsBringBodyPaint type={(int)type} -> {__result}");
        }
        catch { }
    }
}

[HarmonyPatch(typeof(GameStateFacade), nameof(GameStateFacade.IsEquipBodyPaint))]
public class PaintDbgIsEquip
{
    [HarmonyPostfix]
    public static void Postfix(BodyPaintType type, ref bool __result)
    {
        try
        {
            if ((int)type < PaintManager.CustomIdStart) return;
            PaintDebug.Once($"equip{(int)type}:{__result}", $"[PaintDbg] IsEquipBodyPaint type={(int)type} -> {__result}");
        }
        catch { }
    }
}

[HarmonyPatch(typeof(PlayerBodyMaterialController), "SetupBodyPaintFromGameState")]
public class PaintDbgSetup
{
    [HarmonyPrefix]
    public static void Prefix() => Plugin.Log.LogInfo("[PaintDbg] SetupBodyPaintFromGameState <<");
    [HarmonyPostfix]
    public static void Postfix() => Plugin.Log.LogInfo("[PaintDbg] SetupBodyPaintFromGameState >>");
}

[HarmonyPatch(typeof(PlayerBodyMaterialController), "ApplyFromGameState")]
public class PaintDbgApply
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        try
        {
            Plugin.Log.LogInfo("[PaintDbg] ApplyFromGameState <<");
            PaintManager.DumpPaintState("apply");
        }
        catch { }
    }

    [HarmonyPostfix]
    public static void Postfix() => Plugin.Log.LogInfo("[PaintDbg] ApplyFromGameState >>");
}

[HarmonyPatch(typeof(PlayerBodyMaterialController), "TransToMaterialAsset")]
public class PaintDbgTrans
{
    [HarmonyPostfix]
    public static void Postfix(BodyPaintType type, ref Material __result)
    {
        try
        {
            PaintDebug.Once($"trans{(int)type}", $"[PaintDbg] TransToMaterialAsset type={(int)type} -> {( __result != null ? __result.name : "NULL")}");
        }
        catch { }
    }
}
