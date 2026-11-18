using System;
using UnityEngine;

namespace MyFirstPlugin.Core;

public static class Il2CppHelpers
{
    public static Transform FindBoneByName(Transform root, string nameLower)
    {
        if (root == null) return null;
        if (root.name.ToLowerInvariant().Contains(nameLower)) return root;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindBoneByName(root.GetChild(i), nameLower);
            if (found != null) return found;
        }
        return null;
    }

    public static Transform FindHeadBone(Transform playerRoot)
    {
        if (playerRoot == null) return null;

        try
        {
            Animator animator = playerRoot.GetComponent<Animator>();
            if (animator == null) animator = playerRoot.GetComponentInChildren<Animator>();

            if (animator != null)
            {
                Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
                if (head != null) return head;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Animator lookup failed: {ex.Message}");
        }

        return FindBoneByName(playerRoot, "head");
    }

    public static void DumpHierarchy(Transform t, int depth = 0, int maxDepth = 6)
    {
        if (t == null || depth > maxDepth) return;
        Plugin.Log.LogInfo($"{new string(' ', depth * 2)}- {t.name}");
        for (int i = 0; i < t.childCount; i++)
            DumpHierarchy(t.GetChild(i), depth + 1, maxDepth);
    }
}