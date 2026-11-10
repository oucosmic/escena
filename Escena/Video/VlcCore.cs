using LibVLCSharp.Shared;

namespace Escena.Video;

/// <summary>Una sola instància del motor VLC per a tot el programa (s'inicia el primer cop que cal).</summary>
public static class VlcCore
{
    public static readonly string[] VideoExtensions =
        { ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".wmv", ".webm", ".mpg", ".mpeg", ".ts", ".mts" };

    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };

    public static string VideoFilter =>
        "Fitxers de vídeo|" + string.Join(";", VideoExtensions.Select(e => "*" + e)) + "|Tots els fitxers|*.*";

    public static string ImageFilter =>
        "Imatges|" + string.Join(";", ImageExtensions.Select(e => "*" + e)) + "|Tots els fitxers|*.*";

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<LibVLC> Instance = new(() =>
    {
        Core.Initialize();
        return new LibVLC("--no-osd", "--no-video-title-show", "--no-snapshot-preview", "--quiet");
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static LibVLC LibVLC => Instance.Value;

    /// <summary>Durada en segons (0 si no es pot saber).</summary>
    public static async Task<double> GetDurationAsync(string path)
    {
        var media = new Media(LibVLC, path, FromType.FromPath);
        try
        {
            var status = await media.Parse(MediaParseOptions.ParseLocal, 5000);
            // La continuació pot arribar al fil d'esdeveniments del VLC: cal sortir-ne abans
            // d'alliberar el Media, o el VLC peta més tard.
            await Task.Yield();
            if (status != MediaParsedStatus.Done) throw new InvalidDataException("format no reconegut");
            return Math.Max(0, media.Duration / 1000.0);
        }
        finally
        {
            await Task.Delay(100);
            media.Dispose();
        }
    }
}
