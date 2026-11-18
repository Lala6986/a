using System;
using System.Collections.Generic;
using ExposureUnnoticed2.Object3D.Player.Scripts;
using MyFirstPlugin.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace MyFirstPlugin.Hair;

internal static class HairSpawner
{
    public static GameObject Current { get; private set; }

    private static readonly List<Renderer> _disabledVanilla = new();
    private static Transform _head;

    public static bool Spawn(HairAsset asset, PlayerFacade facade, out string error)
    {
        error = null;
        if (asset?.Mesh == null) { error = "Asset mesh null"; return false; }
        if (facade == null) { error = "Facade null"; return false; }

        Cleanup();

        try
        {
            Transform playerRoot = facade.GetTransform();
            var paor = GetPaor(playerRoot);
            if (paor == null) { error = "PlayerAvatarObjectReferencer not found"; return false; }

            Transform head = paor.Head ?? Il2CppHelpers.FindHeadBone(playerRoot);
            if (head == null) { error = "Head bone not found"; return false; }

            Transform hairGroup = head.Find("Hair");
            if (hairGroup == null)
            {
                hairGroup = new GameObject("Hair").transform;
                hairGroup.SetParent(head, false);
                hairGroup.gameObject.layer = head.gameObject.layer;
            }

            var container = new GameObject($"CustomHair_{asset.Id}");
            container.transform.SetParent(hairGroup, false);
            container.layer = head.gameObject.layer;
            Current = container;

            Vector3 baseOffset = new Vector3(asset.Manifest.offsetX, asset.Manifest.offsetY, asset.Manifest.offsetZ);
            Quaternion baseRot = Quaternion.Euler(asset.Manifest.rotX, asset.Manifest.rotY, asset.Manifest.rotZ);

            // �� RIGID PATH ��
            if (!asset.HasSkeleton)
            {
                container.transform.localPosition = baseOffset;
                container.transform.localRotation = baseRot;
                container.transform.localScale = Vector3.one * asset.Manifest.scale;

                var rigidGO = new GameObject("CustomHairMesh");
                rigidGO.transform.SetParent(container.transform, false);
                rigidGO.layer = head.gameObject.layer;

                rigidGO.AddComponent<MeshFilter>().sharedMesh = asset.Mesh;
                var mr = rigidGO.AddComponent<MeshRenderer>();
                mr.sharedMaterial = EnsureMaterial(asset, head, paor);
                mr.shadowCastingMode = ShadowCastingMode.On;
                mr.receiveShadows = false;

                HideVanillaHair(head);
                Plugin.Log.LogInfo($"[Hair] Spawned rigid hair {asset.Id}");
                return true;
            }

            // �� SKINNED PATH ��

            var armature = new GameObject("Armature").transform;
            armature.SetParent(container.transform, false);
            armature.gameObject.layer = head.gameObject.layer;

            var mappedBones = BuildLocalSkeleton(asset, armature);

            int hrIdx = -1;
            for (int i = 0; i < asset.BoneNames.Length; i++)
            {
                if (asset.BoneNames[i].ToLowerInvariant() == "hair_root" || asset.BoneNames[i].ToLowerInvariant() == "head")
                {
                    hrIdx = i;
                    break;
                }
            }

            Vector3 autoAlignOffset = Vector3.zero;
            if (hrIdx >= 0 && asset.SourceWorldRest != null && hrIdx < asset.SourceWorldRest.Length)
            {
                var m = asset.SourceWorldRest[hrIdx];
                autoAlignOffset = -new Vector3(m.m03, m.m13, m.m23);
            }

            container.transform.localPosition = baseOffset + autoAlignOffset;
            container.transform.localRotation = baseRot;
            container.transform.localScale = Vector3.one * asset.Manifest.scale;

            var hairGO = new GameObject("CustomHairMesh");
            hairGO.transform.SetParent(container.transform, false);
            hairGO.layer = head.gameObject.layer;

            var smr = hairGO.AddComponent<SkinnedMeshRenderer>();

            Mesh cloned = UnityEngine.Object.Instantiate(asset.Mesh);
            cloned.name = $"{asset.Manifest.name}_Mesh";
            BindposeUtils.SanitizeWeights(cloned, mappedBones);
            cloned.bindposes = asset.InverseBindMatrices;

            smr.sharedMesh = cloned;
            smr.bones = mappedBones;
            smr.rootBone = (hrIdx >= 0) ? mappedBones[hrIdx] : mappedBones[0];

            smr.sharedMaterial = EnsureMaterial(asset, head, paor);
            smr.localBounds = cloned.bounds;
            smr.updateWhenOffscreen = true;
            smr.shadowCastingMode = ShadowCastingMode.On;
            smr.receiveShadows = false;

            // === AUTO-PHYSICS INJECTION ===
            if (asset.Dynamic == null || asset.Dynamic.Components == null || asset.Dynamic.Components.Count == 0)
            {
                Plugin.Log.LogInfo($"[Hair] No manual physics in JSON. Running auto-detector...");
                asset.Dynamic = AutoPhysicsGenerator.Generate(asset);
            }

            if (asset.Dynamic?.Components != null && asset.Dynamic.Components.Count > 0)
                HairPhysicsBuilder.Attach(asset, mappedBones, head, paor);

            HideVanillaHair(head);
            Plugin.Log.LogInfo($"[Hair] Spawned {asset.Id}: {mappedBones.Length} bones (local skeleton)");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            Cleanup();
            return false;
        }
    }

