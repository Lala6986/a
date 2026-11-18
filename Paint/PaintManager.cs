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
using MyFirstPlugin.Hair;
using UnityEngine;

namespace MyFirstPlugin.Paint;

public class PaintAsset
{
    public int Id;
    public PaintManifest Manifest;
    public BodyPaintType BaseType;
    public Texture2D Texture;
    public Material Material;
}

/// <summary>
/// Custom body paints ("tattoos"). Everything lives in
/// BepInEx/plugins/MyFirstPlugin/CustomPaints/&lt;folder&gt;/paint.json (+ optional texture).
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
            if (!File.Exists(jsonPath)) continue;

            try
            {
                var manifest = JsonSerializer.Deserialize<PaintManifest>(
                    File.ReadAllText(jsonPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (manifest == null) continue;

                if (manifest.id < CustomIdStart)
                {
                    manifest.id = NextAvailableId();
                    SaveManifest(jsonPath, manifest);
                }

                if (Bank.ContainsKey(manifest.id))
                {
                    Plugin.Log.LogError($"[Paint] Duplicate paint ID {manifest.id} in {dir}");
                    continue;
                }

                var asset = new PaintAsset
                {
                    Id = manifest.id,
                    Manifest = manifest,
                    BaseType = ParseBaseType(manifest.baseType),
                };

                if (!string.IsNullOrWhiteSpace(manifest.texture))
                {
                    string texPath = Path.Combine(dir, manifest.texture);
                    if (File.Exists(texPath))
                        asset.Texture = LoadTexture(texPath);
                    else
                        Plugin.Log.LogWarning($"[Paint] Missing texture '{texPath}'");
                }

                Bank[manifest.id] = asset;
                Plugin.Log.LogInfo(
                    $"[Paint] Loaded '{manifest.name}' id={manifest.id} base={asset.BaseType}" +
                    $" tex={(asset.Texture != null ? $"{asset.Texture.width}x{asset.Texture.height}" : "none")}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Paint] Failed to load {dir}: {ex}");
            }
        }

        Plugin.Log.LogInfo($"[Paint] Scan complete. {Bank.Count} custom paint(s).");
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

        // ═══ THE CRITICAL FIX ═══
        // Stop Unity's Garbage Collector from destroying our texture!
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
                Plugin.Log.LogInfo($"[Paint] Injected '{asset.Manifest.name}' (ID {asset.Id}).");
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

        // Force tint to white so we don't inherit TattooDark's dark color
        var m = asset.Manifest;
        var tint = new Color(m.r / 255f, m.g / 255f, m.b / 255f, 1f);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
        else if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);

        if (asset.Texture != null)
        {
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", asset.Texture);
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", asset.Texture);

            // The shader is "TransClipping", meaning it cuts out transparency based on a threshold.
            // We lower the threshold so the transparent background of the PNG vanishes completely.
            if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", 0.01f);
        }

        asset.Material = mat;
        Plugin.Log.LogInfo(
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

    // ───────────────────────────── UI refresh (F5) ─────────────────────────────

    // ───────────────────────────── proxy activation ─────────────────────────────

    /// <summary>Set by the ApplyFromGameState prefix, consumed by the TransToMaterialAsset prefix.</summary>
    internal static int PendingCustomId;

    /// <summary>
    /// The game's ApplyFromGameState only walks the vanilla BodyPaintType values, so our
    /// 1000+ types never reach TransToMaterialAsset. We temporarily mark this vanilla type
    /// as active and hijack its material call instead.
    /// </summary>
    internal const BodyPaintType ProxyType = BodyPaintType.TattooDark;

    /// <summary>ID of the first active custom paint, or -1.</summary>
    public static int GetActiveCustomId()
    {
        try
        {
            var dict = GameState.PlayerCustomData?.BodyPaintTypeDict;
            if (dict == null) return -1;
            foreach (var a in Bank.Values)
            {
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
    /// Acts as a cleanup safety net. Since the Proxy Method in PaintPatches.cs 
    /// handles inserting the material natively, we only need this to ensure 
    /// our custom materials are removed when the player turns the paint off.
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

            // Collect ALL active custom IDs
            var activeIds = new List<int>();
            foreach (var a in Bank.Values)
            {
                if (dict.TryGetValue((BodyPaintType)a.Id, out bool on) && on)
                    activeIds.Add(a.Id);
            }

            var mats = _bodyRenderer.materials;

            // Check if the current renderer already matches our desired state
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

            if (stateMatches) return; // Nothing to do, already perfect!

            var list = new List<Material>(mats.Length + activeIds.Count);
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;

                // Remove our old custom materials
                if (m.name != null && m.name.StartsWith(OurMaterialPrefix)) continue;

                // Remove the vanilla tattoos if we have custom paints replacing them
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

            // Find the sweat layer to insert before it
            int insertAt = list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i] != null ? list[i].name : null;
                if (n != null && n.IndexOf("Sweat", StringComparison.OrdinalIgnoreCase) >= 0)
                { insertAt = i; break; }
            }

            // Insert all active custom paints
            foreach (int id in activeIds)
            {
                if (Bank.TryGetValue(id, out var asset))
                {
                    var mat = GetOrCreateMaterial(asset, controller);
                    if (mat != null)
                    {
                        mat.name = OurMaterialPrefix + id;
                        list.Insert(insertAt, mat);
                        insertAt++; // stack them next to each other
                        Plugin.Log.LogInfo($"[Paint] Applied '{asset.Manifest.name}' (id {id}) to {BodyRendererName}");
                    }
                }
            }

            if (activeIds.Count == 0)
                Plugin.Log.LogInfo($"[Paint] Removed all custom paints from {BodyRendererName}");

            _bodyRenderer.materials = list.ToArray();
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[Paint] SyncBodyPaint: {ex.Message}"); }
    }

    // ───────────────────────────── dev helpers ─────────────────────────────

    public static void DumpPaintState(string tag)
    {
        try
        {
            var pcd = GameState.PlayerCustomData;
            if (pcd == null) { Plugin.Log.LogInfo($"[PaintDbg] dict({tag}) = no PlayerCustomData"); return; }
            var dict = pcd.BodyPaintTypeDict;
            if (dict == null) { Plugin.Log.LogInfo($"[PaintDbg] dict({tag}) = NULL"); return; }

            var sb = new System.Text.StringBuilder($"[PaintDbg] dict({tag}):");
            foreach (var kvp in dict) sb.Append($" {(int)kvp.Key}={(kvp.Value ? 1 : 0)}");
            Plugin.Log.LogInfo(sb.ToString());
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[Paint] DumpPaintState: {ex.Message}"); }
    }

    /// <summary>F6: reset only our custom paints to unpurchased and price 0 (dev/testing).</summary>
    public static void DevFreeMode()
    {
        try
        {
            var dict = GameState.PlayerCustomData?.BodyPaintTypeDict;
            foreach (var a in Bank.Values)
            {
                var type = (BodyPaintType)a.Id;
                if (dict != null)
                {
                    if (dict.ContainsKey(type)) dict[type] = false;
                    else dict.Add(type, false);
                }
                if (MBodyPaint.dict != null && MBodyPaint.dict.TryGetValue(type, out var rec))
                    rec._Price_k__BackingField = 0;
                a.Manifest.price = 0;
                try { GameStateFacade.SetActiveBodyPaint(type, false); } catch { }
            }

            Plugin.Log.LogInfo($"[Paint] F6: {Bank.Count} custom paint(s) reset to unpurchased, price 0.");
            DumpPaintState("after F6");
            RefreshUi();
        }
        catch (Exception ex) { Plugin.Log.LogError($"[Paint] F6 failed: {ex}"); }
    }

    /// <summary>F7: dump every renderer under the player with materials + _MainTex names.</summary>
    public static void DevDumpMaterials()
    {
        DumpBodyRenderers("F7");
    }

    /// <summary>F10: temporarily force a VANILLA paint on, dumping the body before/after so we
    /// can see exactly which renderer/material slot the game paints into.</summary>
    public static void DevForceVanilla()
    {
        try
        {
            var type = BodyPaintType.Inmon;
            var dict = GameState.PlayerCustomData?.BodyPaintTypeDict;

            Plugin.Log.LogInfo($"[Paint] F10: forcing vanilla {(int)type} active (was={(dict != null && dict.ContainsKey(type) ? dict[type].ToString() : "missing")})");
            DumpBodyRenderers("F10-before");

            GameStateFacade.SetActiveBodyPaint(type, true);
            DumpPaintState("F10 after SetActive");

            var ctrl = PlayerFacade.Instance != null ? PlayerFacade.Instance.pca?.PlayerBodyMaterialController : null;
            Plugin.Log.LogInfo($"[Paint] F10 controller={(ctrl != null)}");
            if (ctrl != null) ctrl.SetupBodyPaintFromGameState();

            DumpBodyRenderers("F10-after");

            // restore
            GameStateFacade.SetActiveBodyPaint(type, false);
            if (ctrl != null) ctrl.SetupBodyPaintFromGameState();
            DumpBodyRenderers("F10-restored");
        }
        catch (Exception ex) { Plugin.Log.LogError($"[Paint] F10 failed: {ex}"); }
    }

    private static void DumpBodyRenderers(string tag)
    {
        try
        {
            var root = PlayerFacade.Instance?.GetTransform();
            if (root == null) { Plugin.Log.LogInfo($"[Paint] {tag}: no player root"); return; }

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;

                var sb = new System.Text.StringBuilder();
                sb.Append($"[Paint] {tag} '{PathOf(r.transform, root)}' enabled={r.enabled} mats={r.sharedMaterials.Length}");

                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) { sb.Append($" [{i}]=null"); continue; }
                    var tex = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                    sb.Append($" [{i}]={m.name}(main={(tex != null ? tex.name : "-")})");
                }

                Plugin.Log.LogInfo(sb.ToString());
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[Paint] {tag} dump failed: {ex.Message}"); }
    }

    private static string PathOf(Transform t, Transform root)
    {
        var s = t.name;
        var p = t.parent;
        int guard = 0;
        while (p != null && p != root && guard++ < 32)
        {
            s = p.name + "/" + s;
            p = p.parent;
        }
        return s;
    }

    public static void RefreshUi()
    {
        try
        {
            var inst = CustomizeMenuView.Instance;
            if (inst == null) return;
            AccessTools.Method(typeof(CustomizeMenuView), "RefreshPaintView")?.Invoke(inst, null);
            Plugin.Log.LogInfo("[Paint] Paint tab refreshed.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Paint] RefreshUi failed: {ex.Message}");
        }
    }

    /// <summary>F8: dump every vanilla body-paint material + its textures (body UV reference).</summary>
    public static void DumpVanillaPaintTextures()
    {
        try
        {
            string dir = Path.Combine(Paths.BepInExRootPath, "bodypaint_textures");
            Directory.CreateDirectory(dir);

            var ar = AssetReferencer.Instance;
            if (ar == null) { Plugin.Log.LogWarning("[Paint] F8: no AssetReferencer"); return; }

            var mats = new (string Name, Material Mat)[]
            {
                ("SweatBody", ar.SweatBody), ("Inmon", ar.Inmon), ("Rakugaki", ar.Rakugaki),
                ("TattooDark", ar.TattooDark), ("TattooFlower1", ar.TattooFlower1),
                ("TattooFlower2", ar.TattooFlower2), ("TattooLotus", ar.TattooLotus),
                ("TattooSatanism", ar.TattooSatanism), ("TattooSlave", ar.TattooSlave),
                ("RubberSuit", ar.RubberSuit), ("ClothesPaint", ar.ClothesPaint),
            };

            int saved = 0;
            foreach (var (name, mat) in mats)
            {
                if (mat == null) { Plugin.Log.LogInfo($"[Paint] F8 {name}: null"); continue; }
                var sh = mat.shader;
                if (sh == null) continue;

                var props = new System.Text.StringBuilder();
                int n = sh.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    if (sh.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                    string prop = sh.GetPropertyName(i);
                    Texture t = null;
                    try { t = mat.GetTexture(prop); } catch { }
                    props.Append(' ').Append(prop).Append(t != null ? "*" : "-");
                    if (t == null) continue;
                    try
                    {
                        ReferenceExporter.SaveTexture(t, Path.Combine(dir, $"{name}_{prop.TrimStart('_')}.png"));
                        saved++;
                    }
                    catch (Exception ex) { props.Append("!").Append(ex.Message); }
                }
                Plugin.Log.LogInfo($"[Paint] F8 {name}: shader={sh.name} tex:{props}");
            }
            Plugin.Log.LogInfo($"[Paint] F8 done: {saved} texture(s) -> {dir}");
        }
        catch (Exception ex) { Plugin.Log.LogError($"[Paint] F8 failed: {ex}"); }
    }
}
