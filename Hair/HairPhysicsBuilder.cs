using ExposureUnnoticed2.Object3D.Player.Scripts;
using ExposureUnnoticed2.Object3D.Player.Scripts.Costume;
using MyFirstPlugin.Core;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using static DynamicBone;

namespace MyFirstPlugin.Hair;

internal static class HairPhysicsBuilder
{
    public static void Attach(HairAsset asset, Transform[] mappedBones, Transform armatureRoot,
                              PlayerAvatarObjectReferencer paor = null)
    {
        if (asset?.Dynamic?.Components == null) return;

        var nameMap = BuildNameMap(mappedBones, asset.BoneNames);
        var simulated = new HashSet<Transform>();

        var sortedChains = new List<KeyValuePair<string, HairAsset.DynamicComponent>>(asset.Dynamic.Components);
        sortedChains.Sort((a, b) =>
        {
            int idxA = Array.IndexOf(asset.BoneNames, a.Key);
            int idxB = Array.IndexOf(asset.BoneNames, b.Key);
            return GetDepth(idxA, asset.BoneParent).CompareTo(GetDepth(idxB, asset.BoneParent));
        });

        foreach (var kv in sortedChains)
        {
            var rootName = kv.Key;
            var comp = kv.Value;
            if (comp == null) continue;

            if (!nameMap.TryGetValue(rootName, out var root) || root == null)
            {
                Plugin.Log.LogWarning($"[HairPhys] Chain root '{rootName}' not found.");
                continue;
            }

            if (IsUnderAny(root, simulated))
            {
                continue;
            }

            foreach (var t in WalkSet(root, comp.IncludeDescendants ?? true, comp.Exclude))
                simulated.Add(t);

            var db = root.GetComponent<DynamicBone>() ?? root.gameObject.AddComponent<DynamicBone>();
            db.m_Root = root;
            db.m_Roots = null;

            var p = MergePreset(comp.Preset, comp.Params);
            ApplyParams(db, p);

            if (comp.Colliders != null && comp.Colliders.Count > 0)
            {
                if (db.m_Colliders == null)
                    db.m_Colliders = new Il2CppSystem.Collections.Generic.List<DynamicBoneColliderBase>();
                db.m_Colliders.Clear();
                var added = new HashSet<DynamicBoneColliderBase>();
                foreach (var token in comp.Colliders)
                {
                    var t = ResolveCollider(token, armatureRoot, paor);
                    if (t == null) continue;
                    foreach (var c in t.GetComponentsInChildren<DynamicBoneColliderBase>(true))
                        if (c != null && added.Add(c)) db.m_Colliders.Add(c);
                }
            }

            if (comp.Exclude != null && comp.Exclude.Count > 0)
            {
                if (db.m_Exclusions == null)
                    db.m_Exclusions = new Il2CppSystem.Collections.Generic.List<Transform>();
                db.m_Exclusions.Clear();
                foreach (var ex in comp.Exclude)
                    if (nameMap.TryGetValue(ex, out var exT) && exT != null) db.m_Exclusions.Add(exT);
            }

            try { db.SetupParticles(); db.UpdateParameters(); }
            catch { db.enabled = false; db.enabled = true; }

            AttachInertController(root, db);
            Plugin.Log.LogInfo($"[HairPhys] DynamicBone on '{rootName}' ({CountDesc(root)} nodes)");
        }
    }

    private static int GetDepth(int idx, int[] parents)
    {
        int d = 0, curr = idx;
        while (curr != -1 && d < 100) { curr = parents[curr]; d++; }
        return d;
    }

    private static void ApplyParams(DynamicBone db, HairAsset.DynamicParams p)
    {
        if (p == null) return;
        if (p.UpdateRate.HasValue) db.m_UpdateRate = p.UpdateRate.Value;
        if (p.Damping.HasValue) db.m_Damping = Mathf.Clamp01(p.Damping.Value);
        if (p.Elasticity.HasValue) db.m_Elasticity = Mathf.Clamp01(p.Elasticity.Value);
        if (p.Stiffness.HasValue) db.m_Stiffness = Mathf.Clamp01(p.Stiffness.Value);
        if (p.Inert.HasValue) db.m_Inert = Mathf.Clamp01(p.Inert.Value);
        if (p.Gravity != null && p.Gravity.Length == 3) db.m_Gravity = new Vector3(p.Gravity[0], p.Gravity[1], p.Gravity[2]);
        if (p.Force != null && p.Force.Length == 3) db.m_Force = new Vector3(p.Force[0], p.Force[1], p.Force[2]);
        if (p.Weight.HasValue) db.m_BlendWeight = Mathf.Clamp01(p.Weight.Value);
        if (p.EndLength.HasValue) db.m_EndLength = Mathf.Max(0f, p.EndLength.Value);
        if (p.EndOffset != null && p.EndOffset.Length == 3) db.m_EndOffset = new Vector3(p.EndOffset[0], p.EndOffset[1], p.EndOffset[2]);
        if (!string.IsNullOrWhiteSpace(p.FreezeAxis) && Enum.TryParse<FreezeAxis>(p.FreezeAxis, true, out var fa))
            db.m_FreezeAxis = fa;

        // ═══ THE COLLISION FIX ═══
        // If the radius is too thin, the hair slips through the body colliders.
        // We force it to be thick enough to register hits, unless the user explicitly set a bigger one.
        if (p.Radius.HasValue)
            db.m_Radius = Mathf.Max(0.05f, p.Radius.Value);
        else
            db.m_Radius = 0.05f;
    }

