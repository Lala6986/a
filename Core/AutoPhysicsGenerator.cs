using System;
using System.Collections.Generic;
using MyFirstPlugin.Hair;
using UnityEngine;

namespace MyFirstPlugin.Core;

public static class AutoPhysicsGenerator
{
    public static HairAsset.DynamicSpec Generate(HairAsset asset)
    {
        int n = asset.BoneNames.Length;
        if (n == 0 || asset.BoneParent == null) return null;

        int rootIdx = -1;
        for (int i = 0; i < n; i++)
        {
            if (asset.BoneParent[i] == -1 || IsStrictBodyBone(asset.BoneNames[i]))
            {
                rootIdx = i;
                break;
            }
        }
        if (rootIdx == -1) rootIdx = 0;

        var leaves = new List<int>();
        for (int i = 0; i < n; i++)
        {
            bool isLeaf = true;
            for (int j = 0; j < n; j++)
            {
                if (asset.BoneParent[j] == i) { isLeaf = false; break; }
            }
            if (isLeaf && i != rootIdx) leaves.Add(i);
        }

        var chainRoots = new HashSet<int>();
        foreach (int leaf in leaves)
        {
            int curr = leaf;
            int parent = asset.BoneParent[curr];

            while (parent != -1 && parent != rootIdx)
            {
                int childCount = 0;
                for (int j = 0; j < n; j++)
                    if (asset.BoneParent[j] == parent) childCount++;

                if (childCount > 1) break;
                curr = parent;
                parent = asset.BoneParent[curr];
            }
            chainRoots.Add(curr);
        }

        if (chainRoots.Count == 0) return null;

        var spec = new HairAsset.DynamicSpec { Components = new Dictionary<string, HairAsset.DynamicComponent>() };

        foreach (int rIdx in chainRoots)
        {
            string boneName = asset.BoneNames[rIdx];
            if (IsStrictBodyBone(boneName)) continue;

            var comp = new HairAsset.DynamicComponent
            {
                IncludeDescendants = true,
                // Added Spine and Hips so long hair sweeps around the torso!
                Colliders = new List<string> { "Head", "Chest", "Spine", "Hips" },
                Params = new HairAsset.DynamicParams
                {
                    Stiffness = 1f,
                    Damping = 1f,
                    Elasticity = 1f,
                    Inert = 1f,
                    Gravity = new float[] { 0, 0.01f, 0 },
                    FreezeAxis = ""
                }
            };

            spec.Components[boneName] = comp;
            Plugin.Log.LogInfo($"[AutoPhys] Auto-detected chain root: {boneName} (defaulted to 1)");
        }

        return spec.Components.Count > 0 ? spec : null;
    }

    private static bool IsStrictBodyBone(string name)
    {
        string n = name.ToLowerInvariant();
        return n == "armature" || n == "root" || n == "hips" || n == "spine" ||
               n == "chest" || n == "neck" || n == "head" || n.StartsWith("shoulder") ||
               n.StartsWith("arm") || n.StartsWith("leg") || n.StartsWith("hand") || n.StartsWith("foot");
    }
}