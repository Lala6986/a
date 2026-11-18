using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MyFirstPlugin.Core;

public static class BindposeUtils
{
    /// <summary>
    /// Compute target inverse-bind matrices for a custom skinned mesh that will be
    /// driven by the game's skeleton.
    /// 3-tier resolution:
    ///   1) bones matched to body bindposes (perfect binding)
    ///   2) children of resolved bones — propagate using source world-rest deltas
    ///   3) fallback — use current world transform of the mapped bone
    /// </summary>
    public static Matrix4x4[] BuildUnifiedTargetInverseBinds(
        SkinnedMeshRenderer targetSmr,
        Transform[] mappedBones,
        SkinnedMeshRenderer bodySmr,
        int[] boneParent,
        Matrix4x4[] sourceIBM,
        Matrix4x4[] sourceWorldRestOptional)
    {
        int n = mappedBones?.Length ?? 0;
        var result = new Matrix4x4[n];
        if (n == 0) return result;

        Matrix4x4 targetWorld = targetSmr != null
            ? targetSmr.transform.localToWorldMatrix
            : Matrix4x4.identity;

        // Body bindpose lookup
        Dictionary<Transform, int> bodyBoneIndex = null;
        Matrix4x4[] bodyBindposes = null;
        Matrix4x4 bodyWorld = Matrix4x4.identity;
        if (bodySmr != null && bodySmr.bones != null && bodySmr.sharedMesh != null
            && bodySmr.sharedMesh.bindposes != null)
        {
            var bb = bodySmr.bones.ToArray();
            bodyBoneIndex = new Dictionary<Transform, int>(bb.Length);
            for (int i = 0; i < bb.Length; i++)
                if (bb[i] != null) bodyBoneIndex[bb[i]] = i;
            bodyBindposes = bodySmr.sharedMesh.bindposes.ToArray();
            bodyWorld = bodySmr.transform.localToWorldMatrix;
        }

        // Source world rest: prefer explicit, else derive from IBM
        Matrix4x4[] sourceWorldRest = null;
        if (sourceWorldRestOptional != null && sourceWorldRestOptional.Length == n)
            sourceWorldRest = sourceWorldRestOptional;
        else if (sourceIBM != null && sourceIBM.Length == n)
        {
            sourceWorldRest = new Matrix4x4[n];
            for (int i = 0; i < n; i++)
                sourceWorldRest[i] = sourceIBM[i].inverse;
        }

        var worldRest = new Matrix4x4[n];
        var resolved = new bool[n];

        // Pass 1: bones matched to body bindposes
        if (bodyBoneIndex != null && bodyBindposes != null)
        {
            for (int i = 0; i < n; i++)
            {
                var mapped = mappedBones[i];
                if (mapped != null && bodyBoneIndex.TryGetValue(mapped, out var bodyIdx)
                    && bodyIdx >= 0 && bodyIdx < bodyBindposes.Length)
                {
                    worldRest[i] = bodyWorld * bodyBindposes[bodyIdx].inverse;
                    resolved[i] = true;
                }
            }
        }

        // Pass 2: propagate from resolved parents
        if (sourceWorldRest != null && boneParent != null && boneParent.Length == n)
        {
            for (int iteration = 0; iteration < n; iteration++)
            {
                bool progress = false;
                for (int i = 0; i < n; i++)
                {
                    if (resolved[i]) continue;
                    int p = (i >= 0 && i < boneParent.Length) ? boneParent[i] : -1;
                    if (p >= 0 && p < n && resolved[p])
                    {
                        Matrix4x4 delta = sourceWorldRest[p].inverse * sourceWorldRest[i];
                        worldRest[i] = worldRest[p] * delta;
                        resolved[i] = true;
                        progress = true;
                    }
                }
                if (!progress) break;
            }
        }

        // Pass 3: fallback to current world transform
        for (int i = 0; i < n; i++)
        {
            if (resolved[i]) continue;
            var mapped = mappedBones[i];
            worldRest[i] = mapped != null ? mapped.localToWorldMatrix : Matrix4x4.identity;
        }

        // Final: IBM = worldRest^-1 * targetWorld
        for (int i = 0; i < n; i++)
            result[i] = worldRest[i].inverse * targetWorld;

        return result;
    }

    /// <summary>
    /// Corrections[j] = U[j].inverse * sourceIBM[j]. When applied per-vertex (blended
    /// by bone weight), the mesh is warped from GLB bind space into the game rig's bind space.
    /// </summary>
    public static Matrix4x4[] CompleteOrFallbackCorrections(Matrix4x4[] U, Matrix4x4[] sourceIBM)
    {
        int n = U?.Length ?? 0;
        var result = new Matrix4x4[n];
        if (sourceIBM == null || sourceIBM.Length != n)
        {
            for (int i = 0; i < n; i++) result[i] = Matrix4x4.identity;
            Plugin.Log.LogWarning("[Bindpose] Missing or mismatched IBMs — corrections = identity.");
            return result;
        }
        for (int i = 0; i < n; i++)
            result[i] = U[i].inverse * sourceIBM[i];
        return result;
    }