    public static void Cleanup()
    {
        if (Current != null)
        {
            try { UnityEngine.Object.Destroy(Current); } catch { }
            Current = null;
        }

        foreach (var r in _disabledVanilla)
        {
            try { if (r != null) r.enabled = true; } catch { }
        }
        _disabledVanilla.Clear();
        _head = null;
    }

    /// <summary>
    /// Call periodically (e.g. from DevReloaderBehaviour.Update). Keeps vanilla hair hidden
    /// if the game re-enables or re-creates its hair renderer after we spawned.
    /// </summary>
    public static void EnforceHidden()
    {
        if (Current == null || _head == null) return;
        try { HideVanillaHair(_head); } catch { }
    }

    private static Transform[] BuildLocalSkeleton(HairAsset asset, Transform parent)
    {
        int n = asset.BoneNames.Length;
        var bones = new Transform[n];

        for (int i = 0; i < n; i++)
        {
            var go = new GameObject(asset.BoneNames[i]);
            go.layer = parent.gameObject.layer;
            bones[i] = go.transform;
        }

        for (int i = 0; i < n; i++)
        {
            int p = asset.BoneParent[i];
            if (p >= 0 && p < n)
                bones[i].SetParent(bones[p], false);
            else
                bones[i].SetParent(parent, false);
        }

        for (int i = 0; i < n; i++)
        {
            int p = asset.BoneParent[i];
            Matrix4x4 local = (p >= 0 && p < n)
                ? asset.SourceWorldRest[p].inverse * asset.SourceWorldRest[i]
                : asset.SourceWorldRest[i];

            if (DecomposeTRS(local, out var pos, out var rot, out var scale))
            {
                bones[i].localPosition = pos;
                bones[i].localRotation = rot;
                bones[i].localScale = scale;
            }
        }

        return bones;
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
            rot = Quaternion.identity; scale = Vector3.one; return false;
        }

        scale = new Vector3(mag0, mag1, mag2);
        var u0 = c0 / mag0; var u1 = c1 / mag1; var u2 = c2 / mag2;
        var rm = default(Matrix4x4);
        rm.SetColumn(0, new Vector4(u0.x, u0.y, u0.z, 0f));
        rm.SetColumn(1, new Vector4(u1.x, u1.y, u1.z, 0f));
        rm.SetColumn(2, new Vector4(u2.x, u2.y, u2.z, 0f));
        rm.m33 = 1f;

