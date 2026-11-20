"""Word error with the sized encoder context on and off, on real recorded speech (ST-031).

`SCREENTRAIL_SPEECH_SIZED_CONTEXT=1` keeps a narrated session inside the processor budget, and it is off
because the only speech it had been checked on was Windows' synthetic voice. This measures it on people
talking: the individual headset recordings of one meeting of the AMI Meeting Corpus, each scored against
the corpus's own word-level transcript of the person wearing that headset, and, as a second and much
easier data point, read-aloud speech from LibriSpeech.

It is not ST-027's criterion. That one is a technician narrating a support session into their own
microphone, and it still needs the owner's recording (research/fixtures/audio/README.md). This answers a
narrower question the owner's recording was also going to answer: does encoding less of whisper.cpp's
window change what is heard?

What runs is the service's own pipeline (the detector, the gate, the recogniser and the hallucination
filter), through research/eval/real_speech/harness, which compiles the service's source files for a
machine that is not Windows. The words are scored by research/eval/wer.py, unchanged.

    python research/eval/real_speech/measure.py --work ~/screentrail-real-speech

Nothing it downloads belongs in the repository: the recordings and transcripts stay in `--work`, which
must be outside the checkout or ignored by git. Only the numbers are written down
(research/eval/real_speech/RESULTS.md).

The corpora, both CC BY 4.0:

- AMI Meeting Corpus, https://groups.inf.ed.ac.uk/ami/corpus/ (Carletta et al., 2005). Headset
  recordings and the manual annotations, release 1.6.2.
- LibriSpeech, https://www.openslr.org/12 (Panayotov et al., 2015). The test-clean set.
"""

from __future__ import annotations

import argparse
import html
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import time
import urllib.error
import urllib.request
import wave
import zipfile
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent.parent
sys.path.insert(0, str(HERE.parent))

import wer  # noqa: E402

AMI_AUDIO = "https://groups.inf.ed.ac.uk/ami/AMICorpusMirror/amicorpus/{meeting}/audio/{meeting}.Headset-{channel}.wav"
AMI_ANNOTATIONS = "https://groups.inf.ed.ac.uk/ami/AMICorpusAnnotations/ami_public_manual_1.6.2.zip"
LIBRISPEECH = "https://www.openslr.org/resources/12/test-clean.tar.gz"

VARIABLE = "SCREENTRAIL_SPEECH_SIZED_CONTEXT"
IMAGE = "screentrail-speech-harness"
RATE = 16_000

_WORD = re.compile(r"<w\s([^>]*)>([^<]*)</w>")
_SPEAKER = re.compile(r'<speaker\s[^>]*channel="(\d)"[^>]*nxt_agent="([A-Z])"')
_SUMMARY = re.compile(
    r"(\d+) segments, (\d+) rejected as invented, (\d+) stretches heard\. Took ([\d.]+)s for ([\d.]+)s"
)


@dataclass(frozen=True)
class Recording:
    name: str
    corpus: str
    wav: Path
    reference: Path


def fetch(url: str, to: Path, attempts: int = 6) -> None:
    """Downloads once. The AMI mirror answers 503 for a file now and then, so it is asked again."""
    if to.exists() and to.stat().st_size > 4096:
        return
    to.parent.mkdir(parents=True, exist_ok=True)
    part = to.with_suffix(to.suffix + ".part")
    for attempt in range(1, attempts + 1):
        try:
            with urllib.request.urlopen(url, timeout=120) as response, part.open("wb") as out:
                shutil.copyfileobj(response, out)
            part.replace(to)
            return
        except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
            print(f"  {url}: {error} (attempt {attempt} of {attempts})", file=sys.stderr)
            time.sleep(10 * attempt)
    raise SystemExit(f"Could not download {url}")


