using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace MyFirstPlugin.Hair;

/// <summary>
/// Loads custom textures from a "Textures" folder located next to the hair's .glb file.
///
/// Naming convention (matches vanilla's mechanism — see HairCustomAdjuster):
///   - "albedo.png" / "main.png" / "base.png" / "diffuse.png"  -> base texture (TextureId == 0)
///   - "pattern_1.png", "pattern_2.png", ...                    -> pattern textures (TextureId == N)
///   - Any other image file is ignored (except as fallback base if no albedo is present).
///
/// In vanilla, "color pattern" IS the _MainTex. There is no separate overlay map.
/// So pattern_N.png is simply an alternative _MainTex that gets selected when TextureId = N.
/// </summary>
public static class HairTextureLoader
{
    private static readonly string[] BaseNames = { "albedo", "main", "base", "diffuse" };

    public static void LoadTexturesForAsset(HairAsset asset, string glbPath)
    {
        if (asset == null || string.IsNullOrEmpty(glbPath)) return;

        string dir = Path.GetDirectoryName(glbPath);
        if (string.IsNullOrEmpty(dir)) return;

        string texDir = Path.Combine(dir, "Textures");
        if (!Directory.Exists(texDir))
        {
            string alt = Path.Combine(dir, "textures");
            if (!Directory.Exists(alt)) return;
            texDir = alt;
        }

        asset.PatternTextures ??= new Dictionary<int, Texture2D>();

        string[] files = Directory.GetFiles(texDir, "*.*", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        Texture2D firstFallback = null;

        foreach (string file in files)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".tga") continue;

            string baseName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();

            // ── pattern_N.png (or pattern_0.png as base) ──
            var pm = Regex.Match(baseName, @"^pattern_?(\d+)$");
            if (pm.Success && int.TryParse(pm.Groups[1].Value, out int patternId))
            {
                var tex = LoadTexture(file);
                if (tex != null)
                {
                    if (patternId == 0)
                    {
                        if (asset.BaseTexture != null) UnityEngine.Object.Destroy(asset.BaseTexture);
                        asset.BaseTexture = tex;
                        Plugin.Log.LogInfo($"[HairTex] Loaded pattern_0 (base) {tex.width}x{tex.height}");
                    }
                    else
                    {
                        asset.PatternTextures[patternId] = tex;
                        Plugin.Log.LogInfo($"[HairTex] Loaded pattern_{patternId} {tex.width}x{tex.height}");
                    }
                }
                continue;
            }

            // ── explicit base name ──
            bool isBase = false;
            foreach (string bn in BaseNames)
                if (baseName == bn) { isBase = true; break; }

            if (isBase)
            {
                var tex = LoadTexture(file);
                if (tex != null)
                {
                    if (asset.BaseTexture != null) UnityEngine.Object.Destroy(asset.BaseTexture);
                    asset.BaseTexture = tex;
                    Plugin.Log.LogInfo($"[HairTex] Loaded base {tex.width}x{tex.height} from {Path.GetFileName(file)}");
                }
                continue;
            }

            // ── candidate fallback base ──
            if (firstFallback == null)
                firstFallback = LoadTexture(file);
        }

        // If no explicit base, use first unclassified image as fallback base
        if (asset.BaseTexture == null && firstFallback != null)
        {
            asset.BaseTexture = firstFallback;
            Plugin.Log.LogInfo($"[HairTex] Using fallback base '{firstFallback.name}' for hair {asset.Id}");
        }
        else if (firstFallback != null && firstFallback != asset.BaseTexture)
        {
            UnityEngine.Object.Destroy(firstFallback);
        }
    }

    /// <summary>
    /// Number of selectable patterns for this hair (the closet slider max + 1).
    /// Vanilla's slider goes 0 .. HairTextures.Length-1, so we return the same kind of count.
    /// </summary>
    public static int GetPatternCount(HairAsset asset)
    {
        if (asset == null) return 0;

        int maxPatternId = -1;
        if (asset.PatternTextures != null)
        {
            foreach (var k in asset.PatternTextures.Keys)
                if (k > maxPatternId) maxPatternId = k;
        }

        int count = maxPatternId + 1;
        if (asset.BaseTexture != null && count < 1) count = 1;
        return count;
    }

    /// <summary>
    /// Builds a contiguous Texture[] in the exact shape vanilla expects:
    /// index 0 = base (TextureId 0), index N = pattern_N.
    /// Missing ids fall back to the base texture so nothing is ever null.
    /// Assign this to MHairRecord.HairTextures so the vanilla UI slider picks up the right max.
    /// </summary>
    public static Texture[] BuildOrderedTextureArray(HairAsset asset)
    {
        int count = GetPatternCount(asset);
        if (count <= 0) return Array.Empty<Texture>();

        var arr = new Texture[count];

        Texture fallback = asset.BaseTexture;
        if (fallback == null && asset.PatternTextures != null)
        {
            foreach (var kv in asset.PatternTextures)
            {
                if (kv.Value != null) { fallback = kv.Value; break; }
            }
        }

        arr[0] = fallback;
        for (int i = 1; i < count; i++)
        {
            Texture2D pat = null;
            if (asset.PatternTextures != null)
                asset.PatternTextures.TryGetValue(i, out pat);
            arr[i] = (pat != null ? (Texture)pat : null) ?? fallback;
        }
        return arr;
    }

    public static void CleanupAsset(HairAsset asset)
    {
        if (asset == null) return;
        if (asset.BaseTexture != null) { UnityEngine.Object.Destroy(asset.BaseTexture); asset.BaseTexture = null; }
        if (asset.PatternTextures != null)
        {
            foreach (var t in asset.PatternTextures.Values)
                if (t != null) UnityEngine.Object.Destroy(t);
            asset.PatternTextures.Clear();
        }
    }

    private static Texture2D LoadTexture(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (UnityEngine.ImageConversion.LoadImage(tex, data))
            {
                tex.name = Path.GetFileNameWithoutExtension(path);
                return tex;
            }
            UnityEngine.Object.Destroy(tex);
            Plugin.Log.LogWarning($"[HairTex] ImageConversion failed for '{path}'");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[HairTex] Exception loading '{path}': {ex.Message}");
        }
        return null;
    }
}