        rot = Quaternion.LookRotation(rm.GetColumn(2), rm.GetColumn(1));
        return true;
    }

    private static Material EnsureMaterial(HairAsset asset, Transform head, PlayerAvatarObjectReferencer paor)
    {
        if (asset.BaseMaterial != null) return asset.BaseMaterial;

        var template = FindVanillaHairMaterial(head, paor, asset);
        if (template != null)
        {
            // Exact clone of the vanilla counterpart's material: do NOT rewrite the shader or
            // UTS properties. The game's own look comes from those untouched values.
            var m = new Material(template);

            Texture2D tex = null;
            if (asset.BaseTexture != null)
            {
                tex = asset.BaseTexture;
            }
            else if (asset.TextureBytes != null && asset.TextureBytes.Length > 0)
            {
                tex = new Texture2D(2, 2);
                if (!UnityEngine.ImageConversion.LoadImage(tex, asset.TextureBytes))
                {
                    Plugin.Log.LogWarning($"[Hair] Texture load failed for {asset.Id}");
                    UnityEngine.Object.Destroy(tex);
                    tex = null;
                }
            }

            if (tex != null)
            {
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            }

            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            if (m.HasProperty("_CullMode")) m.SetFloat("_CullMode", 0f);

            m.name = $"Hair_{asset.Id}_Mat";
            asset.BaseMaterial = m;
        }
        else
        {
            Plugin.Log.LogWarning($"[Hair] No vanilla hair material found, using default.");
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var m = new Material(cube.GetComponent<MeshRenderer>().sharedMaterial);
            UnityEngine.Object.Destroy(cube);
            asset.BaseMaterial = m;
        }

        return asset.BaseMaterial;
    }

    private static bool _loggedHairRenderers;

    /// <summary>One-shot diagnostic: log vanilla hair renderers' shadow flags + key UTS props.</summary>
    private static void LogVanillaHairRenderers(Transform head)
    {
        if (_loggedHairRenderers) return;
        _loggedHairRenderers = true;
        try
        {
            foreach (var r in head.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                string n = r.gameObject.name.ToLower();
                if (!(n.Contains("hair") || n.Contains("sakura") || n.Contains("pony"))) continue;

                var m = r.sharedMaterial;
                Plugin.Log.LogInfo($"[HairCmp] '{r.gameObject.name}' recv={r.receiveShadows} cast={r.shadowCastingMode} enabled={r.enabled} mat='{(m != null ? m.name : "null")}'");
                if (m == null) continue;
                Plugin.Log.LogInfo($"[HairCmp]   shader='{(m.shader != null ? m.shader.name : "null")}' " +
                    $"_MainTex={GetT(m, "_MainTex")} _BaseMap={GetT(m, "_BaseMap")} _BaseColor={GetC(m, "_BaseColor")} " +
                    $"_Set_SystemShadowsToBase={GetF(m, "_Set_SystemShadowsToBase")} _Tweak_SystemShadowsLevel={GetF(m, "_Tweak_SystemShadowsLevel")}");
                Plugin.Log.LogInfo($"[HairCmp]   _BaseColor_Step={GetF(m, "_BaseColor_Step")} _BaseShade_Feather={GetF(m, "_BaseShade_Feather")} " +
                    $"_1st_ShadeColor_Step={GetF(m, "_1st_ShadeColor_Step")} _1st_ShadeColor_Feather={GetF(m, "_1st_ShadeColor_Feather")} " +
                    $"_1st_ShadeColor={GetC(m, "_1st_ShadeColor")} _CullMode={GetF(m, "_CullMode")}");
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[HairCmp] log failed: {ex.Message}"); }
    }

    private static string GetF(Material m, string p) => m.HasProperty(p) ? m.GetFloat(p).ToString("0.###") : "-";
    private static string GetC(Material m, string p) => m.HasProperty(p) ? m.GetColor(p).ToString() : "-";
    private static string GetT(Material m, string p) => m.HasProperty(p) ? (m.GetTexture(p) != null ? m.GetTexture(p).name : "null") : "-";

    private static Material FindVanillaHairMaterial(Transform head, PlayerAvatarObjectReferencer paor, HairAsset asset)
    {
        LogVanillaHairRenderers(head);

        // Pass 0 (best): the vanilla renderer that uses the same mesh we were exported from.
        if (asset != null && asset.Mesh != null)
        {
            int targetVerts = asset.Mesh.vertexCount;
            SkinnedMeshRenderer best = null;
            int bestDiff = int.MaxValue;

            foreach (var r in head.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.gameObject.name == "CustomHairMesh") continue;
                if (Current != null && r.transform.IsChildOf(Current.transform)) continue;
                var smr = r.TryCast<SkinnedMeshRenderer>();
                if (smr == null || smr.sharedMesh == null) continue;
                int diff = Math.Abs(smr.sharedMesh.vertexCount - targetVerts);
                if (diff < bestDiff) { bestDiff = diff; best = smr; }
            }

            int tolerance = Math.Max(16, targetVerts / 20); // 5%
            if (best != null && bestDiff <= tolerance && best.sharedMaterial != null)
            {
                Plugin.Log.LogInfo($"[Hair] Template material from renderer '{best.gameObject.name}' (geometry match, {best.sharedMesh.vertexCount} verts)");
                return best.sharedMaterial;
            }
        }

        // Pass 1: by renderer name
        foreach (var r in head.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || r.TryCast<SkinnedMeshRenderer>() == null) continue;
            if (Current != null && r.transform.IsChildOf(Current.transform)) continue;
            string n = r.gameObject.name.ToLower();
            if (n.Contains("body") || n.Contains("skin") || n.Contains("face") || n.Contains("eye")) continue;
            if (n.Contains("hair") || n.Contains("sakura"))
            {
                Plugin.Log.LogInfo($"[Hair] Template material from renderer '{r.gameObject.name}' (name match)");
                return r.sharedMaterial;
            }
        }

        // Pass 2: by shader. Vanilla hair uses Toon_ShadingGradeMap, body/face don't.
        foreach (var r in head.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            if (Current != null && r.transform.IsChildOf(Current.transform)) continue;
            var sm = r.sharedMaterial;
            if (sm == null || sm.shader == null) continue;
            string n = r.gameObject.name.ToLower();
            if (n.Contains("body") || n.Contains("skin") || n.Contains("face") || n.Contains("eye")) continue;
            if (sm.shader.name.Contains("ShadingGradeMap"))
            {
                Plugin.Log.LogInfo($"[Hair] Template material from renderer '{r.gameObject.name}' (shader match)");
                return sm;
            }
        }

        // Fallback: body material
        if (paor != null && paor.bodyMeshRenderer != null)
        {
            Plugin.Log.LogWarning("[Hair] No vanilla hair template found, falling back to BODY material.");
            return paor.bodyMeshRenderer.sharedMaterial;
        }
        return null;
    }

    private static void HideVanillaHair(Transform head)
    {
        _head = head;

        foreach (var r in head.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            if (Current != null && r.transform.IsChildOf(Current.transform)) continue;

            string n = r.gameObject.name.ToLower();
            if (!(n.Contains("sakura") || n.Contains("hair"))) continue;
            if (n.Contains("body") || n.Contains("skin") || n.Contains("face") || n.Contains("eye")) continue;

            if (r.enabled)
            {
                if (!_disabledVanilla.Contains(r)) _disabledVanilla.Add(r);
                r.enabled = false;
            }
        }
    }

    private static PlayerAvatarObjectReferencer GetPaor(Transform root)
    {
        try
        {
            return root.GetComponent<PlayerAvatarObjectReferencer>()
                ?? root.GetComponentInChildren<PlayerAvatarObjectReferencer>(true);
        }
        catch { return null; }
    }
}