    /// <summary>
    /// Detects if the armature has a second "control" chain (PlayerBoneScaleAdjuster).
    /// If yes, the visual mesh is bound to a scaled copy, not the animation bones.
    /// </summary>
    public static bool DetectControlParents(Transform armatureRoot)
    {
        if (armatureRoot == null) return false;

        string[] patterns =
        {
            "PlayerBoneScaleAdjuster", "BreastRotateL", "BreastRotateR",
            "BreastRotate_L", "BreastRotate_R", "CTRL_", "Control_",
            "RigHelper", "ScaleAdjuster"
        };

        var q = new Queue<Transform>();
        q.Enqueue(armatureRoot);
        while (q.Count > 0)
        {
            var t = q.Dequeue();
            if (t == null) continue;
            string name = t.name;
            foreach (var p in patterns)
                if (name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            for (int i = 0; i < t.childCount; i++)
                q.Enqueue(t.GetChild(i));
        }
        return false;
    }

    /// <summary>Does the transform have any non-identity local component?</summary>
    public static bool HasNonIdentity(Transform t)
    {
        if (t == null) return false;
        bool rot = Vector3.Distance(t.localRotation.eulerAngles, Vector3.zero) > 0.01f;
        bool pos = t.localPosition.sqrMagnitude > 1e-6f;
        bool scale = Mathf.Abs(t.localScale.x - 1f) > 1e-4f
                  || Mathf.Abs(t.localScale.y - 1f) > 1e-4f
                  || Mathf.Abs(t.localScale.z - 1f) > 1e-4f;
        return rot || pos || scale;
    }

    /// <summary>
    /// Zero out invalid bone indices (bones that didn't resolve to a real transform),
    /// then renormalize the surviving weights.
    /// </summary>
    public static void SanitizeWeights(Mesh mesh, Transform[] mappedBones)
    {
        if (mesh == null) return;

        var bw = mesh.boneWeights;
        if (bw == null || bw.Length == 0) return;

        int boneCount = mappedBones?.Length ?? 0;

        for (int i = 0; i < bw.Length; i++)
        {
            var w = bw[i];

            if (!IsValidBone(w.boneIndex0, boneCount, mappedBones)) w.weight0 = 0f;
            if (!IsValidBone(w.boneIndex1, boneCount, mappedBones)) w.weight1 = 0f;
            if (!IsValidBone(w.boneIndex2, boneCount, mappedBones)) w.weight2 = 0f;
            if (!IsValidBone(w.boneIndex3, boneCount, mappedBones)) w.weight3 = 0f;

            float sum = w.weight0 + w.weight1 + w.weight2 + w.weight3;
            if (sum > 1e-6f)
            {
                float inv = 1f / sum;
                w.weight0 *= inv;
                w.weight1 *= inv;
                w.weight2 *= inv;
                w.weight3 *= inv;
            }
            bw[i] = w;
        }

        mesh.boneWeights = bw;
    }

    public static bool IsValidBone(int index, int boneCount, Transform[] mappedBones)
    {
        if (index < 0 || index >= boneCount) return false;
        return mappedBones[index] != null;
    }

    /// <summary>
    /// Warp mesh vertices/normals/tangents from GLB bind space into game bind space.
    /// For each vertex, the correction matrices of its bound bones are weighted and summed,
    /// producing a single blend matrix. That matrix is applied to the vertex.
    /// </summary>
    public static void BakeIntoTargetBindSpace(Mesh mesh, Matrix4x4[] C)
    {
        if (mesh == null) return;

        var verts = mesh.vertices;
        var normals = mesh.normals;
        var tangents = mesh.tangents;
        var bw = mesh.boneWeights;

        if (bw == null || bw.Length != mesh.vertexCount)
        {
            Plugin.Log.LogWarning("[Bake] Mesh has no boneWeights; skipping bake.");
            return;
        }

        bool hasNormals = normals != null && normals.Length == verts.Length;
        bool hasTangents = tangents != null && tangents.Length == verts.Length;

        for (int i = 0; i < verts.Length; i++)
        {
            var w = bw[i];
            Matrix4x4 blend = Matrix4x4.zero;

            if (w.weight0 > 0f) blend = MeshRetargeter.Add(blend, MeshRetargeter.Mul(C[w.boneIndex0], w.weight0));
            if (w.weight1 > 0f) blend = MeshRetargeter.Add(blend, MeshRetargeter.Mul(C[w.boneIndex1], w.weight1));
            if (w.weight2 > 0f) blend = MeshRetargeter.Add(blend, MeshRetargeter.Mul(C[w.boneIndex2], w.weight2));
            if (w.weight3 > 0f) blend = MeshRetargeter.Add(blend, MeshRetargeter.Mul(C[w.boneIndex3], w.weight3));

            Vector3 v = verts[i];
            verts[i] = blend.MultiplyPoint3x4(v);

            if (hasNormals)
            {
                Vector3 nrm = MeshRetargeter.MultiplyVector3x3(blend, normals[i]);
                nrm.Normalize();
                normals[i] = nrm;
            }
            if (hasTangents)
            {
                Vector4 t = tangents[i];
                Vector3 tv = new Vector3(t.x, t.y, t.z);
                tv = MeshRetargeter.MultiplyVector3x3(blend, tv);
                tv.Normalize();
                tangents[i] = new Vector4(tv.x, tv.y, tv.z, t.w);
            }
        }

        mesh.vertices = verts;
        if (hasNormals) mesh.normals = normals;
        if (hasTangents) mesh.tangents = tangents;
        mesh.RecalculateBounds();
    }
}