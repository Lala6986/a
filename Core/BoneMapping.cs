using System;
using System.Collections.Generic;
using System.Reflection;
using ExposureUnnoticed2.Object3D.Player.Scripts;
using UnityEngine;

namespace MyFirstPlugin.Core;

public static class BoneMapping
{
    private enum Side { None, Left, Right }

    public sealed class Result
    {
        public Transform[] Bones = Array.Empty<Transform>();
        public int ResolvedFromPaor;
        public int ResolvedFromArmature;
        public int ResolvedFromRenderer;
        public int UnresolvedToRoot;
        public int SideTagMismatches;
        public int CreatedProxyCount;
        public Transform[] CreatedProxies = Array.Empty<Transform>();
    }

    internal static readonly Dictionary<string, string[]> ExplicitOverrideRoots =
        new(StringComparer.OrdinalIgnoreCase)
    {
        { "Breast_L", new[] { "Hips/Spine/Chest/BreastRotateL/Breast_L",
                              "Hips/PlayerBoneScaleAdjuster/Spine/PlayerBoneScaleAdjuster/Chest/PlayerBoneScaleAdjuster/BreastRotateL/Breast_L" } },
        { "Breast_R", new[] { "Hips/Spine/Chest/BreastRotateR/Breast_R",
                              "Hips/PlayerBoneScaleAdjuster/Spine/PlayerBoneScaleAdjuster/Chest/PlayerBoneScaleAdjuster/BreastRotateR/Breast_R" } }
    };

