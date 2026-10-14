using ArsanGazMobile.Pages;
using ArsanGazMobile.Services;

namespace ArsanGazMobile;

public partial class App : Application
{
	private Window? _window;

	public App()
	{
		InitializeComponent();
		ApiClient.Instance.SessionEnded += message => MainThread.BeginInvokeOnMainThread(() => ShowLogin(message));
		ApiClient.Instance.PasswordChangeRequired += () => MainThread.BeginInvokeOnMainThread(ShowPasswordChange);
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		_window = new Window(new ContentPage
		{
			Content = new ActivityIndicator { IsRunning = true, Color = Ui.Accent, VerticalOptions = LayoutOptions.Center }
		});
		string? previous = CrashLog.TakeLast();
		if (previous is null)
		{
			_ = StartAsync();
		}
		else
		{
			_window.Page = CrashLog.ErrorPage("Önceki açılışta hata oluştu", previous, () => _ = StartAsync());
		}

		return _window;
	}

	protected override async void OnResume()
	{
		base.OnResume();
		if (_window?.Page is AppShell shell)
		{
			await shell.Home.RefreshUpdateAsync();
		}
	}

	// Signed in (token not expired) → tabs; otherwise the sign-in screen. Phones paired before 1.8.0 have a token
	// but no user; the server refuses it, so they land on the sign-in screen as well.
	private async Task StartAsync()
	{
		if (await ApiClient.Instance.IsSignedInAsync())
		{
			ShowSession(ApiClient.Instance.Session!);
			return;
		}

		bool legacy = ApiClient.Instance.Session is null && await SecureStorage.Default.GetAsync("device_token") is { Length: > 0 };
		ShowLogin(legacy
			? "Bu sürümden itibaren telefonlar web paneldeki kullanıcı adı ve şifreyle giriş yapar. Eski eşleştirme kodu artık geçerli değil."
			: ApiClient.Instance.Session is not null ? "Oturum süresi doldu (24 saat). Yeniden giriş yapın." : null);
	}

	internal void ShowLogin(string? message)
	{
		if (_window is null)
		{
			return;
		}

		if (_window.Page is LoginPage)
		{
			return;
		}

		_window.Page = new LoginPage(ShowSession, message);
	}

	private void ShowSession(UserSession session)
	{
		if (_window is null)
		{
			return;
		}

		if (session.MustChangePassword)
		{
			ShowPasswordChange();
			return;
		}

		try
		{
			_window.Page = new AppShell(session);
		}
		catch (Exception exception)
		{
			CrashLog.Write(exception, "AppShell");
			_window.Page = CrashLog.ErrorPage("Uygulama açılamadı", exception.ToString());
		}
	}

	private void ShowPasswordChange()
	{
		if (_window is null || _window.Page is PasswordPage { IsForced: true })
		{
			return;
		}

		_window.Page = new PasswordPage(forced: true,
			done: () =>
			{
				if (ApiClient.Instance.Session is { } session)
				{
					ShowSession(session);
				}

				return Task.CompletedTask;
			},
			logout: async () =>
			{
				await ApiClient.Instance.LogoutAsync();
				ShowLogin(null);
			});
	}
}
