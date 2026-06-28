namespace ChessOverMesh.Maui;

/// <summary>Whether the app is currently in the foreground. Updated by the platform lifecycle events wired in
/// <see cref="MauiProgram"/>; used to suppress status-bar notifications while the user is looking at the app.</summary>
public static class AppState
{
    public static bool IsForeground = true;
}
