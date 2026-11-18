using BepInEx;
using ExposureUnnoticed2.Master.BodyPaint;
using ExposureUnnoticed2.Object3D.IngameManager;
using ExposureUnnoticed2.Object3D.Player.Scripts;
using ExposureUnnoticed2.Scripts.InGame;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using UnityEngine;

namespace MyFirstPlugin.Paint;

/// <summary>
/// Applies material-property effects to named renderers.
/// Each targeted renderer gets ONE set of cloned materials with ALL matching
/// effects' properties baked in — so multiple effects stack cleanly.
/// </summary>
public static class EffectManager
{
    private sealed class AppliedState
    {
        public Renderer Renderer;
        public Material[] OriginalShared;
        public Material[] AppliedClones;
        public HashSet<int> EffectIds = new();
    }

    // Keyed by Renderer.GetInstanceID()
    private static readonly Dictionary<int, AppliedState> _applied = new();
    private static HashSet<int> _lastActive = new();
    private static float _nextVerify;
    private const float VerifyInterval = 0.5f;

    // ───────────────────────────── public API ─────────────────────────────

    public static void Enforce()
    {
        try
        {
            var dict = GameState.PlayerCustomData?.BodyPaintTypeDict;
            if (dict == null) return;

            var active = new HashSet<int>();
            foreach (var a in PaintManager.Bank.Values)
            {
                if (!a.IsEffect) continue;
                if (dict.TryGetValue((BodyPaintType)a.Id, out bool on) && on)
                    active.Add(a.Id);
            }

            bool activeChanged = !active.SetEquals(_lastActive);
            bool needsVerify = Time.unscaledTime >= _nextVerify;
            if (!activeChanged && !needsVerify) return;
            if (needsVerify) _nextVerify = Time.unscaledTime + VerifyInterval;

            if (activeChanged)
            {
                _lastActive = active;
                RebuildAll(active);
            }
            else if (IsStale())
            {
                // Renderer materials were replaced externally (paint, re-equip, etc.).
                // Full rebuild handles dead renderers + newly created ones.
                RebuildAll(_lastActive);
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[Effect] Enforce: {ex.Message}"); }
    }

    public static void ForceRebuild()
    {
        _lastActive = new HashSet<int>();
        _nextVerify = 0f;
        Enforce();
    }

    /// <summary>Called by PaintManager after it rewrites a renderer's materials.</summary>
    public static void NotifyMaterialsChanged()
    {
        if (_lastActive.Count > 0) ReapplyInPlace();
    }

    // ───────────────────────────── rebuild ─────────────────────────────

    private static void RebuildAll(HashSet<int> active)
    {
        RestoreAll();
        if (active.Count == 0) return;

        var root = PlayerFacade.Instance?.GetTransform();
        if (root == null) return;

        // renderer → list of effects that target it
        var rendererToEffects = new Dictionary<Renderer, List<PaintAsset>>();

        foreach (int id in active)
        {
            if (!PaintManager.Bank.TryGetValue(id, out var asset) || !asset.IsEffect) continue;
            var targets = asset.Manifest?.targets;
            if (targets == null) continue;

            foreach (string target in targets)
            {
                if (string.IsNullOrWhiteSpace(target)) continue;
                int hits = 0;
                foreach (var r in FindRenderers(root, target))
                {
                    hits++;
                    if (!rendererToEffects.TryGetValue(r, out var list))
                        rendererToEffects[r] = list = new List<PaintAsset>();
                    list.Add(asset);
                }
                if (hits == 0)
                    Plugin.Log.LogWarning($"[Effect] '{asset.Manifest.name}': target '{target}' matched 0 renderers");
            }
        }

        foreach (var kv in rendererToEffects)
        {
            var renderer = kv.Key;
            var assets = kv.Value;

            var original = renderer.sharedMaterials;
            if (original == null || original.Length == 0) continue;

            // Manual copy — Il2CppReferenceArray has no .Clone()
            var origCopy = new Material[original.Length];
            for (int i = 0; i < original.Length; i++) origCopy[i] = original[i];

            var clones = new Material[original.Length];
            var state = new AppliedState
            {
                Renderer = renderer,
                OriginalShared = origCopy,
                AppliedClones = clones,
            };
            foreach (var a in assets) state.EffectIds.Add(a.Id);

            for (int i = 0; i < original.Length; i++)
            {
                if (original[i] == null) { clones[i] = null; continue; }
                var clone = new Material(original[i]) { name = original[i].name + "_fx" };
                foreach (var a in assets) ApplyProperties(clone, a);
                clones[i] = clone;
            }

            renderer.sharedMaterials = clones;
            _applied[renderer.GetInstanceID()] = state;
        }

        Plugin.Log.LogInfo(
            $"[Effect] Rebuild: {active.Count} effect(s) → {rendererToEffects.Count} renderer(s)");
    }

    /// <summary>Reapply over whatever materials are currently on the renderer (paint-change path).</summary>
    private static void ReapplyInPlace()
    {
        foreach (var state in _applied.Values)
        {
            if (state.Renderer == null) continue;

            var current = state.Renderer.sharedMaterials;
            if (MaterialsMatch(current, state.AppliedClones)) continue;

            // Manual copy — Il2CppReferenceArray has no .Clone()
            var newOrig = new Material[current.Length];
            for (int i = 0; i < current.Length; i++) newOrig[i] = current[i];
            state.OriginalShared = newOrig;

            var clones = new Material[current.Length];
            for (int i = 0; i < current.Length; i++)
            {
                if (current[i] == null) { clones[i] = null; continue; }
                var clone = new Material(current[i]) { name = current[i].name + "_fx" };
                foreach (int id in state.EffectIds)
                    if (PaintManager.Bank.TryGetValue(id, out var a)) ApplyProperties(clone, a);
                clones[i] = clone;
            }

            state.Renderer.sharedMaterials = clones;
            foreach (var m in state.AppliedClones) if (m != null) UnityEngine.Object.Destroy(m);
            state.AppliedClones = clones;
        }
    }

    private static void RestoreAll()
    {
        foreach (var state in _applied.Values)
        {
            try
            {
                if (state.Renderer != null
                    && MaterialsMatch(state.Renderer.sharedMaterials, state.AppliedClones))
                {
                    state.Renderer.sharedMaterials = state.OriginalShared;
                }
                if (state.AppliedClones != null)
                    foreach (var m in state.AppliedClones)
                        if (m != null) UnityEngine.Object.Destroy(m);
            }
            catch { }
        }
        _applied.Clear();
    }

    private static bool IsStale()
    {
        foreach (var state in _applied.Values)
        {
            if (state.Renderer == null) return true;
            if (!MaterialsMatch(state.Renderer.sharedMaterials, state.AppliedClones)) return true;
        }
        return false;
    }

    /// <summary>
    /// Compares either Il2CppReferenceArray&lt;Material&gt; or Material[].
    /// IList&lt;T&gt; is implemented by both.
    /// </summary>
    private static bool MaterialsMatch(IList<Material> a, IList<Material> b)
    {
        if (a == null || b == null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }

    // ───────────────────────────── target search ─────────────────────────────

    /// <summary>
    /// Target syntax:
    ///   "Name"             → player hierarchy, exact-then-contains
    ///   "*"                → every renderer under the player
    ///   "scene:Name"       → scene-wide, exact-then-contains
    ///   "scene:*"          → every renderer in the scene
    /// </summary>
    private static IEnumerable<Renderer> FindRenderers(Transform root, string target)
    {
        if (target == "*")
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                if (r != null) yield return r;
            yield break;
        }
        if (target.Equals("scene:*", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                if (r != null) yield return r;
            yield break;
        }
        if (target.StartsWith("scene:", StringComparison.OrdinalIgnoreCase))
        {
            string needle = target.Substring(6);
            if (needle.Length == 0) yield break;
            Renderer exact = null;
            var contains = new List<Renderer>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (r == null) continue;
                if (string.Equals(r.name, needle, StringComparison.OrdinalIgnoreCase)) exact = r;
                else if (r.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) contains.Add(r);
            }
            if (exact != null) yield return exact;
            foreach (var r in contains) yield return r;
            yield break;
        }

        // Default: player hierarchy
        Renderer pExact = null;
        var pContains = new List<Renderer>();
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            if (string.Equals(r.name, target, StringComparison.OrdinalIgnoreCase)) pExact = r;
            else if (r.name.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0) pContains.Add(r);
        }
        if (pExact != null) yield return pExact;
        foreach (var r in pContains) yield return r;
    }

    // ───────────────────────────── property application ─────────────────────────────

    private static void ApplyProperties(Material mat, PaintAsset asset)
    {
        var m = asset.Manifest;

        // Semantic shortcuts
        if (m.glossiness.HasValue)
        {
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", m.glossiness.Value);
            else if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", m.glossiness.Value);
            else if (mat.HasProperty("_ClearCoatRoughness")) mat.SetFloat("_ClearCoatRoughness", 1f - m.glossiness.Value);
        }
        if (m.specular.HasValue)
        {
            if (mat.HasProperty("_Specular")) mat.SetFloat("_Specular", m.specular.Value);
            else if (mat.HasProperty("_SpecIntensity")) mat.SetFloat("_SpecIntensity", m.specular.Value);
            else if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", m.specular.Value);
        }
        if (m.specularColor != null && m.specularColor.Length >= 3)
        {
            var c = new Color(m.specularColor[0] / 255f, m.specularColor[1] / 255f, m.specularColor[2] / 255f, 1f);
            if (mat.HasProperty("_SpecColor")) mat.SetColor("_SpecColor", c);
            else if (mat.HasProperty("_SpecularColor")) mat.SetColor("_SpecularColor", c);
        }
        if (m.color != null && m.color.Length >= 3)
        {
            var c = new Color(m.color[0] / 255f, m.color[1] / 255f, m.color[2] / 255f, 1f);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
            else if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        }
        if (m.opacity.HasValue)
        {
            if (mat.HasProperty("_Color"))
            { var c = mat.GetColor("_Color"); c.a = m.opacity.Value; mat.SetColor("_Color", c); }
            if (mat.HasProperty("_BaseColor"))
            { var c = mat.GetColor("_BaseColor"); c.a = m.opacity.Value; mat.SetColor("_BaseColor", c); }
        }

        // Raw shader-property overrides
        if (m.properties != null)
        {
            foreach (var kv in m.properties)
            {
                string name = kv.Key;
                if (!mat.HasProperty(name)) continue;
                try { WriteJson(mat, name, kv.Value); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[Effect] '{name}' write failed: {ex.Message}"); }
            }
        }
    }

    private static void WriteJson(Material mat, string prop, JsonElement val)
    {
        switch (val.ValueKind)
        {
            case JsonValueKind.Number: mat.SetFloat(prop, val.GetSingle()); break;
            case JsonValueKind.True: mat.SetFloat(prop, 1f); break;
            case JsonValueKind.False: mat.SetFloat(prop, 0f); break;
            case JsonValueKind.Array:
                {
                    var arr = new List<float>();
                    foreach (var e in val.EnumerateArray()) arr.Add(e.GetSingle());
                    if (arr.Count == 3) mat.SetColor(prop, new Color(arr[0], arr[1], arr[2], 1f));
                    else if (arr.Count == 4) mat.SetColor(prop, new Color(arr[0], arr[1], arr[2], arr[3]));
                    break;
                }
        }
    }

    // ───────────────────────────── dev dump ─────────────────────────────

    public static void DumpNearby(float radius)
    {
        try
        {
            var player = PlayerFacade.Instance?.GetTransform();
            if (player == null) { Plugin.Log.LogWarning("[EffectDbg] no player"); return; }
            Vector3 center = player.position;

            string dir = Path.Combine(Paths.PluginPath, "MyFirstPlugin", "dumps");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "renderer_dump.txt");

            var items = new List<(Renderer r, float d)>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (r == null) continue;
                float dist = Vector3.Distance(r.transform.position, center);
                if (dist > radius) continue;
                items.Add((r, dist));
            }
            items.Sort((a, b) => a.d.CompareTo(b.d));

            var sb = new StringBuilder();
            sb.AppendLine("# Renderer dump");
            sb.AppendLine($"# When:       {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"# Radius:     {radius}m");
            sb.AppendLine($"# Player pos: {center.x:F2}, {center.y:F2}, {center.z:F2}");
            sb.AppendLine($"# Renderers:  {items.Count}");
            sb.AppendLine();

            foreach (var (r, dist) in items)
            {
                sb.AppendLine($"=== '{r.name}'  dist={dist:F2}m  enabled={r.enabled}  mats={r.sharedMaterials.Length} ===");
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null) { sb.AppendLine("  <null material>"); continue; }
                    sb.AppendLine($"  material '{mat.name}'  shader='{mat.shader?.name}'");
                    DumpProps(sb, mat);
                }
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
            Plugin.Log.LogInfo($"[EffectDbg] {items.Count} renderer(s) within {radius}m -> {path}");
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[EffectDbg] dump failed: {ex.Message}"); }
    }

    private static void DumpProps(StringBuilder sb, Material mat)
    {
        var sh = mat.shader;
        if (sh == null) return;
        int n = sh.GetPropertyCount();
        for (int i = 0; i < n; i++)
        {
            string name = sh.GetPropertyName(i);
            var type = sh.GetPropertyType(i);
            try
            {
                switch (type)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        sb.AppendLine($"    {name} (float)   = {mat.GetFloat(name)}");
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        sb.AppendLine($"    {name} (color)   = {mat.GetColor(name)}");
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        sb.AppendLine($"    {name} (vector)  = {mat.GetVector(name)}");
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        var t = mat.GetTexture(name);
                        sb.AppendLine($"    {name} (texture) = {(t != null ? t.name : "null")}");
                        break;
                }
            }
            catch { sb.AppendLine($"    {name} (?)= <error reading>"); }
        }
    }
}