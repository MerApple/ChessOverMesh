using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;

namespace ChessOverMesh.Maui;

// WindowSoftInputMode = AdjustResize so the keyboard is reported as a window inset (needed for the handler below).
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ApplyKeyboardHandling();
        try { BackgroundPoll.Apply(); } catch { /* scheduling the background job is best-effort */ }
    }

    protected override void OnResume()
    {
        base.OnResume();
        ApplyKeyboardHandling();   // MAUI can reset the soft-input mode after create; re-assert it
    }

    void ApplyKeyboardHandling()
    {
        // Force adjustResize so the keyboard (IME) is reported as a window inset (MAUI may otherwise leave the
        // window in a mode that lets the keyboard overlay/pan the content).
        Window?.SetSoftInputMode(SoftInput.AdjustResize | SoftInput.StateHidden);

        // .NET 9 MAUI runs the app edge-to-edge, so even with adjustResize the keyboard overlays the content
        // rather than shrinking it. Pad the content view by the system bars + keyboard insets so the layout
        // resizes above the keyboard, keeping the chat list visible while typing.
        var content = FindViewById(Android.Resource.Id.Content);
        if (content != null)
        {
            ViewCompat.SetOnApplyWindowInsetsListener(content, new ImeInsetsListener());
            ViewCompat.RequestApplyInsets(content);
        }
    }

    sealed class ImeInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(Android.Views.View? v, WindowInsetsCompat? insets)
        {
            if (v == null || insets == null) return insets ?? WindowInsetsCompat.Consumed;
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
            var ime = insets.GetInsets(WindowInsetsCompat.Type.Ime());
            v.SetPadding(bars.Left, bars.Top, bars.Right, System.Math.Max(bars.Bottom, ime.Bottom));
            return WindowInsetsCompat.Consumed;
        }
    }
}
