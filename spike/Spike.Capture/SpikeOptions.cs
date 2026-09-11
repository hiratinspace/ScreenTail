using System.Globalization;

namespace ScreenTail.Spike.Capture;

internal sealed record SpikeOptions(
    string Command,
    double Minutes,
    string? OutDir,
    bool Whisper,
    bool Uia,
    bool Ocr,
    bool FromScreen,
    string Label)
{
    public const string Usage = """
        Usage: Spike.Capture <command> [options]

        Commands
          run             Hooks + screenshots, plus UIA polling, Whisper and OCR unless disabled (AC1-AC4)
          overlay-check   One full-screen capture; reports whether Spike.Overlay is visible in it (AC3)
          legibility      Synthetic 4K frames downscaled to <= 1600 px; JPEG size and OCR recall (AC4)

        Options
          --minutes <n>   Run length for 'run' (default 5)
          --out <dir>     Output directory (default %LOCALAPPDATA%\ScreenTail.Spike\runs\<timestamp>)
          --no-whisper    Skip speech-to-text load
          --no-uia        Skip UI Automation polling
          --no-ocr        Skip OCR
          --from-screen   'legibility' also measures a capture of the primary monitor
          --label <name>  Suffix for 'overlay-check' output files (default: excluded)
        """;

    public bool FullLoad => Whisper && Uia && Ocr;

    public static SpikeOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("run" or "overlay-check" or "legibility"))
        {
            return null;
        }

        var minutes = 5.0;
        string? outDir = null;
        bool whisper = true, uia = true, ocr = true, fromScreen = false;
        var label = "excluded";

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--minutes" when i + 1 < args.Length
                    && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    && parsed > 0:
                    minutes = parsed;
                    i++;
                    break;
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;
                case "--label" when i + 1 < args.Length:
                    label = args[++i];
                    break;
                case "--no-whisper":
                    whisper = false;
                    break;
                case "--no-uia":
                    uia = false;
                    break;
                case "--no-ocr":
                    ocr = false;
                    break;
                case "--from-screen":
                    fromScreen = true;
                    break;
                default:
                    return null;
            }
        }

        return new SpikeOptions(args[0], minutes, outDir, whisper, uia, ocr, fromScreen, label);
    }
}
