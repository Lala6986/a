using System.Collections.Generic;
using System.Text.Json;

namespace MyFirstPlugin.Paint;

public class PaintManifest
{
    public int id { get; set; } = 0;
    public string name { get; set; } = "Custom Paint";
    public string description { get; set; } = "Runtime-loaded body paint.";
    public int price { get; set; } = 0;

    /// <summary>"paint" (texture tattoo) or "effect" (material-property modifier).</summary>
    public string type { get; set; } = "paint";

    // ───────────── paint fields (type == "paint") ─────────────
    /// <summary>
    /// Which vanilla paint this one is based on. Determines the material/shader/effect:
    /// BodyOil (oily skin), TattooDark, TattooFlower1, TattooFlower2, TattooLotus,
    /// TattooSatanism, TattooSlave, Inmon, Rakugaki, RubberSuit.
    /// </summary>
    public string baseType { get; set; } = "TattooDark";

    /// <summary>Optional texture file inside the paint folder (body-UV overlay, PNG with alpha).</summary>
    public string texture { get; set; } = "";

    public int r { get; set; } = 255;
    public int g { get; set; } = 255;
    public int b { get; set; } = 255;

    // ───────────── effect fields (type == "effect") ─────────────
    /// <summary>Renderer names to apply to. Matched case-insensitively; exact first, contains-fallback.</summary>
    public string[] targets { get; set; }

    /// <summary>Raw shader property overrides: { "_Glossiness": 0.9, "_SpecColor": [1,0.8,0.55] }</summary>
    public Dictionary<string, JsonElement> properties { get; set; }

    // Optional semantic shortcuts (converted to shader props internally by EffectManager).
    public float? glossiness { get; set; }
    public float? specular { get; set; }
    public int[] specularColor { get; set; }
    public int[] color { get; set; }
    public float? opacity { get; set; }
}