    public static Result MapAndInstantiateChainsUsingHierarchy(
        string[] srcNames,
        int[] boneParent,
        Matrix4x4[] sourceWorldRest,
        Matrix4x4[] sourceInverseBind,
        Transform armatureRoot,
        SkinnedMeshRenderer targetSmr,
        PlayerAvatarObjectReferencer objReferencer = null,
        bool preferArmature = true,
        bool allowRendererFallback = true)
    {
        srcNames ??= Array.Empty<string>();
        var result = new Result { Bones = new Transform[srcNames.Length] };

        var paorMap = BuildPaorMap(objReferencer);
        var exactMap = BuildExactNameMap(armatureRoot);
        var canonicalMap = BuildCanonicalMap(armatureRoot);
        var rawMap = targetSmr != null ? BuildRendererRawMap(targetSmr) : new Dictionary<string, int>();
        var canonMap = targetSmr != null ? BuildRendererCanonicalMap(targetSmr) : new Dictionary<string, int>();

        for (int i = 0; i < srcNames.Length; i++)
        {
            string src = string.IsNullOrEmpty(srcNames[i]) ? $"Bone_{i}" : srcNames[i];
            bool resolved = false;
            bool hasEndTag = HasEndTag(src);

            if (paorMap.Count > 0 && TryResolveViaPaor(paorMap, src, out var paorBone, out bool paorSideMismatch) && paorBone != null)
            {
                result.Bones[i] = paorBone;
                result.ResolvedFromPaor++;
                if (paorSideMismatch) result.SideTagMismatches++;
                resolved = true;
            }

            if (!resolved && preferArmature && TryResolveBoneStrict(exactMap, canonicalMap, src, armatureRoot, out var armBone, out bool armSideMismatch) && armBone != null)
            {
                if (hasEndTag && !HasEndTag(armBone.name))
                {
                    var endChild = FindEndChild(armBone, src);
                    if (endChild != null) armBone = endChild;
                }
                result.Bones[i] = armBone;
                result.ResolvedFromArmature++;
                if (armSideMismatch) result.SideTagMismatches++;
                resolved = true;
            }

            if (!resolved && allowRendererFallback && targetSmr != null
                && TryResolveRendererIndexStrict(rawMap, canonMap, src, targetSmr, out int rIdx, out bool rSideMismatch)
                && rIdx >= 0)
            {
                var smrBones = targetSmr.bones;
                if (smrBones != null && rIdx < smrBones.Length && smrBones[rIdx] != null)
                {
                    var bone = smrBones[rIdx];
                    if (hasEndTag && !HasEndTag(bone.name))
                    {
                        var endChild = FindEndChild(bone, src);
                        if (endChild != null) bone = endChild;
                    }
                    result.Bones[i] = bone;
                    result.ResolvedFromRenderer++;
                    if (rSideMismatch) result.SideTagMismatches++;
                }
            }
        }

        var created = new List<Transform>();
        int n = srcNames.Length;
        var done = new bool[n];

        for (int j = 0; j < n; j++)
        {
            if (done[j]) continue;
            if (result.Bones[j] != null) { done[j] = true; continue; }

            int nodeIdx = j;
            int resolvedAncestorIdx = -1;
            var stack = new Stack<int>(8);
            while (nodeIdx >= 0)
            {
                stack.Push(nodeIdx);
                int p = (boneParent != null && nodeIdx < boneParent.Length) ? boneParent[nodeIdx] : -1;
                if (p >= 0 && result.Bones[p] != null)
                {
                    resolvedAncestorIdx = p;
                    break;
                }
                nodeIdx = p;
            }

            var parentTransform = resolvedAncestorIdx >= 0 ? result.Bones[resolvedAncestorIdx] : armatureRoot;
            var path = new List<int>(stack);
            path.Reverse();

            int pathAnchorIdx = -1;
            if (resolvedAncestorIdx >= 0)
            {
                for (int k = 0; k < path.Count; k++)
                    if (path[k] == resolvedAncestorIdx) { pathAnchorIdx = k; break; }
            }

            int baseIdx = resolvedAncestorIdx >= 0
                ? resolvedAncestorIdx
                : (path.Count > 0 ? path[0] : -1);

            Matrix4x4 parentWorld = parentTransform != null ? parentTransform.localToWorldMatrix : Matrix4x4.identity;
            Matrix4x4 baseWorldRest = baseIdx >= 0 ? SW(baseIdx) : Matrix4x4.identity;
            Matrix4x4 offset = parentWorld * baseWorldRest.inverse;

            int start = (resolvedAncestorIdx >= 0 && pathAnchorIdx >= 0) ? pathAnchorIdx + 1 : 0;

            for (int k = start; k < path.Count; k++)
            {
                int idx = path[k];
                if (result.Bones[idx] != null) { parentTransform = result.Bones[idx]; continue; }

                var proxyGO = new GameObject(string.IsNullOrEmpty(srcNames[idx]) ? $"Proxy_{idx}" : srcNames[idx]);
                var proxy = proxyGO.transform;
                proxy.SetParent(parentTransform, false);

                Matrix4x4 parentW = parentTransform != null ? parentTransform.localToWorldMatrix : Matrix4x4.identity;
                Matrix4x4 targetW = offset * SW(idx);
                if (DecomposeTRS(parentW.inverse * targetW, out var pos, out var rot, out var scale))
                {
                    proxy.localPosition = pos;
                    proxy.localRotation = rot;
                    proxy.localScale = scale;
                }
                else
                {
                    proxy.localPosition = Vector3.zero;
                    proxy.localRotation = Quaternion.identity;
                    proxy.localScale = Vector3.one;
                }

                result.Bones[idx] = proxy;
                created.Add(proxy);
                parentTransform = proxy;
            }

            foreach (var k in path) done[k] = true;
        }

        for (int i = 0; i < n; i++)
        {
            if (result.Bones[i] == null)
            {
                result.Bones[i] = armatureRoot;
                result.UnresolvedToRoot++;
            }
        }

        result.CreatedProxyCount = created.Count;
        result.CreatedProxies = created.ToArray();

        return result;

        Matrix4x4 SW(int idx)
        {
            if (sourceWorldRest != null && idx >= 0 && idx < sourceWorldRest.Length && sourceWorldRest[idx] != default)
                return sourceWorldRest[idx];
            if (sourceInverseBind != null && idx >= 0 && idx < sourceInverseBind.Length)
                return sourceInverseBind[idx].inverse;
            return Matrix4x4.identity;
        }
    }

