using Microsoft.Extensions.Logging;
using ZXing.Net.Maui.Controls;

namespace ArsanGazMobile;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		AppDomain.CurrentDomain.UnhandledException += (_, e) => Services.CrashLog.Write(e.ExceptionObject, "UnhandledException");
		TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			Services.CrashLog.Write(e.Exception, "UnobservedTask");
			e.SetObserved();
		};
#if ANDROID
		Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Services.CrashLog.Write(e.Exception, "Android");
#endif
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseBarcodeReader()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
				fonts.AddFont("MaterialIcons-Regular.ttf", "MaterialIcons");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
