using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyFirstPlugin.Hair;
using UnityEngine;
using UnityEngine.Rendering;

namespace MyFirstPlugin.Core;

public static class GlbLoader
{
    private static readonly Matrix4x4 Sx = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));

    public static HairAsset Parse(byte[] glbBytes, string filePath, HairManifest manifest)
    {
        try
        {
            using var ms = new MemoryStream(glbBytes);
            using var reader = new BinaryReader(ms);

            if (reader.ReadInt32() != 0x46546C67) { Plugin.Log.LogError($"[GLB] Not a GLB: {filePath}"); return null; }
            if (reader.ReadInt32() != 2) { Plugin.Log.LogError($"[GLB] Unsupported version: {filePath}"); return null; }
            reader.ReadInt32(); // total length

            int jsonLen = reader.ReadInt32(); reader.ReadInt32();
            byte[] jsonBytes = reader.ReadBytes(jsonLen);
            string json = Encoding.UTF8.GetString(jsonBytes).TrimEnd('\0', ' ');

            int binLen = reader.ReadInt32(); reader.ReadInt32();
            byte[] bin = reader.ReadBytes(binLen);

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var gltf = JsonSerializer.Deserialize<GltfRoot>(json, opts);
            if (gltf == null || gltf.meshes == null || gltf.meshes.Count == 0)
            { Plugin.Log.LogError($"[GLB] No meshes in {filePath}"); return null; }

            // Texture bytes (deferred material build)
            byte[] texBytes = ExtractFirstTexture(gltf, bin);

            // Skeleton
            bool hasSkin = gltf.skins != null && gltf.skins.Count > 0;
            string[] boneNames = null;
            int[] boneParents = null;
            int[][] boneChildren = null;
            Matrix4x4[] ibms = null;
            Matrix4x4[] worldRest = null;
            int skelRoot = 0;

            if (hasSkin)
                ExtractSkinData(gltf, bin, gltf.skins[0], out boneNames, out boneParents,
                                out boneChildren, out ibms, out worldRest, out skelRoot);

            // Mesh
            var mesh = BuildMergedMesh(gltf, bin, hasSkin, out bool meshHasWeights);
            if (mesh == null) { Plugin.Log.LogError($"[GLB] Mesh build failed: {filePath}"); return null; }

            // ═══ PREVENT UNITY FROM UNLOADING THE MESH ON SCENE TRANSITION ═══
            mesh.name = $"Hair_{manifest.id}_Mesh";
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset | HideFlags.DontSaveInBuild;
            try { UnityEngine.Object.DontDestroyOnLoad(mesh); } catch { }

            if (hasSkin && !meshHasWeights)
            {
                Plugin.Log.LogWarning($"[GLB] Skin present but no weights: {filePath}. Treating as rigid.");
                hasSkin = false;
            }

            return new HairAsset
            {
                Id = manifest.id,
                Manifest = manifest,
                Mesh = mesh,
                HasSkeleton = hasSkin,
                BoneNames = boneNames,
                BoneParent = boneParents,
                BoneChildren = boneChildren,
                SkeletonRoot = skelRoot,
                InverseBindMatrices = ibms,
                SourceWorldRest = worldRest,
                TextureBytes = texBytes,
                Dynamic = manifest.Dynamic
            };
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[GLB] Parse exception for {filePath}: {ex}");
            return null;
        }
    }

    // ── helpers ────────────────────────────────────────────────

    private static byte[] ExtractFirstTexture(GltfRoot gltf, byte[] bin)
    {
        if (gltf.images == null || gltf.images.Count == 0) return null;
        var img = gltf.images[0];
        if (img.bufferView < 0 || img.bufferView >= gltf.bufferViews.Count) return null;
        var bv = gltf.bufferViews[img.bufferView];
        if (bv.byteLength <= 0 || bv.byteOffset + bv.byteLength > bin.Length) return null;
        var out_ = new byte[bv.byteLength];
        Array.Copy(bin, bv.byteOffset, out_, 0, bv.byteLength);
        return out_;
    }

    private static void ExtractSkinData(
        GltfRoot gltf, byte[] bin, GltfSkin skin,
        out string[] names, out int[] parents, out int[][] children,
        out Matrix4x4[] ibms, out Matrix4x4[] worldRest, out int root)
    {
        int n = skin.joints.Length;
        names = new string[n];
        parents = new int[n];
        ibms = new Matrix4x4[n];
        worldRest = new Matrix4x4[n];

        var nodeToSkin = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) nodeToSkin[skin.joints[i]] = i;

        var nodeParent = new Dictionary<int, int>();
        for (int i = 0; i < gltf.nodes.Count; i++)
        {
            if (gltf.nodes[i].children == null) continue;
            foreach (var c in gltf.nodes[i].children) nodeParent[c] = i;
        }

        for (int i = 0; i < n; i++)
        {
            int nodeIdx = skin.joints[i];
            names[i] = string.IsNullOrEmpty(gltf.nodes[nodeIdx].name) ? $"Bone_{i}" : gltf.nodes[nodeIdx].name;
            parents[i] = (nodeParent.TryGetValue(nodeIdx, out var pn) && nodeToSkin.TryGetValue(pn, out var ps)) ? ps : -1;
        }

        // Local transforms with X-mirror to Unity space
        var nodeLocal = new Matrix4x4[gltf.nodes.Count];
        for (int i = 0; i < gltf.nodes.Count; i++)
        {
            var nd = gltf.nodes[i];
            Vector3 t = nd.translation != null ? new Vector3(nd.translation[0], nd.translation[1], nd.translation[2]) : Vector3.zero;
            Quaternion r = nd.rotation != null ? new Quaternion(nd.rotation[0], nd.rotation[1], nd.rotation[2], nd.rotation[3]) : Quaternion.identity;
            Vector3 s = nd.scale != null ? new Vector3(nd.scale[0], nd.scale[1], nd.scale[2]) : Vector3.one;
            nodeLocal[i] = Sx * Matrix4x4.TRS(t, r, s) * Sx;
        }

        var nodeWorld = new Matrix4x4[gltf.nodes.Count];
        for (int i = 0; i < gltf.nodes.Count; i++)
        {
            var world = nodeLocal[i];
            int cur = i;
            while (nodeParent.TryGetValue(cur, out var p)) { world = nodeLocal[p] * world; cur = p; }
            nodeWorld[i] = world;
        }

        for (int i = 0; i < n; i++) worldRest[i] = nodeWorld[skin.joints[i]];

        // Inverse bind matrices
        if (skin.inverseBindMatrices >= 0 && skin.inverseBindMatrices < gltf.accessors.Count)
        {
            var acc = gltf.accessors[skin.inverseBindMatrices];
            if (acc.bufferView >= 0 && acc.bufferView < gltf.bufferViews.Count)
            {
                var bv = gltf.bufferViews[acc.bufferView];
                int off = bv.byteOffset + acc.byteOffset;
                for (int i = 0; i < n; i++)
                {
                    float[] m = new float[16];
                    for (int j = 0; j < 16; j++) m[j] = BitConverter.ToSingle(bin, off + (i * 16 + j) * 4);
                    var mat = new Matrix4x4();
                    mat.m00 = m[0]; mat.m01 = -m[4]; mat.m02 = -m[8]; mat.m03 = -m[12];
                    mat.m10 = -m[1]; mat.m11 = m[5]; mat.m12 = m[9]; mat.m13 = m[13];
                    mat.m20 = -m[2]; mat.m21 = m[6]; mat.m22 = m[10]; mat.m23 = m[14];
                    mat.m30 = -m[3]; mat.m31 = m[7]; mat.m32 = m[11]; mat.m33 = m[15];
                    ibms[i] = mat;
                }
            }
        }
        else
        {
            for (int i = 0; i < n; i++) ibms[i] = worldRest[i].inverse;
        }

        root = 0;
        for (int i = 0; i < n; i++) if (parents[i] == -1) { root = i; break; }

        var childrenLists = new List<int>[n];
        for (int i = 0; i < n; i++) childrenLists[i] = new List<int>();
        for (int i = 0; i < n; i++) if (parents[i] >= 0) childrenLists[parents[i]].Add(i);
        children = new int[n][];
        for (int i = 0; i < n; i++) children[i] = childrenLists[i].ToArray();
    }

    private static Mesh BuildMergedMesh(GltfRoot gltf, byte[] bin, bool hasSkin, out bool hasWeights)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        var weights = new List<BoneWeight>();
        hasWeights = false;

        foreach (var meshDef in gltf.meshes)
        {
            foreach (var prim in meshDef.primitives)
            {
                if (prim.attributes.POSITION == -1) continue;

                var posAcc = gltf.accessors[prim.attributes.POSITION];
                var posBv = gltf.bufferViews[posAcc.bufferView];
                int posStride = posBv.byteStride > 0 ? posBv.byteStride : 12;
                var pv = ReadVec3Array(bin, posBv.byteOffset + posAcc.byteOffset, posAcc.count, posStride);
                int baseIdx = verts.Count;
                verts.AddRange(pv);

                // Normals
                if (prim.attributes.NORMAL != -1)
                {
                    var a = gltf.accessors[prim.attributes.NORMAL];
                    var bv = gltf.bufferViews[a.bufferView];
                    int s = bv.byteStride > 0 ? bv.byteStride : 12;
                    normals.AddRange(ReadVec3Array(bin, bv.byteOffset + a.byteOffset, a.count, s));
                }
                else for (int i = 0; i < pv.Length; i++) normals.Add(Vector3.up);

                // UVs
                if (prim.attributes.TEXCOORD_0 != -1)
                {
                    var a = gltf.accessors[prim.attributes.TEXCOORD_0];
                    var bv = gltf.bufferViews[a.bufferView];
                    int s = bv.byteStride > 0 ? bv.byteStride : 8;
                    uvs.AddRange(ReadVec2Array(bin, bv.byteOffset + a.byteOffset, a.count, s));
                }
                else for (int i = 0; i < pv.Length; i++) uvs.Add(Vector2.zero);

                // Indices
                if (prim.indices != -1)
                {
                    var a = gltf.accessors[prim.indices];
                    var bv = gltf.bufferViews[a.bufferView];
                    var idx = ReadIntArray(bin, bv.byteOffset + a.byteOffset, a.count, a.componentType);
                    for (int i = 0; i + 2 < idx.Length; i += 3)
                    { int t = idx[i]; idx[i] = idx[i + 2]; idx[i + 2] = t; }
                    for (int i = 0; i < idx.Length; i++) tris.Add(idx[i] + baseIdx);
                }

                // Weights
                if (hasSkin && prim.attributes.JOINTS_0 != -1 && prim.attributes.WEIGHTS_0 != -1)
                {
                    var jAcc = gltf.accessors[prim.attributes.JOINTS_0];
                    var wAcc = gltf.accessors[prim.attributes.WEIGHTS_0];
                    var jBv = gltf.bufferViews[jAcc.bufferView];
                    var wBv = gltf.bufferViews[wAcc.bufferView];

                    // JOINTS_0: 4 components. 5121=UBYTE(4), 5123=USHORT(8), 5125=UINT(16)
                    int jStride = jBv.byteStride > 0 ? jBv.byteStride :
                        (jAcc.componentType == 5125 ? 16 : (jAcc.componentType == 5121 ? 4 : 8));

                    // WEIGHTS_0: 4 components. 5121=UBYTE(4), 5123=USHORT(8), 5126=FLOAT(16)
                    int wStride = wBv.byteStride > 0 ? wBv.byteStride :
                        (wAcc.componentType == 5126 ? 16 : (wAcc.componentType == 5123 ? 8 : 4));

                    var joints = ReadJoints(bin, jBv.byteOffset + jAcc.byteOffset, jAcc.count, jAcc.componentType, jStride);
                    var ws = ReadWeights(bin, wBv.byteOffset + wAcc.byteOffset, wAcc.count, wAcc.componentType, wStride);

                    int count = Math.Min(pv.Length, jAcc.count);
                    for (int i = 0; i < count; i++)
                    {
                        float w0 = ws[i * 4 + 0], w1 = ws[i * 4 + 1], w2 = ws[i * 4 + 2], w3 = ws[i * 4 + 3];
                        float sum = w0 + w1 + w2 + w3;
                        if (sum > 1e-4f) { w0 /= sum; w1 /= sum; w2 /= sum; w3 /= sum; }
                        else { w0 = 1; w1 = w2 = w3 = 0; }

                        weights.Add(new BoneWeight
                        {
                            boneIndex0 = joints[i * 4 + 0],
                            boneIndex1 = joints[i * 4 + 1],
                            boneIndex2 = joints[i * 4 + 2],
                            boneIndex3 = joints[i * 4 + 3],
                            weight0 = w0,
                            weight1 = w1,
                            weight2 = w2,
                            weight3 = w3
                        });
                    }
                    hasWeights = true;
                }
                else
                {
                    for (int i = 0; i < pv.Length; i++)
                        weights.Add(new BoneWeight { boneIndex0 = 0, weight0 = 1 });
                }
            }
        }

        if (verts.Count == 0) return null;

        var mesh = new Mesh { name = "CustomHairMesh" };
        if (verts.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = verts.ToArray();
        mesh.normals = normals.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.boneWeights = weights.ToArray();
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }

    private static Vector3[] ReadVec3Array(byte[] d, int off, int count, int stride)
    {
        var r = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            int o = off + i * stride;
            r[i] = new Vector3(-BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8));
        }
        return r;
    }

    private static Vector2[] ReadVec2Array(byte[] d, int off, int count, int stride)
    {
        var r = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            int o = off + i * stride;
            r[i] = new Vector2(BitConverter.ToSingle(d, o), 1f - BitConverter.ToSingle(d, o + 4));
        }
        return r;
    }

    private static int[] ReadIntArray(byte[] d, int off, int count, int ct, int stride = -1)
    {
        int elem = ct == 5125 ? 4 : (ct == 5121 ? 1 : 2);
        int s = stride > 0 ? stride : elem;
        var r = new int[count];
        for (int i = 0; i < count; i++)
        {
            int o = off + i * s;
            if (elem == 4) r[i] = (int)BitConverter.ToUInt32(d, o);
            else if (elem == 1) r[i] = d[o];
            else r[i] = BitConverter.ToUInt16(d, o);
        }
        return r;
    }

    private static int[] ReadJoints(byte[] d, int off, int count, int ct, int stride)
    {
        var r = new int[count * 4];
        int elem = ct == 5125 ? 4 : (ct == 5121 ? 1 : 2);
        int s = stride > 0 ? stride : elem * 4;
        for (int i = 0; i < count; i++)
        {
            int vo = off + i * s;
            for (int j = 0; j < 4; j++)
            {
                int o = vo + j * elem;
                if (elem == 4) r[i * 4 + j] = (int)BitConverter.ToUInt32(d, o);
                else if (elem == 1) r[i * 4 + j] = d[o];
                else r[i * 4 + j] = BitConverter.ToUInt16(d, o);
            }
        }
        return r;
    }

    private static float[] ReadWeights(byte[] d, int off, int count, int ct, int stride)
    {
        var r = new float[count * 4];
        int elem = ct == 5126 ? 4 : (ct == 5121 ? 1 : 2);
        int s = stride > 0 ? stride : elem * 4;
        for (int i = 0; i < count; i++)
        {
            int vo = off + i * s;
            for (int j = 0; j < 4; j++)
            {
                int o = vo + j * elem;
                if (ct == 5126) r[i * 4 + j] = BitConverter.ToSingle(d, o);
                else if (ct == 5123) r[i * 4 + j] = BitConverter.ToUInt16(d, o) / 65535f;
                else r[i * 4 + j] = d[o] / 255f;
            }
        }
        return r;
    }
}