def ami(work: Path, meeting: str) -> list[Recording]:
    """One recording per person in the meeting: their own headset, and the words they said."""
    archive = work / "ami_public_manual_1.6.2.zip"
    fetch(AMI_ANNOTATIONS, archive)
    recordings = []
    with zipfile.ZipFile(archive) as annotations:
        meetings = annotations.read("corpusResources/meetings.xml").decode("iso-8859-1")
        block = re.search(rf'observation="{meeting}".*?</meeting>', meetings, re.DOTALL)
        if block is None:
            raise SystemExit(f"{meeting} is not in the AMI annotations")
        for channel, agent in sorted(_SPEAKER.findall(block.group(0))):
            words = annotations.read(f"words/{meeting}.{agent}.words.xml").decode("iso-8859-1")
            reference = work / "reference" / f"{meeting}.{agent}.txt"
            reference.parent.mkdir(parents=True, exist_ok=True)
            reference.write_text(ami_reference(words), encoding="utf-8")
            wav = work / "audio" / f"{meeting}.Headset-{channel}.wav"
            fetch(AMI_AUDIO.format(meeting=meeting, channel=channel), wav)
            recordings.append(Recording(f"{meeting} speaker {agent}", "AMI", wav, reference))
    return recordings


def ami_reference(words_xml: str) -> str:
    """
    The words in the order they were said. Everything said is kept, the "um"s and the false starts
    included, because the reference is what was said (research/fixtures/audio/README.md). Punctuation
    tokens are left out; wer.py would drop them anyway. Laughs and coughs are not words in the corpus.
    """
    said = []
    for attributes, text in _WORD.findall(words_xml):
        if 'punc="true"' in attributes:
            continue
        start = re.search(r'starttime="([\d.]+)"', attributes)
        said.append((float(start.group(1)) if start else 0.0, html.unescape(text).strip()))
    said.sort(key=lambda word: word[0])
    return " ".join(text for _, text in said if text) + "\n"


def librispeech(work: Path, minutes: float) -> list[Recording]:
    """
    Read-aloud speech, one recording: whole chapters by different readers, one utterance after another
    with a second and a half of silence between, until there are `minutes` of it.
    """
    archive = work / "librispeech-test-clean.tar.gz"
    fetch(LIBRISPEECH, archive)
    decoder = shutil.which("ffmpeg") or shutil.which("afconvert")
    if decoder is None:
        raise SystemExit("LibriSpeech is FLAC: install ffmpeg (or run on macOS, which has afconvert)")

    flac = work / "librispeech"
    wav = work / "audio" / "librispeech-test-clean.wav"
    reference = work / "reference" / "librispeech-test-clean.txt"
    if wav.exists() and reference.exists():
        return [Recording("LibriSpeech test-clean", "LibriSpeech", wav, reference)]

    with tarfile.open(archive) as tar:
        transcripts = sorted(m.name for m in tar.getmembers() if m.name.endswith(".trans.txt"))
        # One chapter from each of the first readers, in the archive's own order: nothing is chosen by ear.
        chosen, readers = [], set()
        for name in transcripts:
            reader = name.split("/")[-3]
            if reader not in readers:
                readers.add(reader)
                chosen.append(name)
        said, samples, gap = [], bytearray(), bytes(int(RATE * 1.5) * 2)
        for name in chosen:
            if len(samples) / 2 / RATE >= minutes * 60:
                break
            lines = tar.extractfile(name).read().decode("utf-8").splitlines()
            for line in lines:
                utterance, text = line.split(" ", 1)
                member = f"{name.rsplit('/', 1)[0]}/{utterance}.flac"
                tar.extract(member, flac, filter="data")
                samples += decode(decoder, flac / member) + gap
                said.append(text.lower())

    wav.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(wav), "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(RATE)
        out.writeframes(bytes(samples))
    reference.parent.mkdir(parents=True, exist_ok=True)
    reference.write_text(" ".join(said) + "\n", encoding="utf-8")
    return [Recording("LibriSpeech test-clean", "LibriSpeech", wav, reference)]


