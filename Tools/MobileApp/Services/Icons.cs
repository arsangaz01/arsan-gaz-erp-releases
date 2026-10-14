namespace ArsanGazMobile.Services;

// Google Material Icons (Apache-2.0, see Resources/Licenses/MaterialIcons-LICENSE.txt); values are the font's codepoints.
public static class Icons
{
    public const string Font = "MaterialIcons";

    public const string QrScanner = "\uf206";
    public const string ManageSearch = "\uf02f";
    public const string Dashboard = "\ue871";
    public const string FactCheck = "\uf0c5";
    public const string Route = "\ueacd";
    public const string LocalShipping = "\ue558";
    public const string Handshake = "\uebcb";
    public const string EventNote = "\ue616";
    public const string Campaign = "\uef49";
    public const string Insights = "\uf092";
    public const string SmartToy = "\uf06c";
    public const string Settings = "\ue8b8";
    public const string Power = "\ue8ac";
    public const string PhotoCamera = "\ue412";
    public const string PhotoLibrary = "\ue413";
    public const string Search = "\ue8b6";
    public const string LocationOn = "\ue0c8";
    public const string History = "\ue889";
    public const string Event = "\ue878";
    public const string WaterDrop = "\ue798";
    public const string PropaneTank = "\uec13";
    public const string SystemUpdate = "\ue62a";
    public const string AccountCircle = "\ue853";
    public const string ChevronRight = "\ue5cc";

    public static FontImageSource Source(string glyph, Color color, double size = 24) => new()
    {
        Glyph = glyph,
        FontFamily = Font,
        Color = color,
        Size = size
    };

    public static Label Glyph(string glyph, Color color, double size = 24) => new()
    {
        Text = glyph,
        FontFamily = Font,
        FontSize = size,
        TextColor = color,
        HorizontalTextAlignment = TextAlignment.Center,
        VerticalTextAlignment = TextAlignment.Center
    };
}
