using System.Collections.Generic;
using System.Text.Json.Serialization;
using UnityEngine;

namespace MyFirstPlugin.Hair;

public sealed class HairAsset
{
    public int Id;
    public HairManifest Manifest;

    // Geometry
    public Mesh Mesh;
    public bool HasSkeleton;
    public string[] BoneNames;
    public int[] BoneParent;
    public int[][] BoneChildren;
    public int SkeletonRoot;
    public Matrix4x4[] InverseBindMatrices;
    public Matrix4x4[] SourceWorldRest;

    // Material
    public byte[] TextureBytes;             // legacy: embedded in GLB
    public Material BaseMaterial;           // built lazily at spawn

    // Custom textures (from "Textures/" folder next to the .glb)
    public Texture2D BaseTexture;                                 // TextureId == 0
    public Dictionary<int, Texture2D> PatternTextures;            // TextureId == N (N > 0)

    // Physics
    public DynamicSpec Dynamic;

    // ── JSON-deserializable spec types (property names match hair.json) ──

    public sealed class DynamicSpec
    {
        [JsonPropertyName("components")] public Dictionary<string, DynamicComponent> Components { get; set; }
    }

    public sealed class DynamicComponent
    {
        [JsonPropertyName("preset")] public string Preset { get; set; }
        [JsonPropertyName("includeDescendants")] public bool? IncludeDescendants { get; set; }
        [JsonPropertyName("exclude")] public List<string> Exclude { get; set; }
        [JsonPropertyName("colliders")] public List<string> Colliders { get; set; }
        [JsonPropertyName("params")] public DynamicParams Params { get; set; }
        [JsonPropertyName("overrides")] public Dictionary<string, DynamicParams> Overrides { get; set; }
    }

    public sealed class DynamicParams
    {
        [JsonPropertyName("updateRate")] public int? UpdateRate { get; set; }
        [JsonPropertyName("damping")] public float? Damping { get; set; }
        [JsonPropertyName("elasticity")] public float? Elasticity { get; set; }
        [JsonPropertyName("stiffness")] public float? Stiffness { get; set; }
        [JsonPropertyName("inert")] public float? Inert { get; set; }
        [JsonPropertyName("radius")] public float? Radius { get; set; }
        [JsonPropertyName("gravity")] public float[] Gravity { get; set; }
        [JsonPropertyName("force")] public float[] Force { get; set; }
        [JsonPropertyName("freezeAxis")] public string FreezeAxis { get; set; }
        [JsonPropertyName("distanceDisable")] public float? DistanceDisable { get; set; }
        [JsonPropertyName("weight")] public float? Weight { get; set; }
        [JsonPropertyName("endLength")] public float? EndLength { get; set; }
        [JsonPropertyName("endOffset")] public float[] EndOffset { get; set; }
    }
}