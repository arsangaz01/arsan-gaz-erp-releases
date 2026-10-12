using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		Window window = new();
		string? previous = CrashLog.TakeLast();
		window.Page = previous is null
			? CreateShell()
			: CrashLog.ErrorPage("Önceki açılışta hata oluştu", previous, () => window.Page = CreateShell());
		return window;
	}

	private static Page CreateShell()
	{
		try
		{
			return new AppShell();
		}
		catch (Exception exception)
		{
			CrashLog.Write(exception, "AppShell");
			return CrashLog.ErrorPage("Uygulama açılamadı", exception.ToString());
		}
	}
}