def decode(decoder: str, flac: Path) -> bytes:
    out = flac.with_suffix(".wav")
    if decoder.endswith("ffmpeg"):
        command = [
            decoder,
            "-y",
            "-loglevel",
            "error",
            "-i",
            str(flac),
            "-ac",
            "1",
            "-ar",
            str(RATE),
            "-c:a",
            "pcm_s16le",
            str(out),
        ]
    else:
        command = [decoder, "-f", "WAVE", "-d", f"LEI16@{RATE}", "-c", "1", str(flac), str(out)]
    subprocess.run(command, check=True)
    with wave.open(str(out), "rb") as audio:
        return audio.readframes(audio.getnframes())


def build(runner: str) -> Path:
    project = HERE / "harness" / "SpeechHarness.csproj"
    # Standard output is the results table and nothing else.
    quiet = {"check": True, "stdout": sys.stderr}
    subprocess.run(["dotnet", "build", str(project), "-c", "Release", "--nologo", "-v", "quiet"], **quiet)
    if runner == "docker":
        subprocess.run(["docker", "build", "-q", "-t", IMAGE, str(HERE / "harness")], **quiet)
    return HERE / "harness" / "bin" / "Release" / "net10.0"


def transcribe(harness: Path, work: Path, recording: Recording, sized: bool, runner: str, model: str):
    """
    One run of the pipeline. Returns what it heard and its own summary line.

    In a Linux container by default, because whisper.cpp's macOS build runs on the graphics processor
    unless told otherwise and the service gives it no such instruction: on a Mac the native run is a
    different computation from the laptop's. The Linux and Windows builds have the processor alone.
    """
    switch = "1" if sized else "0"
    if runner == "docker":
        inside = "/work/" + recording.wav.relative_to(work).as_posix()
        command = [
            "docker",
            "run",
            "--rm",
            "-v",
            f"{harness}:/harness:ro",
            "-v",
            f"{work}:/work",
            # Where the pipeline keeps its model on Linux, so one download serves every run.
            "-v",
            f"{work / 'models'}:/ScreenTrail",
            "-e",
            f"{VARIABLE}={switch}",
            IMAGE,
            "dotnet",
            "/harness/speech-harness.dll",
            inside,
            "--model",
            model,
        ]
        environment = None
    else:
        command = ["dotnet", str(harness / "speech-harness.dll"), str(recording.wav), "--model", model]
        environment = {**os.environ, VARIABLE: switch}
    (work / "models").mkdir(exist_ok=True)
    started = time.monotonic()
    done = subprocess.run(command, capture_output=True, text=True, env=environment, check=False)
    wall = time.monotonic() - started
    if done.returncode != 0:
        raise SystemExit(f"{' '.join(command)}\n{done.stderr}")
    expected = "sized to each segment" if sized else "full"
    if f"Encoder context: {expected}" not in done.stderr:
        raise SystemExit(f"The pipeline did not run with the {expected} context:\n{done.stderr}")
    summary = _SUMMARY.search(done.stderr)
    return done.stdout, summary.groups() if summary else None, wall


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--work",
        type=Path,
        required=True,
        help="Where the corpora and transcripts are kept. Outside the checkout.",
    )
    parser.add_argument("--meeting", action="append", help="AMI meeting id; may be given more than once.")
    parser.add_argument(
        "--librispeech-minutes",
        type=float,
        default=12.0,
        help="Minutes of read-aloud speech as the second data point; 0 leaves it out.",
    )
    parser.add_argument(
        "--runner",
        choices=["docker", "native"],
        default="docker",
        help="native only where whisper.cpp has no graphics backend (Windows, Linux).",
    )
    parser.add_argument("--model", default="base.en", help="ST-027's default model.")
    parser.add_argument(
        "--jobs",
        type=int,
        default=1,
        help="Runs at once. Each uses one processor; more than one makes the timings indicative only.",
    )
    args = parser.parse_args(argv)

    work = args.work.expanduser().resolve()
    if REPO in work.parents or work == REPO:
        ignored = subprocess.run(["git", "-C", str(REPO), "check-ignore", "-q", str(work)], check=False)
        if ignored.returncode != 0:
            raise SystemExit(
                f"{work} is inside the repository and not ignored: the corpora are not committed"
            )
    work.mkdir(parents=True, exist_ok=True)

    recordings: list[Recording] = []
    for meeting in args.meeting or ["ES2004a"]:
        recordings += ami(work, meeting)
    if args.librispeech_minutes > 0:
        recordings += librispeech(work, args.librispeech_minutes)

    harness = build(args.runner)
    arms = (("full", False), ("sized", True))
    todo = [(recording, arm, sized) for recording in recordings for arm, sized in arms]
    with ThreadPoolExecutor(max_workers=max(args.jobs, 1)) as pool:
        runs = {}
        for index, (recording, arm, sized) in enumerate(todo):
            run = pool.submit(transcribe, harness, work, recording, sized, args.runner, args.model)
            runs[(recording.name, arm)] = run
            if index == 0:
                # The first run fetches the model; the rest start once it is on disk.
                run.result()
    rows = []
    for recording in recordings:
        reference = recording.reference.read_text(encoding="utf-8")
        row = {"recording": recording.name, "corpus": recording.corpus}
        heard = {}
        for arm, _ in arms:
            text, summary, wall = runs[(recording.name, arm)].result()
            out = work / "heard" / f"{recording.wav.stem}.{arm}.txt"
            out.parent.mkdir(parents=True, exist_ok=True)
            out.write_text(text, encoding="utf-8")
            heard[arm] = text
            row[arm] = wer.measure(reference, text).as_dict()
            if summary:
                segments, rejected, stretches, took, seconds = summary
                row[arm] |= {
                    "segments": int(segments),
                    "rejected": int(rejected),
                    "stretches": int(stretches),
                    "pipeline_seconds": float(took),
                }
                row["audio_seconds"] = float(seconds)
            row[arm]["wall_seconds"] = round(wall, 1)
            print(f"{recording.name}, {arm}: {wer.measure(reference, text)}", file=sys.stderr)
        # How far apart the two transcripts are from each other, whatever the reference says.
        row["sized_against_full"] = wer.measure(heard["full"], heard["sized"]).as_dict()
        rows.append(row)

    (work / "results.json").write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")
    print(table(rows))
    return 0