    public static Result Map(
        string[] srcNames,
        Transform armatureRoot,
        SkinnedMeshRenderer targetSmr,
        bool preferArmature = true,
        bool allowRendererFallback = true,
        PlayerAvatarObjectReferencer objReferencer = null)
    {
        srcNames ??= Array.Empty<string>();
        var result = new Result { Bones = new Transform[srcNames.Length] };

        var paorMap = BuildPaorMap(objReferencer);
        var exactMap = BuildExactNameMap(armatureRoot);
        var canonicalMap = BuildCanonicalMap(armatureRoot);
        var rawMap = targetSmr != null ? BuildRendererRawMap(targetSmr) : new Dictionary<string, int>();
        var canonMap = targetSmr != null ? BuildRendererCanonicalMap(targetSmr) : new Dictionary<string, int>();

        Transform hips = null;
        if (paorMap.Count > 0 && TryResolveViaPaor(paorMap, "Hips", out var h1, out _)) hips = h1;
        if (hips == null && TryResolveBoneStrict(exactMap, canonicalMap, "Hips", armatureRoot, out var h2, out _)) hips = h2;

        for (int i = 0; i < srcNames.Length; i++)
        {
            string src = string.IsNullOrEmpty(srcNames[i]) ? $"Bone_{i}" : srcNames[i];
            bool resolved = false;
            bool hasEndTag = HasEndTag(src);

            if (paorMap.Count > 0 && TryResolveViaPaor(paorMap, src, out var paorBone, out var paorSide) && paorBone != null)
            {
                result.Bones[i] = paorBone;
                result.ResolvedFromPaor++;
                if (paorSide) result.SideTagMismatches++;
                resolved = true;
            }

            if (!resolved && preferArmature && TryResolveBoneStrict(exactMap, canonicalMap, src, armatureRoot, out var armBone, out var armSide) && armBone != null)
            {
                if (hasEndTag && !HasEndTag(armBone.name))
                {
                    var ec = FindEndChild(armBone, src); if (ec != null) armBone = ec;
                }
                result.Bones[i] = armBone;
                result.ResolvedFromArmature++;
                if (armSide) result.SideTagMismatches++;
                resolved = true;
            }

            if (!resolved && allowRendererFallback && targetSmr != null
                && TryResolveRendererIndexStrict(rawMap, canonMap, src, targetSmr, out int rIdx, out var rSide) && rIdx >= 0)
            {
                var smrBones = targetSmr.bones;
                if (smrBones != null && rIdx < smrBones.Length && smrBones[rIdx] != null)
                {
                    var bone = smrBones[rIdx];
                    if (hasEndTag && !HasEndTag(bone.name))
                    {
                        var ec = FindEndChild(bone, src); if (ec != null) bone = ec;
                    }
                    result.Bones[i] = bone;
                    result.ResolvedFromRenderer++;
                    if (rSide) result.SideTagMismatches++;
                    resolved = true;
                }
            }

            if (!resolved)
            {
                result.Bones[i] = hips ?? armatureRoot;
                result.UnresolvedToRoot++;
            }
        }
        return result;
    }

    private static bool HasEndTag(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string s = name.ToLowerInvariant();
        return s.EndsWith("_end") || s.EndsWith(".end");
    }

    private static Transform FindEndChild(Transform baseTr, string sourceName)
    {
        if (baseTr == null) return null;
        string core = DecomposeCoreAndSide(sourceName).core;
        if (!HasEndTag(sourceName)) return null;

        Transform fallback = null;
        var q = new Queue<Transform>();
        for (int i = 0; i < baseTr.childCount; i++) q.Enqueue(baseTr.GetChild(i));

        while (q.Count > 0)
        {
            var t = q.Dequeue();
            string n = t.name.ToLowerInvariant();
            bool looksLikeEnd = n.EndsWith("_end") || n.EndsWith(".end") || n.Contains("_end_") || n.Contains(".end_");

            if (!looksLikeEnd)
            {
                for (int i = 0; i < t.childCount; i++) q.Enqueue(t.GetChild(i));
                continue;
            }

            if (!SidesCompatible(sourceName, t.name))
            {
                for (int i = 0; i < t.childCount; i++) q.Enqueue(t.GetChild(i));
                continue;
            }

            string coreCanonical = CanonicalName(core);
            if (n.Contains(coreCanonical)) return t;
            if (fallback == null) fallback = t;

            for (int i = 0; i < t.childCount; i++) q.Enqueue(t.GetChild(i));
        }
        return fallback;
    }

