using Whisper.net.Ggml;

namespace ScreenTail.Spike.Capture;

/// <summary>Downloads models on first use into %LOCALAPPDATA%\ScreenTail.Spike (never into the repo).</summary>
internal static class Assets
{
    private static readonly Uri TessdataUrl = new("https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata");

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenTail.Spike");

    public static string TessdataDir { get; } = Path.Combine(Root, "tessdata");

    public static async Task<string> EnsureTessdataAsync()
    {
        var path = Path.Combine(TessdataDir, "eng.traineddata");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(TessdataDir);
            Console.WriteLine("Downloading Tesseract English data (tessdata_fast, ~4 MB)...");
            using var http = new HttpClient();
            await using var source = await http.GetStreamAsync(TessdataUrl);
            await WriteAtomicallyAsync(source, path);
        }

        return TessdataDir;
    }

    public static async Task<string> EnsureWhisperModelAsync()
    {
        var path = Path.Combine(Root, "models", "ggml-base.bin");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Console.WriteLine("Downloading Whisper base model (~150 MB)...");
            await using var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Base);
            await WriteAtomicallyAsync(source, path);
        }

        return path;
    }

    private static async Task WriteAtomicallyAsync(Stream source, string path)
    {
        var partial = path + ".part";
        await using (var target = File.Create(partial))
        {
            await source.CopyToAsync(target);
        }

        File.Move(partial, path, overwrite: true);
    }
}
