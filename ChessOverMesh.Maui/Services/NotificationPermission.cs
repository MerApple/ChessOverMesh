namespace ChessOverMesh.Maui;

/// <summary>Runtime POST_NOTIFICATIONS permission (Android 13+). Older versions grant notifications implicitly.</summary>
public sealed class NotificationPermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        new[] { ("android.permission.POST_NOTIFICATIONS", true) };
}
