using System;
using System.Collections.Generic;
using ExposureUnnoticed2.Master.Hair;
using ExposureUnnoticed2.ObjectUI.ClosetMenu.CustomizeMenu;
using ExposureUnnoticed2.Scripts.InGame;
using HarmonyLib;
using UnityEngine;

namespace MyFirstPlugin.Hair;

public static class HairColorPatches
{
    // Log a missing/out-of-range pattern only once per (hair, id) so the console isn't spammed.
    private static readonly HashSet<string> _warnedMissingPattern = new();

    // One-shot diagnostics so we can see exactly what the game asked for.
    private static readonly HashSet<string> _loggedApplied = new();

    public static void Apply(GameObject hair, MHairRecord.HairCustomizeData d)
    {
        if (hair == null || d == null) return;

        TryResolveAsset(hair, d, out var asset);

        var baseColor = new Color(
            Mathf.Clamp01(d.R / 255f),
            Mathf.Clamp01(d.G / 255f),
            Mathf.Clamp01(d.B / 255f),
            1f);

        // ── COLOR PATTERN ──
        // TextureId selects the whole _MainTex / _BaseMap (not an overlay).
        // Clamp to the range of textures we actually supplied and fall back to base.
        Texture2D mainTex = null;
        bool bakedPattern = false;
        if (asset != null)
        {
            int count = HairTextureLoader.GetPatternCount(asset);
            int textureId = d.TextureId;

            if (textureId < 0 || (count > 0 && textureId >= count))
            {
                WarnMissingPattern(asset.Id, textureId, count, "out of range");
                textureId = 0;
            }

            if (textureId <= 0)
            {
                mainTex = asset.BaseTexture ?? FirstPattern(asset);
            }
            else if (asset.PatternTextures != null &&
                     asset.PatternTextures.TryGetValue(textureId, out var pat) && pat != null)
            {
                mainTex = pat;
            }
            else
            {
                WarnMissingPattern(asset.Id, textureId, count, "file missing");
                mainTex = asset.BaseTexture ?? FirstPattern(asset);
                textureId = 0;
            }

            // Keep the saved/UI value in sync with what is actually displayed.
            if (d.TextureId != textureId)
            {
                try { d.TextureId = textureId; } catch { }
            }

            // pattern_1 and up are complete, pre-coloured textures.
            bakedPattern = textureId > 0 && mainTex != null;
        }

        // A baked pattern already carries its own colour. Multiplying it by the
        // RGB sliders again is what made the hair look darker/more saturated
        // than the reference, so only the tintable base (pattern_0) takes RGB.
        Color tint = bakedPattern ? Color.white : baseColor;
        var shadeColor = ComputeShadeColor(tint, d.ShadeColor);

        LogApplied(asset != null ? asset.Id : (int)d.HairType, d.TextureId, d, bakedPattern);

        foreach (var r in hair.GetComponentsInChildren<Renderer>(true))
        {
            var m = r.sharedMaterial;
            if (m == null) continue;
            HairProfileIO.ApplyOnce(m, d);

            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", tint);
            else if (m.HasProperty("_Color")) m.SetColor("_Color", tint);

            if (mainTex != null)
            {
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", mainTex);
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", mainTex);
            }

            if (m.HasProperty("_1st_ShadeColor"))
                m.SetColor("_1st_ShadeColor", shadeColor);
        }
    }

    public static Color ComputeShadeColor(Color baseColor, int shadeColor)
    {
        // Vanilla neutral = baseColor * 0.9, alpha forced to 1 (old code scaled alpha too)
        Color neutral = new Color(baseColor.r * 0.9f, baseColor.g * 0.9f, baseColor.b * 0.9f, 1f);
        if (shadeColor == 0) return neutral;

        float t = Mathf.Clamp01(Mathf.Abs(shadeColor) / 100f);

        // Endpoints from the vanilla dumps (yellow base): -100 -> (0.9, 0, 0), +100 -> (0, 0.212, 0.9)
        Color target = shadeColor < 0
            ? new Color(0.9f, 0.0f, 0.0f, 1f)
            : new Color(0.0f, 0.212f, 0.9f, 1f);

        return Color.Lerp(neutral, target, t);
    }

    public static void ApplyFromSave(int hairId)
    {
        try
        {
            var dict = GameState.PlayerCustomData?.HairCustomizeDataDict;
            var key = (HairType)hairId;
            if (dict == null || !dict.ContainsKey(key)) return;
            Apply(HairSpawner.Current, dict[key]);
        }
        catch (Exception ex) { Plugin.Log.LogError($"ApplyFromSave failed: {ex}"); }
    }

