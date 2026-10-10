using Android.App;
using Android.Content;
using Android.Speech;

namespace ArsanGazMobile.Services;

public static class SpeechInput
{
    public const int RequestCode = 4711;
    private static TaskCompletionSource<string?>? _pending;

    public static Task<string?> ListenAsync()
    {
        Activity activity = Platform.CurrentActivity ?? throw new InvalidOperationException("Uygulama penceresi hazır değil.");
        _pending?.TrySetResult(null);
        _pending = new TaskCompletionSource<string?>();

        Intent intent = new(RecognizerIntent.ActionRecognizeSpeech);
        intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
        intent.PutExtra(RecognizerIntent.ExtraLanguage, "tr-TR");
        intent.PutExtra(RecognizerIntent.ExtraLanguagePreference, "tr-TR");
        intent.PutExtra(RecognizerIntent.ExtraPrompt, "Komutunuzu söyleyin");
        intent.PutExtra(RecognizerIntent.ExtraMaxResults, 1);
        try
        {
            activity.StartActivityForResult(intent, RequestCode);
        }
        catch (ActivityNotFoundException)
        {
            _pending = null;
            throw new InvalidOperationException("Bu telefonda konuşma tanıma bulunamadı. Google uygulamasını veya 'Google Konuşma Hizmetleri'ni yükleyin.");
        }

        return _pending.Task;
    }

    public static void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != RequestCode || _pending is null)
        {
            return;
        }

        string? text = resultCode == Result.Ok
            ? data?.GetStringArrayListExtra(RecognizerIntent.ExtraResults)?.FirstOrDefault()
            : null;
        _pending.TrySetResult(text);
        _pending = null;
    }
}
