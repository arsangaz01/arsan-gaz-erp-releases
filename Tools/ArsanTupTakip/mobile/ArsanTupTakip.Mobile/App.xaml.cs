using ArsanTupTakip.Mobile.Pages;
using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile;

public partial class App : Application
{
	private static Window? _window;

	public App()
	{
		InitializeComponent();
		ApiClient.SessionEnded += message => ShowLogin(message);
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		Window window = new();
		_window = window;
		string? previous = CrashLog.TakeLast();
		if (previous is null)
		{
			window.Page = Loading();
			MainThread.BeginInvokeOnMainThread(async () => await StartAsync());
		}
		else
		{
			window.Page = CrashLog.ErrorPage("Önceki açılışta hata oluştu", previous, () =>
			{
				window.Page = Loading();
				MainThread.BeginInvokeOnMainThread(async () => await StartAsync());
			});
		}

		return window;
	}

	protected override async void OnResume()
	{
		base.OnResume();
		// The 24-hour login may have run out while the app was in the background.
		if (_window?.Page is not LoginPage && !await ApiClient.Instance.HasSessionAsync())
		{
			ApiClient.Instance.ClearSession();
			ShowLogin("Oturum süresi doldu (giriş 24 saat geçerlidir). Kullanıcı adı ve şifrenizle yeniden giriş yapın.");
			return;
		}

		if (_window?.Page is NavigationPage { RootPage: HomePage home } && !await home.RefreshAccessAsync(force: true))
		{
			await home.RefreshUpdateAsync();
		}
	}

	private static async Task StartAsync()
	{
		try
		{
			await ServerUrlCoordinator.BootstrapAsync();
		}
		catch (ApiException)
		{
		}

		if (!await ApiClient.Instance.HasSessionAsync())
		{
			bool expired = ApiClient.Instance.Account is not null;
			ApiClient.Instance.ClearSession();
			ShowLogin(expired ? "Oturum süresi doldu (giriş 24 saat geçerlidir). Yeniden giriş yapın." : null);
			return;
		}

		try
		{
			// Permissions may have changed in the panel since the last start.
			await ApiClient.Instance.RefreshAccountAsync();
		}
		catch (SessionExpiredException)
		{
			return;
		}
		catch (ApiException)
		{
		}

		ShowMain();
	}

	public static void ShowLogin(string? message)
	{
		if (_window is not null)
		{
			_window.Page = new LoginPage(message);
		}
	}

	// Rebuilds the home tiles for the current user's permissions.
	public static void ShowMain()
	{
		if (_window is null)
		{
			return;
		}

		if (ApiClient.Instance.Account is not AccountInfo account)
		{
			ShowLogin(null);
			return;
		}

		try
		{
			_window.Page = account.MustChangePassword
				? new NavigationPage(new ChangePasswordPage(required: true))
				: new NavigationPage(new HomePage()) { BarBackgroundColor = Ui.Navy, BarTextColor = Colors.White };
		}
		catch (Exception exception)
		{
			CrashLog.Write(exception, "Ana menü");
			_window.Page = CrashLog.ErrorPage("Uygulama açılamadı", exception.ToString());
		}
	}

	public static async Task LogoutAsync()
	{
		await ApiClient.Instance.LogoutAsync();
		ShowLogin("Çıkış yapıldı.");
	}

	private static ContentPage Loading() => new()
	{
		Content = new ActivityIndicator { IsRunning = true, Color = Ui.Accent, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center }
	};
}
