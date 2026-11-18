using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using ExposureUnnoticed2.Master.BodyPaint;
using ExposureUnnoticed2.Object3D.IngameManager;
using ExposureUnnoticed2.Object3D.Player.Scripts;
using ExposureUnnoticed2.ObjectUI.ClosetMenu.CustomizeMenu;
using ExposureUnnoticed2.Scripts.InGame;
using HarmonyLib;
using UnityEngine;

namespace MyFirstPlugin.Paint;

public class PaintAsset
{
    public int Id;
    public PaintManifest Manifest;
    public BodyPaintType BaseType;
    public Texture2D Texture;
    public Material Material;

    /// <summary>True when this entry comes from effect.json — handled by EffectManager, not PaintManager.</summary>
    public bool IsEffect;
}

/// <summary>
/// Custom body paints ("tattoos"). Everything lives in
/// BepInEx/plugins/MyFirstPlugin/CustomPaints/&lt;folder&gt;/paint.json (+ optional texture)
/// or effect.json (material-property modifier, see EffectManager).
/// </summary>
public static class PaintManager
{
    public const int CustomIdStart = 1000;

    public static readonly Dictionary<int, PaintAsset> Bank = new();

    private static readonly Dictionary<string, BodyPaintType> _baseTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Inmon"] = BodyPaintType.Inmon,
            ["Rakugaki"] = BodyPaintType.Rakugaki,
            ["TattooDark"] = BodyPaintType.TattooDark,
            ["TattooFlower1"] = BodyPaintType.TattooFlower1,
            ["TattooFlower2"] = BodyPaintType.TattooFlower2,
            ["TattooLotus"] = BodyPaintType.TattooLotus,
            ["TattooSatanism"] = BodyPaintType.TattooSatanism,
            ["TattooSlave"] = BodyPaintType.TattooSlave,
            ["BodyOil"] = BodyPaintType.BodyOil,
            ["RubberSuit"] = BodyPaintType.RubberSuit,
        };

    // ───────────────────────────── loading ─────────────────────────────

    public static void LoadCustomPaints()
    {
        foreach (var old in Bank.Values)
        {
            try
            {
                if (old.Material != null) UnityEngine.Object.Destroy(old.Material);
                if (old.Texture != null) UnityEngine.Object.Destroy(old.Texture);
            }
            catch { }
        }
        Bank.Clear();

        string root = Path.Combine(Paths.PluginPath, "MyFirstPlugin", "CustomPaints");
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            Plugin.Log.LogInfo($"[Paint] Created directory: {root}");
            return;
        }

        foreach (string dir in Directory.GetDirectories(root))
        {
            string jsonPath = Path.Combine(dir, "paint.json");
            if (!File.Exists(jsonPath)) jsonPath = Path.Combine(dir, "effect.json");
            if (!File.Exists(jsonPath)) continue;

            try
            {
                var manifest = JsonSerializer.Deserialize<PaintManifest>(
                    File.ReadAllText(jsonPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (manifest == null) continue;

                bool isEffect = string.Equals(manifest.type, "effect", StringComparison.OrdinalIgnoreCase);

                if (manifest.id < CustomIdStart)
                {
                    manifest.id = NextAvailableId();
                    SaveManifest(jsonPath, manifest);
                }

                if (Bank.ContainsKey(manifest.id))
                {
                    Plugin.Log.LogError($"[Paint] Duplicate id {manifest.id} in {dir}");
                    continue;
                }

                var asset = new PaintAsset
                {
                    Id = manifest.id,
                    Manifest = manifest,
                    BaseType = isEffect ? BodyPaintType.TattooDark : ParseBaseType(manifest.baseType),
                    IsEffect = isEffect,
                };

                if (!isEffect && !string.IsNullOrWhiteSpace(manifest.texture))
                {
                    string texPath = Path.Combine(dir, manifest.texture);
                    if (File.Exists(texPath)) asset.Texture = LoadTexture(texPath);
                    else Plugin.Log.LogWarning($"[Paint] Missing texture '{texPath}'");
                }

                if (isEffect && (manifest.targets == null || manifest.targets.Length == 0))
                    Plugin.Log.LogWarning($"[Paint] Effect '{manifest.name}' has no targets — will not apply.");

                Bank[manifest.id] = asset;
                Plugin.Log.LogInfo(
                    $"[Paint] Loaded {(isEffect ? "effect" : "paint")} '{manifest.name}' id={manifest.id}" +
                    (isEffect
                        ? $" targets=[{string.Join(",", manifest.targets ?? Array.Empty<string>())}]"
                        : $" base={asset.BaseType}" +
                          $" tex={(asset.Texture != null ? $"{asset.Texture.width}x{asset.Texture.height}" : "none")}"));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Paint] Failed to load {dir}: {ex}");
            }
        }

        Plugin.Log.LogInfo($"[Paint] Scan complete. {Bank.Count} custom entr{(Bank.Count == 1 ? "y" : "ies")}.");
    }

    private static BodyPaintType ParseBaseType(string s)
    {
        if (!string.IsNullOrWhiteSpace(s) && _baseTypes.TryGetValue(s.Trim(), out var t))
            return t;
        if (!string.IsNullOrWhiteSpace(s))
            Plugin.Log.LogWarning($"[Paint] Unknown baseType '{s}', using TattooDark.");
        return BodyPaintType.TattooDark;
    }

    private static int NextAvailableId()
    {
        int id = CustomIdStart;
        while (Bank.ContainsKey(id)) id++;
        return id;
    }

    private static void SaveManifest(string path, PaintManifest manifest)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(manifest,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[Paint] Could not write id to {path}: {ex.Message}"); }
    }

    private static Texture2D LoadTexture(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
        if (!ImageConversion.LoadImage(tex, bytes))
        {
            UnityEngine.Object.Destroy(tex);
            throw new Exception($"Could not decode {path}");
        }
        tex.name = Path.GetFileNameWithoutExtension(path);
        tex.wrapMode = TextureWrapMode.Clamp;

        // ═══ Stop Unity's GC from destroying our texture ═══
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;

        return tex;
    }

    // ───────────────────────────── master injection ─────────────────────────────

    public static void InjectRecords()
    {
        var dict = MBodyPaint.dict;
        if (dict == null)
        {
            Plugin.Log.LogWarning("[Paint] MBodyPaint.dict is NULL, cannot inject.");
            return;
        }

        foreach (var asset in Bank.Values)
        {
            try
            {
                var type = (BodyPaintType)asset.Id;
                if (dict.ContainsKey(type)) continue;

                var rec = ScriptableObject.CreateInstance<RBodyPaint>();
                rec._Type_k__BackingField = type;
                rec._NameKey_k__BackingField = asset.Manifest.name;
                rec._Price_k__BackingField = asset.Manifest.price;
                rec._DescriptionKey_k__BackingField = asset.Manifest.description;
                rec._TypeId_k__BackingField = asset.Id;

                dict.Add(type, rec);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Paint] Inject failed for {asset.Id}: {ex}");
            }
        }
    }

    // ───────────────────────────── materials ─────────────────────────────

    public static PaintAsset Get(int id) => Bank.TryGetValue(id, out var a) ? a : null;

    public static Material GetOrCreateMaterial(PaintAsset asset, PlayerBodyMaterialController controller)
    {
        if (asset.Material != null) return asset.Material;
        if (asset.IsEffect) return null; // Effects never create paint materials.

        Material baseMat = null;
        try
        {
            // The game crashes if we pass BodyOil or RubberSuit to TransToMaterialAsset
            if ((int)asset.BaseType < CustomIdStart
                && asset.BaseType != BodyPaintType.BodyOil
                && asset.BaseType != BodyPaintType.RubberSuit)
            {
                baseMat = controller.TransToMaterialAsset(asset.BaseType);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Paint] TransToMaterialAsset({asset.BaseType}) failed: {ex.Message}");
        }

        baseMat ??= ResolveFallback(asset.BaseType);
        if (baseMat == null)
        {
            Plugin.Log.LogError($"[Paint] No base material for {asset.BaseType}; paint will not render.");
            return null;
        }

        var mat = new Material(baseMat) { name = $"CustomPaint_{asset.Id}" };

        // Force tint so we don't inherit TattooDark's dark color.
        var m = asset.Manifest;
        var tint = new Color(m.r / 255f, m.g / 255f, m.b / 255f, 1f);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
        else if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);

        if (asset.Texture != null)
        {
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", asset.Texture);
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", asset.Texture);

            // TransClipping shader cuts alpha; lower threshold so the transparent
            // background of the PNG vanishes completely.
            if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", 0.01f);
        }

        asset.Material = mat;
        Plugin.Log.LogDebug(
            $"[Paint] Material '{mat.name}' from '{baseMat.name}' shader={mat.shader?.name}" +
            $" tex={(asset.Texture != null ? asset.Texture.name : "base")}");
        return mat;
    }

    private static Material ResolveFallback(BodyPaintType type)
    {
        try
        {
            var ar = AssetReferencer.Instance;
            if (ar == null) return null;
            return type switch
            {
                BodyPaintType.Inmon => ar.Inmon,
                BodyPaintType.Rakugaki => ar.Rakugaki,
                BodyPaintType.TattooDark => ar.TattooDark,
                BodyPaintType.TattooFlower1 => ar.TattooFlower1,
                BodyPaintType.TattooFlower2 => ar.TattooFlower2,
                BodyPaintType.TattooLotus => ar.TattooLotus,
                BodyPaintType.TattooSatanism => ar.TattooSatanism,
                BodyPaintType.TattooSlave => ar.TattooSlave,
                BodyPaintType.BodyOil => ar.SweatBody,
                BodyPaintType.RubberSuit => ar.RubberSuit,
                _ => ar.TattooDark,
            };
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Paint] ResolveFallback failed: {ex.Message}");
            return null;
        }
    }

    // ───────────────────────────── proxy activation ─────────────────────────────

    /// <summary>Set by the ApplyFromGameState prefix, consumed by the TransToMaterialAsset prefix.</summary>
    internal static int PendingCustomId;

    /// <summary>
    /// The game's ApplyFromGameState only walks the vanilla BodyPaintType values, so our
    /// 1000+ types never reach TransToMaterialAsset. We temporarily mark this vanilla type
    /// as active and hijack its material call instead.
    /// </summary>
    internal const BodyPaintType ProxyType = BodyPaintType.TattooDark;

    /// <summary>ID of the first active custom paint (effects excluded), or -1.</summary>
    public static int GetActiveCustomId()
    {
        try
        {
            var dict = GameState.PlayerCustomData?.BodyPaintTypeDict;
            if (dict == null) return -1;
            foreach (var a in Bank.Values)
            {
                if (a.IsEffect) continue;
                if (dict.TryGetValue((BodyPaintType)a.Id, out bool on) && on) return a.Id;
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[Paint] GetActiveCustomId: {ex.Message}"); }
        return -1;
    }

    private static Renderer _bodyRenderer;

    private const string BodyRendererName = "Selestia_body";
    private const string OurMaterialPrefix = "CustomPaint_";

    /// <summary>
    /// Keeps the body's material list in sync with active custom paints.
    /// Effects are handled separately by EffectManager — they are skipped here.
    /// </summary>
    internal static void SyncBodyPaint(PlayerBodyMaterialController controller)
    {
        try
        {
            var facade = PlayerFacade.Instance;
            if (facade == null) return;
            var root = facade.GetTransform();
            if (root == null) return;

            if (_bodyRenderer == null)
            {
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r != null && r.name == BodyRendererName) { _bodyRenderer = r; break; }
                }
                if (_bodyRenderer == null) return;
            }

            var dict = GameState.PlayerCustomData?.BodyPaintTypeDict;
            if (dict == null) return;

            // Collect ALL active custom paints (effects excluded).
            var activeIds = new List<int>();
            foreach (var a in Bank.Values)
            {
                if (a.IsEffect) continue;
                if (dict.TryGetValue((BodyPaintType)a.Id, out bool on) && on)
                    activeIds.Add(a.Id);
            }

            var mats = _bodyRenderer.materials;

            // Already matches desired state?
            bool stateMatches = true;
            int currentCustomCount = 0;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m != null && m.name != null && m.name.StartsWith(OurMaterialPrefix))
                {
                    currentCustomCount++;
                    string idStr = m.name.Substring(OurMaterialPrefix.Length);
                    if (!int.TryParse(idStr, out int id) || !activeIds.Contains(id))
                        stateMatches = false;
                }
            }
            if (currentCustomCount != activeIds.Count) stateMatches = false;

            if (stateMatches) return;

            var list = new List<Material>(mats.Length + activeIds.Count);
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;

                // Drop our old custom paint materials.
                if (m.name != null && m.name.StartsWith(OurMaterialPrefix)) continue;

                // Drop the vanilla proxy material so the custom one can take its slot.
                if (activeIds.Count > 0)
                {
                    bool isProxy = false;
                    foreach (var id in activeIds)
                    {
                        if (Bank.TryGetValue(id, out var asset))
                        {
                            string baseTypeName = asset.BaseType.ToString();
                            if (m.name != null && m.name.Contains(baseTypeName)) { isProxy = true; break; }
                        }
                    }
                    if (isProxy) continue;
                }

                list.Add(m);
            }

            // Insert before the Sweat layer if present.
            int insertAt = list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i] != null ? list[i].name : null;
                if (n != null && n.IndexOf("Sweat", StringComparison.OrdinalIgnoreCase) >= 0)
                { insertAt = i; break; }
            }

            foreach (int id in activeIds)
            {
                if (Bank.TryGetValue(id, out var asset))
                {
                    var mat = GetOrCreateMaterial(asset, controller);
                    if (mat != null)
                    {
                        mat.name = OurMaterialPrefix + id;
                        list.Insert(insertAt, mat);
                        insertAt++;
                    }
                }
            }

            _bodyRenderer.materials = list.ToArray();

            // NEW: effects overlay paint — reapply now instead of waiting for the 0.5s verify tick
            EffectManager.NotifyMaterialsChanged();
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[Paint] SyncBodyPaint: {ex.Message}"); }
    }

    // ───────────────────────────── UI refresh ─────────────────────────────

    public static void RefreshUi()
    {
        try
        {
            var inst = CustomizeMenuView.Instance;
            if (inst == null) return;
            AccessTools.Method(typeof(CustomizeMenuView), "RefreshPaintView")?.Invoke(inst, null);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Paint] RefreshUi failed: {ex.Message}");
        }
    }
}