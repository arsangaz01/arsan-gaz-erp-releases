namespace ArsanGazMobile.Services;

public static class SpeechInput
{
    public const int RequestCode = 4711;

    public static Task<string?> ListenAsync() =>
        throw new InvalidOperationException("iPhone'da konuşarak komut henüz yok. Yazarak deneyin.");

    public static void OnActivityResult(int requestCode, object resultCode, object? data)
    {
    }
}
