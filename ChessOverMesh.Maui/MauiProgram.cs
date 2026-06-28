using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;

namespace ChessOverMesh.Maui;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			})
			.ConfigureLifecycleEvents(events =>
			{
#if ANDROID
				// Track foreground/background so the receive loop can suppress notifications while the user
				// is actively in the app, and post them when it's backgrounded.
				events.AddAndroid(android => android
					.OnResume(_ => AppState.IsForeground = true)
					.OnStop(_ => AppState.IsForeground = false));
#endif
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