// ── glTF POCOs (kept from your existing GlbLoader) ──

public class GltfRoot
{
    public List<GltfMesh> meshes { get; set; }
    public List<GltfAccessor> accessors { get; set; }
    public List<GltfBufferView> bufferViews { get; set; }
    public List<GltfImage> images { get; set; }
    public List<GltfNode> nodes { get; set; }
    public List<GltfSkin> skins { get; set; }
}
public class GltfMesh { public List<GltfPrimitive> primitives { get; set; } }
public class GltfPrimitive { public GltfAttributes attributes { get; set; } public int indices { get; set; } = -1; }
public class GltfAttributes
{
    public int POSITION { get; set; } = -1;
    public int NORMAL { get; set; } = -1;
    public int TEXCOORD_0 { get; set; } = -1;
    public int JOINTS_0 { get; set; } = -1;
    public int WEIGHTS_0 { get; set; } = -1;
}
public class GltfAccessor { public int bufferView { get; set; } public int byteOffset { get; set; } public int count { get; set; } public int componentType { get; set; } }
public class GltfBufferView { public int buffer { get; set; } public int byteOffset { get; set; } public int byteLength { get; set; } public int byteStride { get; set; } }
public class GltfImage { public int bufferView { get; set; } = -1; public string mimeType { get; set; } public string uri { get; set; } }
public class GltfNode { public string name { get; set; } public int[] children { get; set; } public float[] translation { get; set; } public float[] rotation { get; set; } public float[] scale { get; set; } }
public class GltfSkin { public int[] joints { get; set; } public int inverseBindMatrices { get; set; } = -1; }