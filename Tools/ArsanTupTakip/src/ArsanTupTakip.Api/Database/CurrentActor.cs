namespace ArsanTupTakip;

// The signed-in user of the current request ("depo1 (Mobil)", "yonetici (Panel)"); set once the request is
// authenticated and written into CylinderMovement.PerformedBy when a movement is saved without one.
public static class CurrentActor
{
    private static readonly AsyncLocal<string?> Value = new();

    public static string? Name
    {
        get => Value.Value;
        set => Value.Value = value;
    }
}
