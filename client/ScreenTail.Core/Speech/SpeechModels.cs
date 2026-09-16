namespace ScreenTail.Core.Speech;

/// <summary>
/// The models ScreenTail will download, and the hashes it checks them against (ST-027).
///
/// English-only builds: v1 documents English support calls, and the multilingual models of the same size
/// are measurably worse at English for the same CPU. ST-027's "multilingual transcription" is a later
/// ticket and a different model list.
///
/// <b>The hashes are the point.</b> A model file is native code's input, it arrives over the network, and
/// it decides what words end up in a customer's ticket. <see cref="ModelDownload"/> refuses a file whose
/// hash does not match and deletes it, so a truncated download, a proxy's error page, or a substituted
/// file never reaches the transcriber. These were read from the publisher's own LFS pointers on
/// 2026-09-16 and are checked on every start, not only after a download.
/// </summary>
public static class SpeechModels
{
    private const string Base = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    /// <summary>
    /// The default (AC1's "default model"). About 150 MB, and the smallest that holds its accuracy on a
    /// technician talking over a fan while their own machine is busy doing the capture.
    /// </summary>
    public static SpeechModel BaseEnglish { get; } = new(
        "base.en",
        new Uri(Base + "ggml-base.en.bin"),
        147_964_211,
        "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002");

    /// <summary>
    /// For a machine with cores to spare. Three times the size and materially better on product names,
    /// which is where a wrong word turns into a wrong fact in a note.
    /// </summary>
    public static SpeechModel SmallEnglish { get; } = new(
        "small.en",
        new Uri(Base + "ggml-small.en.bin"),
        487_614_201,
        "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d");

    /// <summary>
    /// A fallback for a machine that cannot keep up, and what the tests use because it downloads in
    /// seconds. Noticeably worse; chosen only when the alternative is no narration at all.
    /// </summary>
    public static SpeechModel TinyEnglish { get; } = new(
        "tiny.en",
        new Uri(Base + "ggml-tiny.en.bin"),
        77_704_715,
        "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f");

    public static IReadOnlyList<SpeechModel> All { get; } = [TinyEnglish, BaseEnglish, SmallEnglish];

    /// <summary>
    /// Which model suits this machine. ST-031 budgets 15% of the CPU for all of ScreenTail, and
    /// transcription is the hungriest thing in it, so a four-core laptop that is also taking screenshots
    /// and running OCR gets the base model and an eight-core desktop gets the better one.
    /// </summary>
    public static SpeechModel For(int processors) => processors >= 8 ? SmallEnglish : BaseEnglish;
}