    private static HairAsset.DynamicParams MergePreset(string presetName, HairAsset.DynamicParams user)
    {
        if (string.IsNullOrWhiteSpace(presetName)) return user;
        if (presetName.Trim().ToLowerInvariant() != "wiggle") return user;

        var preset = new HairAsset.DynamicParams
        {
            UpdateRate = 60,
            Damping = 0.227f,
            Elasticity = 0.126f,
            Stiffness = 0.324f,
            Inert = 0.674f,
            Radius = 0.03f,
            Gravity = new float[3],
            Force = new float[3],
            FreezeAxis = "X",
            DistanceDisable = 20f,
            Weight = 1f
        };
        if (user == null) return preset;

        if (user.UpdateRate.HasValue) preset.UpdateRate = user.UpdateRate;
        if (user.Damping.HasValue) preset.Damping = user.Damping;
        if (user.Elasticity.HasValue) preset.Elasticity = user.Elasticity;
        if (user.Stiffness.HasValue) preset.Stiffness = user.Stiffness;
        if (user.Inert.HasValue) preset.Inert = user.Inert;
        if (user.Radius.HasValue) preset.Radius = user.Radius;
        if (user.Weight.HasValue) preset.Weight = user.Weight;
        if (user.DistanceDisable.HasValue) preset.DistanceDisable = user.DistanceDisable;
        if (user.EndLength.HasValue) preset.EndLength = user.EndLength;
        if (!string.IsNullOrWhiteSpace(user.FreezeAxis)) preset.FreezeAxis = user.FreezeAxis;
        if (user.Gravity != null && user.Gravity.Length == 3) preset.Gravity = user.Gravity;
        if (user.Force != null && user.Force.Length == 3) preset.Force = user.Force;
        if (user.EndOffset != null && user.EndOffset.Length == 3) preset.EndOffset = user.EndOffset;
        return preset;
    }

    private static void AttachInertController(Transform host, DynamicBone db)
    {
        var ctrl = host.GetComponent<DynamicBoneCancerInertController>()
                ?? host.gameObject.AddComponent<DynamicBoneCancerInertController>();
        try
        {
            var t = typeof(DynamicBoneCancerInertController);
            var f1 = t.GetField("dbs", BindingFlags.Instance | BindingFlags.NonPublic);
            var f2 = t.GetField("initInerts", BindingFlags.Instance | BindingFlags.NonPublic);
            f1?.SetValue(ctrl, new DynamicBone[] { db });
            f2?.SetValue(ctrl, new float[] { db.m_Inert });
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[HairPhys] InertController: {ex.Message}"); }
    }

    private static Dictionary<string, Transform> BuildNameMap(Transform[] bones, string[] names)
    {
        var d = new Dictionary<string, Transform>(StringComparer.Ordinal);
        if (bones == null || names == null) return d;
        int n = Math.Min(bones.Length, names.Length);
        for (int i = 0; i < n; i++)
            if (bones[i] != null && !string.IsNullOrWhiteSpace(names[i]) && !d.ContainsKey(names[i]))
                d[names[i]] = bones[i];
        return d;
    }

    private static bool IsUnderAny(Transform t, HashSet<Transform> roots)
    {
        while (t != null) { if (roots.Contains(t)) return true; t = t.parent; }
        return false;
    }

    private static List<Transform> WalkSet(Transform root, bool recursive, List<string> excludes)
    {
        var list = new List<Transform>();
        if (root == null) return list;
        if (!recursive) { list.Add(root); return list; }
        var q = new Queue<Transform>(); q.Enqueue(root);
        while (q.Count > 0)
        {
            var t = q.Dequeue();
            if (excludes != null && excludes.Contains(t.name)) continue;
            list.Add(t);
            for (int i = 0; i < t.childCount; i++) q.Enqueue(t.GetChild(i));
        }
        return list;
    }

    private static int CountDesc(Transform t) { int n = 1; for (int i = 0; i < t.childCount; i++) n += CountDesc(t.GetChild(i)); return n; }

    private static Transform ResolveCollider(string token, Transform armatureRoot, PlayerAvatarObjectReferencer paor)
    {
        if (paor != null)
        {
            switch ((token ?? "").ToLowerInvariant())
            {
                case "head": return paor.Head;
                case "neck": return paor.Neck;
                case "chest": case "upperchest": return paor.Chest;
                case "spine": return paor.Spine;
                case "hips": case "hip": return paor.Hip;
            }
        }
        return Il2CppHelpers.FindBoneByName(armatureRoot, token?.ToLowerInvariant() ?? "");
    }
}