    private static void OnHairUi(CustomizeMenuView v)
    {
        try
        {
            var d = v.currentHairCustomizeData;
            if (d == null) return;
            if (!HairManager.LoadedManifests.ContainsKey((int)d.HairType)) return;
            Apply(HairSpawner.Current, d);
        }
        catch { }
    }

    // ═══ COLOR PATTERN SLIDER RANGE ═══
    // Vanilla sets hairTextureId's max from MHairRecord.HairTextures.Length.
    // We already replaced that array in HairMasterPatches; this postfix is a
    // safety net that forces the correct max for custom hairs.
    [HarmonyPatch(typeof(CustomizeMenuView), nameof(CustomizeMenuView.OnChangeHairType))]
    [HarmonyPostfix]
    public static void PatchHairPatternSliderRange(CustomizeMenuView __instance)
    {
        try
        {
            var d = __instance.currentHairCustomizeData;
            if (d == null) return;

            int id = (int)d.HairType;
            if (id < 1000 || !HairManager.HairBank.TryGetValue(id, out var asset)) return;

            int count = HairTextureLoader.GetPatternCount(asset);
            int max = Mathf.Max(0, count - 1);

            if (__instance.hairTextureId != null)
                __instance.hairTextureId.SetMax(max);

            if (d.TextureId < 0 || d.TextureId > max)
                d.TextureId = 0;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Hair] Pattern slider range patch failed: {ex.Message}");
        }
    }

    [HarmonyPatch(typeof(CustomizeMenuView), nameof(CustomizeMenuView._OnChangeHairType_b__109_0))]
    [HarmonyPostfix] public static void P0(CustomizeMenuView __instance) => OnHairUi(__instance);
    [HarmonyPatch(typeof(CustomizeMenuView), nameof(CustomizeMenuView._OnChangeHairType_b__109_1))]
    [HarmonyPostfix] public static void P1(CustomizeMenuView __instance) => OnHairUi(__instance);
    [HarmonyPatch(typeof(CustomizeMenuView), nameof(CustomizeMenuView._OnChangeHairType_b__109_2))]
    [HarmonyPostfix] public static void P2(CustomizeMenuView __instance) => OnHairUi(__instance);
    [HarmonyPatch(typeof(CustomizeMenuView), nameof(CustomizeMenuView._OnChangeHairType_b__109_3))]
    [HarmonyPostfix] public static void P3(CustomizeMenuView __instance) => OnHairUi(__instance);
    [HarmonyPatch(typeof(CustomizeMenuView), nameof(CustomizeMenuView._OnChangeHairType_b__109_4))]
    [HarmonyPostfix] public static void P4(CustomizeMenuView __instance) => OnHairUi(__instance);
    [HarmonyPatch(typeof(CustomizeMenuView), nameof(CustomizeMenuView._OnChangeHairType_b__109_5))]
    [HarmonyPostfix] public static void P5(CustomizeMenuView __instance) => OnHairUi(__instance);

    private static Texture2D FirstPattern(HairAsset asset)
    {
        if (asset?.PatternTextures == null) return null;
        foreach (var kv in asset.PatternTextures)
            if (kv.Value != null) return kv.Value;
        return null;
    }

    private static void WarnMissingPattern(int hairId, int requested, int available, string reason)
    {
        string key = $"{hairId}:{requested}";
        if (!_warnedMissingPattern.Add(key)) return;
        Plugin.Log.LogWarning(
            $"[Hair] Pattern {requested} for hair {hairId} {reason} (available 0..{Mathf.Max(0, available - 1)}); using base.");
    }

    // Logs the tint decision once per distinct state so pattern mismatches can
    // be diagnosed from LogOutput.log without spamming every frame.
    private static void LogApplied(int hairId, int textureId, MHairRecord.HairCustomizeData d, bool baked)
    {
        string key = $"{hairId}:{textureId}:{d.R}:{d.G}:{d.B}:{d.ShadeColor}:{baked}";
        if (!_loggedApplied.Add(key)) return;
        Plugin.Log.LogInfo(baked
            ? $"[Hair] Color hair={hairId} pattern={textureId} (baked colour, RGB {d.R}/{d.G}/{d.B} ignored) shade={d.ShadeColor}"
            : $"[Hair] Color hair={hairId} pattern={textureId} (RGB tint {d.R}/{d.G}/{d.B}) shade={d.ShadeColor}");
    }

    private static bool TryResolveAsset(GameObject hair, MHairRecord.HairCustomizeData d, out HairAsset asset)
    {
        asset = null;
        int id = (int)d.HairType;
        if (id >= 1000 && HairManager.HairBank.TryGetValue(id, out asset)) return true;
        if (hair != null)
        {
            var parts = hair.name.Split('_');
            if (parts.Length >= 2 && int.TryParse(parts[1], out int nid) &&
                HairManager.HairBank.TryGetValue(nid, out asset)) return true;
        }
        return false;
    }
}
