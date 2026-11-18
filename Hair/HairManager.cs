using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BepInEx;
using ExposureUnnoticed2.Master.Hair;
using MyFirstPlugin.Core;

namespace MyFirstPlugin.Hair;

public static class HairManager
{
    public static Dictionary<int, HairManifest> LoadedManifests = new();
    public static Dictionary<int, string> LoadedGlbPaths = new();
    public static Dictionary<int, HairAsset> HairBank = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void LoadCustomHairManifests()
    {
        // Free old textures + materials before wiping the bank (avoids leak on F5 reload).
        foreach (var old in HairBank.Values)
        {
            try
            {
                if (old.BaseMaterial != null) UnityEngine.Object.Destroy(old.BaseMaterial);
                HairTextureLoader.CleanupAsset(old);
            }
            catch { }
        }

        LoadedManifests.Clear();
        LoadedGlbPaths.Clear();
        HairBank.Clear();

        string customHairDir = Path.Combine(Paths.PluginPath, "MyFirstPlugin", "CustomHairs");
        if (!Directory.Exists(customHairDir))
        {
            Directory.CreateDirectory(customHairDir);
            Plugin.Log.LogInfo($"Created custom hair directory: {customHairDir}");
            return;
        }

        int nextAvailableId = GetNextAvailableId();

        foreach (string dir in Directory.GetDirectories(customHairDir))
        {
            string jsonPath = Path.Combine(dir, "hair.json");

            // ═══ AUTO-GENERATION LOGIC ═══
            if (!File.Exists(jsonPath))
            {
                var glbs = Directory.GetFiles(dir, "*.glb");
                if (glbs.Length == 0) continue;

                string glbPath = glbs[0];
                string glbFileName = Path.GetFileName(glbPath);
                string folderName = new DirectoryInfo(dir).Name;

                Plugin.Log.LogInfo($"[Hair] Auto-generating hair.json for '{folderName}'...");

                var autoManifest = new HairManifest
                {
                    name = folderName,
                    description = "Auto-generated from GLB. Edit hair.json to customize.",
                    glbFileName = glbFileName,
                    price = 150,
                };

                try
                {
                    byte[] glbBytes = File.ReadAllBytes(glbPath);
                    var dummyAsset = GlbLoader.Parse(glbBytes, glbPath, autoManifest);
                    if (dummyAsset != null && dummyAsset.HasSkeleton)
                        autoManifest.Dynamic = AutoPhysicsGenerator.Generate(dummyAsset);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Hair] Could not auto-parse {glbFileName} for physics: {ex.Message}");
                }

                try
                {
                    string rawJson = JsonSerializer.Serialize(autoManifest, _jsonOptions);
                    string cleanJson = FormatJsonCleanly(rawJson);
                    File.WriteAllText(jsonPath, cleanJson);
                    Plugin.Log.LogInfo($"[Hair] Successfully wrote auto-generated hair.json.");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[Hair] Failed to save auto-generated JSON: {ex.Message}");
                }
            }

            try
            {
                string jsonContent = File.ReadAllText(jsonPath);
                HairManifest manifest = JsonSerializer.Deserialize<HairManifest>(jsonContent,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (manifest == null) continue;

                if (manifest.id > 0 && manifest.id < 1000)
                {
                    Plugin.Log.LogWarning($"Hair ID {manifest.id} is in vanilla range, reassigning.");
                    manifest.id = 0;
                }

                if (manifest.id <= 0)
                {
                    manifest.id = nextAvailableId++;
                    SaveManifestId(jsonPath, manifest);
                }
                else if (manifest.id >= nextAvailableId)
                {
                    nextAvailableId = manifest.id + 1;
                }

                if (LoadedManifests.ContainsKey(manifest.id))
                {
                    Plugin.Log.LogError($"Duplicate hair ID {manifest.id} in {dir}");
                    continue;
                }

                LoadedManifests[manifest.id] = manifest;
                Plugin.Log.LogInfo($"Hair manifest: {manifest.name} (ID: {manifest.id})");

                string glbPath = Path.Combine(dir, manifest.glbFileName);
                if (File.Exists(glbPath))
                {
                    LoadedGlbPaths[manifest.id] = glbPath;

                    try
                    {
                        byte[] glbBytes = File.ReadAllBytes(glbPath);
                        var asset = GlbLoader.Parse(glbBytes, glbPath, manifest);
                        if (asset != null)
                        {
                            HairBank[manifest.id] = asset;

                            // ═══ CUSTOM TEXTURE FOLDER ═══
                            HairTextureLoader.LoadTexturesForAsset(asset, glbPath);

                            Plugin.Log.LogInfo(
                                $"  Parsed: {(asset.HasSkeleton ? "skinned" : "rigid")}, {asset.Mesh.vertexCount} verts" +
                                (asset.HasSkeleton ? $", {asset.BoneNames.Length} bones" : "") +
                                $", baseTex={(asset.BaseTexture != null ? "folder" : (asset.TextureBytes != null ? "glb" : "none"))}" +
                                $", patterns={asset.PatternTextures?.Count ?? 0}");
                        }
                        else Plugin.Log.LogWarning($"  Parse failed for {manifest.name}");
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"  Bank load error: {ex}");
                    }
                }
                else Plugin.Log.LogWarning($"Missing GLB for {manifest.name}: {glbPath}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to load manifest in {dir}: {ex}");
            }
        }

        HairMasterPatches.ResetInjected();
        HairMasterPatches.RefreshCustomRecords(); // re-point HairTextures after F5 texture reload
        Plugin.Log.LogInfo($"Hair scan complete. {LoadedManifests.Count} manifests, {HairBank.Count} banked.");
    }

    private static int GetNextAvailableId()
    {
        int maxGameId = 1000;
        try
        {
            if (MHair.dict != null)
                foreach (HairType key in MHair.dict.Keys)
                {
                    int v = (int)key;
                    if (v > maxGameId) maxGameId = v;
                }
        }
        catch (Exception ex) { Plugin.Log.LogError($"MHair.dict inspect: {ex.Message}"); }
        return maxGameId + 1;
    }

    private static void SaveManifestId(string jsonPath, HairManifest manifest)
    {
        try
        {
            string rawJson = JsonSerializer.Serialize(manifest, _jsonOptions);
            string cleanJson = FormatJsonCleanly(rawJson);
            File.WriteAllText(jsonPath, cleanJson);
        }
        catch (Exception ex) { Plugin.Log.LogError($"SaveManifestId: {ex.Message}"); }
    }

    private static string FormatJsonCleanly(string json)
    {
        string pattern = @"\[\s*([^\[\]\{\}]+?)\s*\]";
        return Regex.Replace(json, pattern, m =>
        {
            string content = m.Groups[1].Value;
            content = Regex.Replace(content, @"\s+", "");
            return "[" + content + "]";
        });
    }
}