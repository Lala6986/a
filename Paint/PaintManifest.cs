namespace MyFirstPlugin.Paint;

public class PaintManifest
{
    public int id { get; set; } = 0;
    public string name { get; set; } = "Custom Paint";
    public string description { get; set; } = "Runtime-loaded body paint.";
    public int price { get; set; } = 0;

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
}
