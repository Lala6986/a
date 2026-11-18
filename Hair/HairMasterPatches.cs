using System;
using System.IO;
using System.Text;
using BepInEx;
using ExposureUnnoticed2.Master.Hair;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace MyFirstPlugin.Hair;

[HarmonyPatch(typeof(MHair), nameof(MHair.CheckInitialize))]
public class HairMasterPatches
{
    private static bool _hasInjected = false;

    /// <summary>Called by HairManager.LoadCustomHairManifests() so hot-reload re-injects.</summary>
    internal static void ResetInjected() => _hasInjected = false;

    [HarmonyPostfix]
    public static void PostfixCheckInitialize()
    {
        if (_hasInjected) return;
        Plugin.Log.LogInfo("Injecting custom hair records into MHair.dict...");

        DumpVanillaHairTexturesIfRequested();

        if (MHair.dict == null)
        {
            Plugin.Log.LogError("MHair.dict is NULL during init.");
            return;
        }

        foreach (var kvp in HairManager.LoadedManifests)
        {
            try
            {
                HairType customId = (HairType)kvp.Key;
                HairManifest manifest = kvp.Value;

                if (MHair.dict.ContainsKey(customId))
                {
                    Plugin.Log.LogWarning($"Custom hair ID {kvp.Key} already in dict, skipping.");
                    continue;
                }

                // Clone any vanilla record so we inherit its UI sprites/icons
                MHairRecord record = null;
                foreach (var key in MHair.dict.Keys)
                {
                    if ((int)key < 1000)
                    {
                        record = UnityEngine.Object.Instantiate(MHair.dict[key]);
                        break;
                    }
                }

                if (record == null)
                    record = ScriptableObject.CreateInstance<MHairRecord>();

                record.Type = customId;
                record.NameKey = manifest.name;
                record.DescriptionKey = manifest.description;
                record.Price = manifest.price;
                record.DefaultValue = new MHairRecord.HairCustomizeData(customId)
                {
                    TextureId = 0,
                    R = 255,
                    G = 255,
                    B = 255,
                    ShadeColor = 0
                };

                // ═══ COLOR PATTERN ═══
                // Vanilla stores its "color pattern" textures in MHairRecord.HairTextures,
                // indexed by HairCustomizeData.TextureId. The closet slider max is derived
                // from this array's Length. Replace the cloned vanilla list with ours.
                ApplyHairTextures(record, kvp.Key);

                MHair.dict.Add(customId, record);
                Plugin.Log.LogInfo($"Injected hair: {manifest.name} (ID {kvp.Key})" +
                    $" patterns={(record.HairTextures != null ? record.HairTextures.Length : 0)}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to inject hair ID {kvp.Key}: {ex}");
            }
        }

        _hasInjected = true;
        Plugin.Log.LogInfo($"MHair.dict injection complete. Total custom: {HairManager.LoadedManifests.Count}");
    }

    /// <summary>
    /// Refreshes HairTextures on already-injected records (used by F5 hot reload).
    /// Safe to call repeatedly; only touches our own custom-hair records.
    /// </summary>
    internal static void RefreshCustomRecords()
    {
        if (MHair.dict == null) return;

        foreach (var kvp in HairManager.LoadedManifests)
        {
            try
            {
                if (!MHair.dict.TryGetValue((HairType)kvp.Key, out var record)) continue;
                ApplyHairTextures(record, kvp.Key);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Hair] RefreshCustomRecords failed for {kvp.Key}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// One-shot diagnostic: logs every vanilla hair record's colour-pattern texture names and
    /// saves the ponytail set (the record containing "white"+"blue") to `BepInEx/hairtextures/`.
    /// Runs automatically once (marker file), or again if `dump_hairtextures.flag` exists.
    /// </summary>
    private static void DumpVanillaHairTexturesIfRequested()
    {
        try
        {
            string dir = Path.Combine(Paths.BepInExRootPath, "hairtextures");
            string flag = Path.Combine(Paths.BepInExRootPath, "dump_hairtextures.flag");
            string marker = Path.Combine(dir, ".done");
            bool forced = File.Exists(flag);
            if (!forced && File.Exists(marker)) return;
            if (forced) File.Delete(flag);
            Directory.CreateDirectory(dir);

            MHairRecord pony = null;
            foreach (var kvp in MHair.dict)
            {
                if ((int)kvp.Key >= 1000) continue;
                var rec = kvp.Value;
                if (rec == null) continue;

                var arr = rec.HairTextures;
                int n = arr != null ? arr.Length : 0;
                var names = new StringBuilder();
                bool hasWhite = false, hasBlue = false;
                for (int i = 0; i < n; i++)
                {
                    var t = arr[i];
                    names.Append(' ').Append(i).Append('=').Append(t != null ? t.name : "null");
                    if (t != null)
                    {
                        if (t.name == "white") hasWhite = true;
                        if (t.name == "blue") hasBlue = true;
                    }
                }
                Plugin.Log.LogInfo($"[HairTexDump] {(int)kvp.Key} '{rec.NameKey}' count={n}:{names}");
                if (hasWhite && hasBlue && pony == null) pony = rec;
            }

            if (pony != null)
            {
                var arr = pony.HairTextures;
                for (int i = 0; i < arr.Length; i++)
                {
                    var t = arr[i];
                    if (t == null) continue;
                    try
                    {
                        ReferenceExporter.SaveTexture(t, Path.Combine(dir, $"{i}_{Sanitize(t.name)}.png"));
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"[HairTexDump] save {t.name} failed: {ex.Message}");
                    }
                }
                Plugin.Log.LogInfo($"[HairTexDump] saved ponytail pattern set -> {dir}");
            }
            else Plugin.Log.LogWarning("[HairTexDump] ponytail record (white+blue) not found");

            File.WriteAllText(marker, DateTime.Now.ToString("u"));
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[HairTexDump] failed: {ex}");
        }
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unnamed";
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private static void ApplyHairTextures(MHairRecord record, int hairId)
    {
        if (record == null) return;

        Texture[] textures = null;
        if (HairManager.HairBank.TryGetValue(hairId, out var asset))
            textures = HairTextureLoader.BuildOrderedTextureArray(asset);

        if (textures != null && textures.Length > 0)
            record.HairTextures = new Il2CppReferenceArray<Texture>(textures);
        else
            record.HairTextures = new Il2CppReferenceArray<Texture>(0L);
    }
}