    private static bool SidesCompatible(string src, string tgt)
    {
        var a = DetectSide(src);
        var b = DetectSide(tgt);
        if (a == Side.None || b == Side.None) return true;
        return a == b;
    }

    private static Side DetectSide(string name)
    {
        if (string.IsNullOrEmpty(name)) return Side.None;
        string s = name.ToLowerInvariant();
        if (s.EndsWith("_l") || s.Contains(".l") || s.EndsWith(" left") || s.EndsWith("_left")
            || s.StartsWith("l_") || s.StartsWith("left_")) return Side.Left;
        if (s.EndsWith("_r") || s.Contains(".r") || s.EndsWith(" right") || s.EndsWith("_right")
            || s.StartsWith("r_") || s.StartsWith("right_")) return Side.Right;
        return Side.None;
    }

    private static (string core, Side side) DecomposeCoreAndSide(string name)
    {
        string n = CanonicalName(name);
        Side side = Side.None;

        if (n.EndsWith("_l")) { n = n[..^2]; side = Side.Left; }
        else if (n.EndsWith("_r")) { n = n[..^2]; side = Side.Right; }
        else if (n.EndsWith("left")) { n = n[..^4]; side = Side.Left; }
        else if (n.EndsWith("right")) { n = n[..^5]; side = Side.Right; }

        if (n.StartsWith("l_")) { n = n[2..]; side = Side.Left; }
        else if (n.StartsWith("r_")) { n = n[2..]; side = Side.Right; }

        n = n.Trim('_');
        return (n.EndsWith("_end") ? n[..^4] : n, side);
    }

    private static IEnumerable<string> SideVariants(string core, Side side)
    {
        if (string.IsNullOrEmpty(core)) yield break;
        switch (side)
        {
            case Side.Left:
                yield return core + "_L"; yield return core + "_l"; yield return core + ".L";
                yield return "L_" + core; yield return "l_" + core;
                yield return core + "_Left"; yield return core + "_left";
                yield return "Left_" + core; yield return "left_" + core;
                break;
            case Side.Right:
                yield return core + "_R"; yield return core + "_r"; yield return core + ".R";
                yield return "R_" + core; yield return "r_" + core;
                yield return core + "_Right"; yield return core + "_right";
                yield return "Right_" + core; yield return "right_" + core;
                break;
        }
        yield return core;
    }

    private static IEnumerable<string> CoreAliases(string core)
    {
        yield return core;
        if (core.Contains("pelvis")) yield return core.Replace("pelvis", "hips");
        if (core.Contains("hips")) yield return core.Replace("hips", "pelvis");
        if (core.Contains("glute")) yield return core.Replace("glute", "butt");
        if (core.Contains("butt")) yield return core.Replace("butt", "glute");
        if (core.Contains("thigh")) yield return core.Replace("thigh", "upperleg");
        if (core.Contains("upperleg")) yield return core.Replace("upperleg", "thigh");
        if (core.Contains("calf")) yield return core.Replace("calf", "lowerleg");
        if (core.Contains("shin")) yield return core.Replace("shin", "lowerleg");
        if (core.Contains("lowerleg")) { yield return core.Replace("lowerleg", "calf"); yield return core.Replace("lowerleg", "shin"); }
        if (core.Contains("forearm")) yield return core.Replace("forearm", "lowerarm");
        if (core.Contains("lowerarm")) yield return core.Replace("lowerarm", "forearm");
        if (core.Contains("upperarm")) yield return core.Replace("upperarm", "arm");
        if (core.Contains("arm") && !core.Contains("forearm") && !core.Contains("lowerarm"))
            yield return core.Replace("arm", "upperarm");
        if (core.Contains("breast")) yield return core.Replace("breast", "boob");
        if (core.Contains("boob")) yield return core.Replace("boob", "breast");
    }

    private static string CanonicalName(string n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        n = n.Trim();

        int dot = n.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < n.Length)
        {
            bool allDigits = true;
            for (int i = dot + 1; i < n.Length; i++)
                if (!char.IsDigit(n[i])) { allDigits = false; break; }
            if (allDigits) n = n[..dot];
        }

