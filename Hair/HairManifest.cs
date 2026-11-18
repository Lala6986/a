using System.Text.Json.Serialization;

namespace MyFirstPlugin.Hair;

public class HairManifest
{
    public int id { get; set; } = 0;
    public string name { get; set; } = "Custom GLB Hair";
    public string description { get; set; } = "A runtime-loaded GLB hairstyle.";
    public int price { get; set; } = 150;
    public string glbFileName { get; set; } = "hair.glb";

    public float scale { get; set; } = 1.0f;
    public float offsetX { get; set; } = 0f;
    public float offsetY { get; set; } = 0f;
    public float offsetZ { get; set; } = 0f;
    public float rotX { get; set; } = 0f;
    public float rotY { get; set; } = 0f;
    public float rotZ { get; set; } = 0f;

    [JsonPropertyName("dynamic")]
    public HairAsset.DynamicSpec Dynamic { get; set; }
}