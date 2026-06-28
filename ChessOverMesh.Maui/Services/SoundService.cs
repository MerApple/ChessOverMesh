using Plugin.Maui.Audio;

namespace ChessOverMesh.Maui;

/// <summary>The bundled notification sounds the user can pick (asset "" = off). MAUI port of the desktop
/// SoundLibrary — instead of scanning the Windows Media folder, these are short tones bundled in Resources/Raw.</summary>
internal static class SoundLibrary
{
    public sealed record Sound(string Name, string Asset);   // Asset == "" means no sound

    public static readonly Sound None = new("(none)", "");

    public static IReadOnlyList<Sound> Available() => new[]
    {
        None,
        new Sound("Ding", "ding.wav"),
        new Sound("Chime", "chime.wav"),
        new Sound("Alert", "alert.wav"),
    };

    public static string DefaultChess() => "ding.wav";
    public static string DefaultChat() => "chime.wav";
}

/// <summary>Plays bundled notification tones via Plugin.Maui.Audio. Players are cached per asset and
/// replayed by seeking to the start, so repeated notifications don't reopen the file.</summary>
internal static class SoundService
{
    static readonly Dictionary<string, IAudioPlayer> _players = new();

    public static async void Play(string asset, int volume)
    {
        if (string.IsNullOrEmpty(asset)) return;   // "(none)"
        try
        {
            if (!_players.TryGetValue(asset, out var player))
            {
                using var stream = await FileSystem.OpenAppPackageFileAsync(asset);
                player = AudioManager.Current.CreatePlayer(stream);
                _players[asset] = player;
            }
            player.Volume = Math.Clamp(volume, 0, 100) / 100.0;
            player.Seek(0);
            player.Play();
        }
        catch { /* missing/unplayable asset or no audio output — ignore */ }
    }
}
