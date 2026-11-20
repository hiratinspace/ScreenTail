using ScreenTrail.Core.Speech;
using ScreenTrail.Service.Speech;

// ST-031: `ScreenTrail.Service transcribe`, for a machine the service does not run on. The arguments and
// the output are the service's: the transcript on standard output, the timing on standard error, and
// SCREENTRAIL_SPEECH_SIZED_CONTEXT read from the environment by the same code.
if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: speech-harness <recording.wav> [--model tiny.en|base.en|small.en]");
    return 2;
}

// The service's own choice (Program.ModelFrom): the named model, or the one it would pick for this
// machine's processors. measure.py always names one, so a Mac and the laptop are asked for the same.
var at = Array.IndexOf(args, "--model");
var name = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
var model = SpeechModels.All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
    ?? SpeechModels.For(Environment.ProcessorCount);

return await Transcribe.RunAsync(args[0], model).ConfigureAwait(false);