def table(rows: list[dict]) -> str:
    def cell(arm: dict) -> str:
        return (
            f"{arm['wer']:.1%} ({arm['substitutions']} sub, {arm['deletions']} del, {arm['insertions']} ins)"
        )

    lines = [
        "| Recording | Length | Words said | Word error, full | Word error, sized | Sized against full |"
        " Pipeline time, full | sized |",
        "|---|---|---|---|---|---|---|---|",
    ]
    totals: dict[tuple[str, str], list[int]] = {}
    for row in rows:
        minutes = row.get("audio_seconds", 0) / 60
        lines.append(
            f"| {row['recording']} | {minutes:.1f} min | {row['full']['reference_words']} | "
            f"{cell(row['full'])} | {cell(row['sized'])} | {row['sized_against_full']['wer']:.1%} |"
            f" {row['full'].get('pipeline_seconds', 0):.0f} s |"
            f" {row['sized'].get('pipeline_seconds', 0):.0f} s |"
        )
        for arm in ("full", "sized"):
            total = totals.setdefault((row["corpus"], arm), [0, 0, 0, 0])
            for index, key in enumerate(("reference_words", "substitutions", "deletions", "insertions")):
                total[index] += row[arm][key]
    # Per corpus, never pooled: read-aloud speech would dilute the number that matters.
    lines.append("")
    for (corpus, arm), (words, subs, dels, ins) in totals.items():
        lines.append(
            f"{corpus}, {arm}: {(subs + dels + ins) / max(words, 1):.1%} "
            f"over {words} reference words ({subs} sub, {dels} del, {ins} ins)"
        )
    return "\n".join(lines)


if __name__ == "__main__":
    raise SystemExit(main())
