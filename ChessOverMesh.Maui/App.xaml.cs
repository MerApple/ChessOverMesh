namespace ChessOverMesh.Maui;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		ApplyUiTextSize();   // seed the app-wide button/label text size from the saved setting
	}

	/// <summary>Pushes the saved app-wide UI text size into the shared resource that the Button/Label/input
	/// styles bind via DynamicResource — so buttons and labels across the app resize live.</summary>
	public static void ApplyUiTextSize()
	{
		if (Current != null) Current.Resources["UiTextSize"] = AppSettings.UiTextSize;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}