using System.IO;

namespace ChessOverMesh.Gui;

/// <summary>
/// The notification sounds the user can choose from — the .wav files in the Windows Media folder, plus a
/// "(none)" option. Played via MediaPlayer so volume can be controlled (SystemSounds can't be).
/// </summary>
internal static class SoundLibrary
{
    public sealed record Sound(string Name, string Path);   // Path == "" means no sound

    public static readonly Sound None = new("(none)", "");

    private static string MediaDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

    /// <summary>"(none)" followed by every .wav in the Windows Media folder (by display name).</summary>
    public static IReadOnlyList<Sound> Available()
    {
        var list = new List<Sound> { None };
        try
        {
            if (Directory.Exists(MediaDir))
                list.AddRange(Directory.GetFiles(MediaDir, "*.wav")
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .Select(f => new Sound(Path.GetFileNameWithoutExtension(f), f)));
        }
        catch { /* fall back to just "(none)" */ }
        return list;
    }

    public static string DefaultChess() => Prefer("ding.wav", "Windows Ding.wav", "chimes.wav", "Windows Notify.wav");
    public static string DefaultChat() => Prefer("Windows Notify System Generic.wav", "Windows Notify.wav", "chord.wav", "chimes.wav");

    // First preferred file that exists, else any .wav, else "" (none).
    private static string Prefer(params string[] names)
    {
        try
        {
            foreach (var n in names)
            {
                var p = Path.Combine(MediaDir, n);
                if (File.Exists(p)) return p;
            }
            if (Directory.Exists(MediaDir))
                return Directory.GetFiles(MediaDir, "*.wav").FirstOrDefault() ?? "";
        }
        catch { /* ignore */ }
        return "";
    }
}
