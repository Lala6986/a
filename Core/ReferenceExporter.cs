using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using ExposureUnnoticed2.Object3D.Player.Scripts;
using ExposureUnnoticed2.ObjectUI.ClosetMenu.CustomizeMenu;
using MyFirstPlugin.Core;
using UnityEngine;
using UnityEngine.Rendering;
using System.Reflection;

namespace MyFirstPlugin.Hair;

public static class ReferenceExporter
{
    private static string Dir => Paths.BepInExRootPath;
    private static string Flag => Path.Combine(Dir, "export_reference.flag");

    // Call this from your existing MonoBehaviour's Update() method (e.g., inside DevReloaderBehaviour)
    public static void CheckHotkey()
    {
        if (!File.Exists(Flag)) return;

        // Press F9 while the flag file exists and you are in-game/closet to export
        if (Input.GetKeyDown(KeyCode.F9))
        {
            Plugin.Log.LogInfo("F9 pressed with export flag detected! Running reference export...");
            try
            {
                File.Delete(Flag);
                Export();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reference export failed: {ex}");
            }
        }
    }

    public static void RunExportNow()
    {
        try
        {
            Export();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Reference export failed: {ex}");
        }
    }

    private static void Export()
    {
        var facade = PlayerFacade.Instance;
        if (facade == null) { Plugin.Log.LogError("Export: no PlayerFacade"); return; }

        Transform root = facade.GetTransform();
        Transform head = Il2CppHelpers.FindHeadBone(root);
        if (head == null) { Plugin.Log.LogError("Export: head bone not found"); return; }

        var inv = CultureInfo.InvariantCulture;
        var obj = new StringBuilder();
        var mats = new StringBuilder();
        obj.AppendLine("# Unity head-bone space, X negated (= glTF space). Import in Blender with default OBJ axes (Forward -Z, Up Y).");

        string texDir = Path.Combine(Dir, "player_textures");
        Directory.CreateDirectory(texDir);
        var doneTextures = new HashSet<string>();

        int vertexBase = 1;
        foreach (var r in root.GetComponentsInChildren<Renderer>(false))
        {
            if (r == null || !r.enabled) continue;
            if (IsNoise(r)) continue;

            Mesh mesh;
            Matrix4x4 l2w = r.transform.localToWorldMatrix;

            var smr = r.TryCast<SkinnedMeshRenderer>();
            if (smr != null)
            {
                if (smr.sharedMesh == null || smr.sharedMesh.vertexCount == 0) continue;
                mesh = new Mesh();
                smr.BakeMesh(mesh);
            }
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                mesh = mf.sharedMesh;
            }

            var verts = mesh.vertices;
            var tris = mesh.triangles;
            if (verts.Length == 0 || tris.Length == 0) continue;

            obj.Append("o ").AppendLine(r.gameObject.name.Replace(' ', '_'));
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 p = head.InverseTransformPoint(l2w.MultiplyPoint3x4(verts[i]));
                obj.Append("v ")
                   .Append((-p.x).ToString("F5", inv)).Append(' ')
                   .Append(p.y.ToString("F5", inv)).Append(' ')
                   .Append(p.z.ToString("F5", inv)).AppendLine();
            }
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                obj.Append("f ")
                   .Append(vertexBase + tris[i]).Append(' ')
                   .Append(vertexBase + tris[i + 2]).Append(' ')
                   .Append(vertexBase + tris[i + 1]).AppendLine();
            }
            vertexBase += verts.Length;

            DumpMaterials(r, mats, inv);
            if (IsHairRenderer(r, head))
                HairProfileIO.Export(r, Dir);
            ExportTextures(r, texDir, doneTextures);
        }

        File.WriteAllText(Path.Combine(Dir, "player_reference.obj"), obj.ToString());
        string stamp = DateTime.Now.ToString("HHmmss");
        File.WriteAllText(Path.Combine(Dir, $"player_materials_{stamp}.txt"), mats.ToString());
        DumpTypeMembers("HairCustomAdjuster", Path.Combine(Dir, "HairCustomAdjuster_members.txt"));
        DumpTypeMembers("HairCustomizeData", Path.Combine(Dir, "HairCustomizeData_members.txt"));
        DumpTypeMembers("MHairRecord", Path.Combine(Dir, "MHairRecord_members.txt"));

        var h = new StringBuilder();
        Walk(root, 0, h);
        File.WriteAllText(Path.Combine(Dir, "player_hierarchy.txt"), h.ToString());

        Plugin.Log.LogInfo(Guid.NewGuid() == default ? "" : $"Reference export written to {Dir} (head bone: {head.name})");
    }

    private static bool IsHairRenderer(Renderer r, Transform head)
    {
        var hair = head.Find("Hair");
        if (hair != null && r.transform.IsChildOf(hair)) return true;
        return r.gameObject.name.StartsWith("CustomHairMesh");
    }

    private static void DumpTypeMembers(string typeName, string path)
    {
        try
        {
            var sb = new StringBuilder();
            const BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var t in typeof(PlayerFacade).Assembly.GetTypes())
            {
                if (t.Name != typeName) continue;
                sb.AppendLine($"== {t.FullName}");
                foreach (var fi in t.GetFields(f)) sb.AppendLine($"field  {fi.FieldType.Name} {fi.Name}");
                foreach (var pi in t.GetProperties(f)) sb.AppendLine($"prop   {pi.PropertyType.Name} {pi.Name}");
                foreach (var mi in t.GetMethods(f))
                {
                    var ps = string.Join(", ", Array.ConvertAll(mi.GetParameters(), p => p.ParameterType.Name + " " + p.Name));
                    sb.AppendLine($"method {mi.ReturnType.Name} {mi.Name}({ps})");
                }
            }
            File.WriteAllText(path, sb.ToString());
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"DumpTypeMembers {typeName}: {ex.Message}"); }
    }

    private static bool IsNoise(Renderer r)
    {
        string n = r.gameObject.name;
        if (n.StartsWith("Sphere") || n.StartsWith("Capsule") || n.StartsWith("Cube")) return true;
        if (n.Contains("Censor") || n.Contains("Particle")) return true;
        foreach (var m in r.sharedMaterials)
            if (m != null && (m.name.Contains("Censor") || m.name.Contains("Default-"))) return true;
        return false;
    }

    private static void ExportTextures(Renderer r, string dir, HashSet<string> done)
    {
        foreach (var m in r.sharedMaterials)
        {
            if (m == null) continue;
            try
            {
                var sh = m.shader;
                int n = sh.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    if (sh.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                    var tex = m.GetTexture(sh.GetPropertyName(i));
                    if (tex == null || tex.width < 8 || tex.height < 8) continue;
                    if (!done.Add(tex.name)) continue;
                    SaveTexture(tex, Path.Combine(dir, Safe(tex.name) + ".png"));
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Texture export failed on {m.name}: {ex.Message}");
            }
        }
    }

    internal static void SaveTexture(Texture tex, string path)
    {
        var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
        Graphics.Blit(tex, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var t2 = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
        t2.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
        t2.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        File.WriteAllBytes(path, (byte[])UnityEngine.ImageConversion.EncodeToPNG(t2));
        UnityEngine.Object.Destroy(t2);
    }

    private static string Safe(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private static void DumpMaterials(Renderer r, StringBuilder sb, CultureInfo inv)
    {
        try
        {
            sb.AppendLine($"=== {r.gameObject.name}");
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) continue;
                var sh = m.shader;
                sb.AppendLine($"  material: {m.name}   shader: {sh.name}");
                int n = sh.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    string name = sh.GetPropertyName(i);
                    string val = "";
                    switch (sh.GetPropertyType(i))
                    {
                        case ShaderPropertyType.Color: val = m.GetColor(name).ToString(); break;
                        case ShaderPropertyType.Vector: val = m.GetVector(name).ToString(); break;
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range: val = m.GetFloat(name).ToString(inv); break;
                        case ShaderPropertyType.Texture:
                            var t = m.GetTexture(name);
                            val = t == null ? "null" : t.name; break;
                    }
                    sb.AppendLine($"    {name} = {val}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (material dump files: {ex.Message})");
        }
    }

    private static void Walk(Transform t, int depth, StringBuilder sb)
    {
        string comps = "";
        try
        {
            foreach (var c in t.GetComponents<Component>())
                if (c != null) comps += c.GetIl2CppType().Name + ", ";
        }
        catch { }
        sb.AppendLine($"{new string(' ', depth * 2)}{t.name}   [{comps.TrimEnd(',', ' ')}]");
        for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, sb);
    }
}