        n = n.Replace('.', '_').Replace(' ', '_');
        return n.ToLowerInvariant();
    }

    private static IEnumerable<string> EnumerateAliases(string src)
    {
        if (string.IsNullOrEmpty(src)) yield break;
        yield return src.Replace('.', '_');
        yield return src.Replace('_', '.');
        yield return src.ToLowerInvariant();
        yield return src.ToUpperInvariant();
        yield return src.Replace(".L", "_L").Replace(".R", "_R");
        yield return src.Replace("_L", ".L").Replace("_R", ".R");
        yield return src.Replace("Left", "_L").Replace("Right", "_R");
        yield return src.Replace("_Left", "_L").Replace("_Right", "_R");
        yield return src.Replace("left", "_l").Replace("right", "_r");
        yield return src.Replace("Bip001 ", "");
        yield return src.Replace("bip001 ", "");

        if (HasEndTag(src))
        {
            if (src.EndsWith(".end", StringComparison.OrdinalIgnoreCase)) yield return src[..^4];
            else if (src.EndsWith("_end", StringComparison.OrdinalIgnoreCase)) yield return src[..^4];
            else yield return src;
        }
        else yield return src + "_end";
    }

    private static Dictionary<string, Transform> BuildPaorMap(PlayerAvatarObjectReferencer paor)
    {
        var map = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
        if (paor == null) return map;

        Reg(paor.Hip, new[] { "hips", "pelvis", "hip" });
        Reg(paor.Spine, new[] { "spine" });
        Reg(paor.Chest, new[] { "chest", "upperchest" });
        Reg(paor.Neck, new[] { "neck" });
        Reg(paor.Head, new[] { "head" });
        Reg(paor.ThumbR, new[] { "thumb_r", "r_thumb", "thumb.right" });
        Reg(paor.BreastRotateL, new[] { "breastrotate_l", "breast_rotate_l", "boob_rotate_l" });
        Reg(paor.BreastRotateR, new[] { "breastrotate_r", "breast_rotate_r", "boob_rotate_r" });

        RegLR(paor.ToeL, paor.ToeR, "toe", Array.Empty<string>());
        RegLR(paor.FootL, paor.FootR, "foot", Array.Empty<string>());
        RegLR(paor.LowerLegL, paor.LowerLegR, "lowerleg", new[] { "calf", "shin" });
        RegLR(paor.LegL, paor.LegR, "upperleg", new[] { "thigh" });
        RegLR(paor.ShoulderL, paor.ShoulderR, "shoulder", new[] { "clavicle" });
        RegLR(paor.UpperArmL, paor.UpperArmR, "upperarm", new[] { "arm" });
        RegLR(paor.LowerArmL, paor.LowerArmR, "lowerarm", new[] { "forearm" });
        RegLR(paor.ButtL, paor.ButtR, "butt", new[] { "glute" });

        try
        {
            var dbl = paor.GetType().GetField("BreastDBL", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(paor);
            var dbr = paor.GetType().GetField("BreastDBR", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(paor);
            if (dbl != null && dbr != null)
            {
                var l = dbl.GetType().GetField("m_ReferenceObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(dbl) as Transform;
                var r = dbr.GetType().GetField("m_ReferenceObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(dbr) as Transform;
                if (l != null && r != null) RegLR(l, r, "breast", new[] { "boob", "chest_breast" });
            }
        }
        catch { }

        // ═══ FIX: Removed "armature" so it doesn't map to Hips! ═══
        if (paor.bodyMeshRenderer != null && paor.bodyMeshRenderer.rootBone != null)
            Reg(paor.bodyMeshRenderer.rootBone, new[] { "root", "skeleton" });

        return map;

        void Reg(Transform tr, string[] keys)
        {
            if (tr == null) return;
            foreach (var k in keys)
            {
                string c = CanonicalName(k);
                if (!string.IsNullOrEmpty(c) && !map.ContainsKey(c)) map[c] = tr;
            }
        }

        void RegLR(Transform l, Transform r, string core, string[] extra)
        {
            if (l != null)
            {
                foreach (var v in SideVariants(core, Side.Left)) Reg(l, new[] { v });
                foreach (var e in extra)
                    foreach (var v in SideVariants(e, Side.Left)) Reg(l, new[] { v });
            }
            if (r != null)
            {
                foreach (var v in SideVariants(core, Side.Right)) Reg(r, new[] { v });
                foreach (var e in extra)
                    foreach (var v in SideVariants(e, Side.Right)) Reg(r, new[] { v });
            }
        }
    }

    private static bool TryResolveViaPaor(Dictionary<string, Transform> paorMap, string src, out Transform tr, out bool sideMismatch)
    {
        tr = null;
        sideMismatch = false;
        if (paorMap == null || paorMap.Count == 0 || string.IsNullOrEmpty(src)) return false;

        bool hasEndTag = HasEndTag(src);

        if (paorMap.TryGetValue(CanonicalName(src), out tr))
        {
            if (tr != null && SidesCompatible(src, tr.name))
            {
                if (hasEndTag)
                {
                    var ec = FindEndChild(tr, src);
                    if (ec != null) { tr = ec; return true; }
                }
                return true;
            }
            if (tr != null) sideMismatch = true;
        }

        foreach (var alias in EnumerateAliases(src))
        {
            if (paorMap.TryGetValue(CanonicalName(alias), out tr))
            {
                if (tr != null && SidesCompatible(src, tr.name))
                {
                    if (hasEndTag)
                    {
                        var ec = FindEndChild(tr, src);
                        if (ec != null) { tr = ec; return true; }
                    }
                    return true;
                }
                if (tr != null) sideMismatch = true;
            }
        }

        var (core, side) = DecomposeCoreAndSide(src);
        foreach (var v in SideVariants(core, DetectSide(src)))
        {
            if (paorMap.TryGetValue(CanonicalName(v), out tr))
            {
                if (tr != null && SidesCompatible(src, tr.name))
                {
                    if (hasEndTag)
                    {
                        var ec = FindEndChild(tr, src);
                        if (ec != null) { tr = ec; return true; }
                    }
                    return true;
                }
                if (tr != null) sideMismatch = true;
            }
        }

        foreach (var ca in CoreAliases(core))
            foreach (var v in SideVariants(ca, DetectSide(src)))
            {
                if (paorMap.TryGetValue(CanonicalName(v), out tr))
                {
                    if (tr != null && SidesCompatible(src, tr.name))
                    {
                        if (hasEndTag)
                        {
                            var ec = FindEndChild(tr, src);
                            if (ec != null) { tr = ec; return true; }
                        }
                        return true;
                    }
                    if (tr != null) sideMismatch = true;
                }
            }

        tr = null;
        return false;
    }

    private static Dictionary<string, Transform> BuildExactNameMap(Transform root)
    {
        var map = new Dictionary<string, Transform>(512, StringComparer.OrdinalIgnoreCase);
        if (root == null) return map;
        var q = new Queue<Transform>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var t = q.Dequeue();
            if (!map.ContainsKey(t.name)) map[t.name] = t;
            for (int i = 0; i < t.childCount; i++) q.Enqueue(t.GetChild(i));
        }
        return map;
    }

    private static Dictionary<string, Transform> BuildCanonicalMap(Transform root)
    {
        var map = new Dictionary<string, Transform>(512, StringComparer.OrdinalIgnoreCase);
        if (root == null) return map;
        var q = new Queue<Transform>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var t = q.Dequeue();
            string c = CanonicalName(t.name);
            if (!map.ContainsKey(c)) map[c] = t;
            for (int i = 0; i < t.childCount; i++) q.Enqueue(t.GetChild(i));
        }
        return map;
    }

    private static Dictionary<string, int> BuildRendererRawMap(SkinnedMeshRenderer smr)
    {
        var map = new Dictionary<string, int>(256, StringComparer.OrdinalIgnoreCase);
        if (smr == null) return map;
        var bones = smr.bones;
        if (bones == null) return map;
        for (int i = 0; i < bones.Length; i++)
        {
            var b = bones[i];
            if (b != null && !map.ContainsKey(b.name)) map[b.name] = i;
        }
        return map;
    }

    private static Dictionary<string, int> BuildRendererCanonicalMap(SkinnedMeshRenderer smr)
    {
        var map = new Dictionary<string, int>(256, StringComparer.OrdinalIgnoreCase);
        if (smr == null) return map;
        var bones = smr.bones;
        if (bones == null) return map;
        for (int i = 0; i < bones.Length; i++)
        {
            var b = bones[i];
            if (b != null)
            {
                string c = CanonicalName(b.name);
                if (!map.ContainsKey(c)) map[c] = i;
            }
        }
        return map;
    }

    private static bool TryResolveRendererIndexStrict(
        Dictionary<string, int> rawMap, Dictionary<string, int> canonMap,
        string src, SkinnedMeshRenderer smr, out int index, out bool sideMismatch)
    {
        sideMismatch = false;
        index = -1;
        if (smr == null) return false;
        var smrBones = smr.bones;

        if (rawMap.TryGetValue(src, out index) && index >= 0 && index < smrBones.Length)
        {
            var b = smrBones[index];
            if (b != null && SidesCompatible(src, b.name)) return true;
            if (b != null) sideMismatch = true;
        }

        foreach (var alias in EnumerateAliases(src))
            if (rawMap.TryGetValue(alias, out index) && index >= 0 && index < smrBones.Length)
            {
                var b = smrBones[index];
                if (b != null && SidesCompatible(src, b.name)) return true;
                if (b != null) sideMismatch = true;
            }

        if (canonMap.TryGetValue(CanonicalName(src), out index) && index >= 0 && index < smrBones.Length)
        {
            var b = smrBones[index];
            if (b != null && SidesCompatible(src, b.name)) return true;
            if (b != null) sideMismatch = true;
        }

        var (core, side) = DecomposeCoreAndSide(src);
        foreach (var v in SideVariants(core, side))
            if (rawMap.TryGetValue(v, out index) && index >= 0 && index < smrBones.Length)
            {
                var b = smrBones[index];
                if (b != null && SidesCompatible(src, b.name)) return true;
                if (b != null) sideMismatch = true;
            }

        foreach (var ca in CoreAliases(core))
            foreach (var v in SideVariants(ca, side))
                if (rawMap.TryGetValue(v, out index) && index >= 0 && index < smrBones.Length)
                {
                    var b = smrBones[index];
                    if (b != null && SidesCompatible(src, b.name)) return true;
                    if (b != null) sideMismatch = true;
                }

        foreach (var v in SideVariants(core, side))
            if (canonMap.TryGetValue(CanonicalName(v), out index) && index >= 0 && index < smrBones.Length)
            {
                var b = smrBones[index];
                if (b != null && SidesCompatible(src, b.name)) return true;
                if (b != null) sideMismatch = true;
            }

        index = -1;
        return false;
    }

    private static bool TryResolveBoneStrict(
        Dictionary<string, Transform> exactMap, Dictionary<string, Transform> canonicalMap,
        string src, Transform armatureRoot, out Transform bone, out bool sideMismatch)
    {
        sideMismatch = false;

        if (exactMap.TryGetValue(src, out bone))
        {
            if (SidesCompatible(src, bone.name)) return true;
            sideMismatch = true;
        }

        foreach (var alias in EnumerateAliases(src))
            if (exactMap.TryGetValue(alias, out bone) && SidesCompatible(src, bone.name)) return true;

        if (canonicalMap.TryGetValue(CanonicalName(src), out bone) && SidesCompatible(src, bone.name)) return true;

        var (core, side) = DecomposeCoreAndSide(src);
        foreach (var v in SideVariants(core, side))
            if (exactMap.TryGetValue(v, out bone) && SidesCompatible(src, bone.name)) return true;

        foreach (var ca in CoreAliases(core))
            foreach (var v in SideVariants(ca, side))
                if (exactMap.TryGetValue(v, out bone) && SidesCompatible(src, bone.name)) return true;

        foreach (var v in SideVariants(core, side))
            if (canonicalMap.TryGetValue(CanonicalName(v), out bone) && SidesCompatible(src, bone.name)) return true;

        bone = null;
        return false;
    }

    public static void ApplyExplicitOverridesWithDescendants(string[] srcNames, Transform[] bones, Transform armatureRoot)
    {
        if (srcNames == null || bones == null || armatureRoot == null) return;

        var overrides = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in ExplicitOverrideRoots)
        {
            var t = ResolveFirstExisting(armatureRoot, kv.Value);
            if (t != null) overrides[kv.Key] = t;
            else Plugin.Log.LogWarning($"[BoneMap] Override base not found for '{kv.Key}' under '{armatureRoot.name}'.");
        }
        if (overrides.Count == 0) return;

        for (int i = 0; i < srcNames.Length; i++)
        {
            string src = srcNames[i] ?? "";
            if (src.Length == 0) continue;

            if (overrides.TryGetValue(src, out var b) && b != null)
            {
                bones[i] = HasEndTag(src) ? (FindEndChild(b, src) ?? b) : b;
                continue;
            }

            int dot = src.IndexOf('.');
            if (dot <= 0) continue;

            string prefix = src[..dot];
            if (overrides.TryGetValue(prefix, out var baseBone) && baseBone != null)
            {
                string subPath = src[(dot + 1)..].Replace('.', '/');
                var found = baseBone.Find(subPath);
                if (found == null) found = FindInSubtreeByName(baseBone, src)
                                        ?? FindInSubtreeByName(baseBone, subPath.Replace('/', '.'));
                if (HasEndTag(src) && found == null) found = FindEndChild(baseBone, src);
                bones[i] = found ?? baseBone;
            }
        }
    }

    private static Transform ResolveFirstExisting(Transform root, string[] relativePaths)
    {
        if (root == null || relativePaths == null) return null;
        foreach (var p in relativePaths)
            if (!string.IsNullOrEmpty(p))
            {
                var t = root.Find(p);
                if (t != null) return t;
            }
        return null;
    }

    private static Transform FindInSubtreeByName(Transform root, string exactName)
    {
        if (root == null || string.IsNullOrEmpty(exactName)) return null;
        var q = new Queue<Transform>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var t = q.Dequeue();
            if (string.Equals(t.name, exactName, StringComparison.Ordinal)) return t;
            for (int i = 0; i < t.childCount; i++) q.Enqueue(t.GetChild(i));
        }
        return null;
    }

    public static int FindIndex(string[] names, string wanted)
    {
        if (names == null || wanted == null) return -1;
        for (int i = 0; i < names.Length; i++)
            if (string.Equals(names[i], wanted, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public static string PathOf(Transform t)
    {
        if (t == null) return "<null>";
        var stack = new Stack<string>();
        while (t != null) { stack.Push(t.name); t = t.parent; }
        return string.Join("/", stack.ToArray());
    }

    private static bool DecomposeTRS(in Matrix4x4 m, out Vector3 pos, out Quaternion rot, out Vector3 scale)
    {
        pos = new Vector3(m.m03, m.m13, m.m23);

        var c0 = new Vector3(m.m00, m.m10, m.m20);
        var c1 = new Vector3(m.m01, m.m11, m.m21);
        var c2 = new Vector3(m.m02, m.m12, m.m22);

        float mag0 = c0.magnitude, mag1 = c1.magnitude, mag2 = c2.magnitude;
        if (mag0 < 1e-8f || mag1 < 1e-8f || mag2 < 1e-8f)
        {
            rot = Quaternion.identity;
            scale = Vector3.one;
            return false;
        }

        scale = new Vector3(mag0, mag1, mag2);

        var u0 = c0 / mag0;
        var u1 = c1 / mag1;
        var u2 = c2 / mag2;

        var rm = default(Matrix4x4);
        rm.SetColumn(0, new Vector4(u0.x, u0.y, u0.z, 0f));
        rm.SetColumn(1, new Vector4(u1.x, u1.y, u1.z, 0f));
        rm.SetColumn(2, new Vector4(u2.x, u2.y, u2.z, 0f));
        rm.m33 = 1f;

        rot = Quaternion.LookRotation(rm.GetColumn(2), rm.GetColumn(1));
        return true;
    }
}