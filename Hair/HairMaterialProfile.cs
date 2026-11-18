using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine;
using UnityEngine.Rendering;
using ExposureUnnoticed2.Master.Hair;

namespace MyFirstPlugin.Hair;

public class HairMaterialProfile
{
    public string shader { get; set; }
    public int renderQueue { get; set; }
    public List<string> keywords { get; set; } = new();
    public Dictionary<string, float> floats { get; set; } = new();
    public Dictionary<string, float[]> colors { get; set; } = new();
    public Dictionary<string, string> textures { get; set; } = new();   // property -> png file name
}

public static class HairProfileIO
{
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, HairMaterialProfile> _profiles = new();
    private static readonly Dictionary<string, Texture2D> _texCache = new();
    private static readonly HashSet<int> _applied = new();

    private static string Safe(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    // ---------- EXPORT (dev tool, called from ReferenceExporter) ----------
    public static void Export(Renderer r, string outDir)
    {
        foreach (var m in r.sharedMaterials)
        {
            if (m == null) continue;
            var p = new HairMaterialProfile { shader = m.shader.name, renderQueue = m.renderQueue };
            foreach (var k in m.shaderKeywords) p.keywords.Add(k);

            var sh = m.shader;
            int n = sh.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                string name = sh.GetPropertyName(i);
                switch (sh.GetPropertyType(i))
                {
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        p.floats[name] = m.GetFloat(name); break;
                    case ShaderPropertyType.Color:
                        var c = m.GetColor(name);
                        p.colors[name] = new[] { c.r, c.g, c.b, c.a }; break;
                    case ShaderPropertyType.Vector:
                        var v = m.GetVector(name);
                        p.colors[name] = new[] { v.x, v.y, v.z, v.w }; break;
                    case ShaderPropertyType.Texture:
                        var t = m.GetTexture(name);
                        if (t != null && t.width >= 8 && t.height >= 8)
                            p.textures[name] = Safe(t.name) + ".png";
                        break;
                }
            }
            File.WriteAllText(Path.Combine(outDir, "hair_material_" + Safe(m.name) + ".json"),
                JsonSerializer.Serialize(p, Opt));
        }
    }

    // ---------- LOAD (runtime) ----------
    private static string DirFor(MHairRecord.HairCustomizeData d)
    {
        return HairManager.LoadedGlbPaths.TryGetValue((int)d.HairType, out var glb)
            ? Path.GetDirectoryName(glb) : null;
    }

    public static void ApplyOnce(Material m, MHairRecord.HairCustomizeData d)
    {
        try
        {
            int key = m.GetInstanceID();
            if (_applied.Contains(key)) return;

            string dir = DirFor(d);
            if (dir == null) return;

            string path = Path.Combine(dir, "material.json");
            if (!File.Exists(path)) return;          // no profile = old behaviour

            if (!_profiles.TryGetValue(path, out var p))
            {
                p = JsonSerializer.Deserialize<HairMaterialProfile>(File.ReadAllText(path), Opt);
                _profiles[path] = p;
            }
            if (p == null) return;
            _applied.Add(key);

            if (!string.IsNullOrEmpty(p.shader))
            {
                var sh = Shader.Find(p.shader);
                if (sh != null) { if (m.shader != sh) m.shader = sh; }
                else Plugin.Log.LogWarning($"[Hair] Shader '{p.shader}' not found, keeping current shader.");
            }

            foreach (var kv in p.floats)
                if (m.HasProperty(kv.Key)) m.SetFloat(kv.Key, kv.Value);

            foreach (var kv in p.colors)
            {
                if (!m.HasProperty(kv.Key) || kv.Value == null || kv.Value.Length < 4) continue;
                m.SetColor(kv.Key, new Color(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
            }

            foreach (var kv in p.textures)
            {
                if (!m.HasProperty(kv.Key)) continue;
                var tex = LoadTex(dir, kv.Value);
                if (tex != null) m.SetTexture(kv.Key, tex);
            }

            foreach (var k in p.keywords) m.EnableKeyword(k);
            if (p.renderQueue > 0) m.renderQueue = p.renderQueue;

            Plugin.Log.LogInfo($"[Hair] Applied material.json to {m.name}");
        }
        catch (Exception ex) { Plugin.Log.LogError($"[Hair] ApplyOnce failed: {ex}"); }
    }

    private static Texture2D LoadTex(string dir, string file)
    {
        string full = Path.Combine(dir, file);
        if (!File.Exists(full)) full = Path.Combine(dir, "textures", file);
        if (!File.Exists(full)) { Plugin.Log.LogWarning($"[Hair] Missing texture {file}"); return null; }
        if (_texCache.TryGetValue(full, out var cached) && cached != null) return cached;

        var t = new Texture2D(2, 2, TextureFormat.RGBA32, true, false);
        if (!UnityEngine.ImageConversion.LoadImage(t, File.ReadAllBytes(full))) return null;
        t.name = Path.GetFileNameWithoutExtension(file);
        _texCache[full] = t;
        return t;
    }
}