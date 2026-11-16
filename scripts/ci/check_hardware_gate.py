#!/usr/bin/env python3
"""ST-018: runs the Hardware evidence gate from ci.yml against made-up laptop results.

The gate is a shell script inside ci.yml. This takes that exact script out of the file, runs it with a
stand-in for `gh` that answers with chosen check runs, and holds it to what docs/dev/windows-test-loop.md
says it does:

  - the laptop off on purpose (HW_RUNNER not true): no evidence, said in the summary, the pull request
    labelled needs-hardware-evidence, and not a blocker;
  - a pull request from a fork, whose code never reaches the laptop: the same answer, without waiting;
  - both laptop jobs passed: a pass;
  - a laptop job skipped while the laptop was meant to run: not a pass. The summary says the jobs did
    not run, the pull request is labelled, and the gate fails (2026-11-15 review: it used to count a
    skipped job as a passed one);
  - a laptop job failed or was cancelled: a failure.

Usage: check_hardware_gate.py [repo root] [path to ci.yml, default .github/workflows/ci.yml]
"""

import os
import pathlib
import stat
import subprocess
import sys
import tempfile

import yaml

REQUIRED = [
    'Spare laptop - capabilities and real-window tests',
    'Spare laptop - input injection (takes over the keyboard)',
]
REPO = 'owner/repo'
SHA = 'a' * 40

FAKE_GH = """#!/usr/bin/env bash
# Stands in for the GitHub CLI: records every call, answers the check-runs query from a file.
printf '%s\\n' "$*" >> "$FAKE_GH_LOG"
if [ "$1" = "api" ]; then
  cat "$FAKE_GH_RUNS"
fi
exit 0
"""


def gate_script(ci_path):
    workflow = yaml.safe_load(open(ci_path))
    job = workflow['jobs'].get('hardware-gate')
    if not job:
        sys.exit(f"{ci_path} has no hardware-gate job.")
    steps = [s for s in job['steps'] if s.get('name') == 'Wait for the spare laptop']
    if len(steps) != 1:
        sys.exit(f"{ci_path}: the hardware gate has no single 'Wait for the spare laptop' step.")
    return steps[0]['run']


def run(script, work, *, hw_runner, head_repo, runs):
    runs_file = work / 'runs.tsv'
    runs_file.write_text(''.join(f"{name}\t{status}\t{conclusion}\n" for name, status, conclusion in runs))
    log = work / 'gh.log'
    log.write_text('')
    summary = work / 'summary.md'
    summary.write_text('')
    env = {
        'PATH': f"{work}{os.pathsep}{os.environ['PATH']}",
        'HOME': str(work),
        'GH_TOKEN': 'not-a-token',
        'HW_RUNNER': hw_runner,
        'REPO': REPO,
        'SHA': SHA,
        'PR': '7',
        'HEAD_REPO': head_repo,
        'GITHUB_STEP_SUMMARY': str(summary),
        'FAKE_GH_LOG': str(log),
        'FAKE_GH_RUNS': str(runs_file),
    }
    # Every case here ends on the gate's first look, so a gate that waits instead has already failed.
    try:
        done = subprocess.run(['bash', '-c', script], env=env, capture_output=True, text=True, timeout=60)
    except subprocess.TimeoutExpired:
        return None, '', '', 'timed out: the gate waited where it should have answered'
    return done.returncode, summary.read_text(), log.read_text(), done.stdout + done.stderr


def both(conclusion_one, conclusion_two):
    return [(REQUIRED[0], 'completed', conclusion_one), (REQUIRED[1], 'completed', conclusion_two)]


# (name, HW_RUNNER, head repo, check runs, expected exit code, text the summary must hold, labelled)
CASES = [
    ('laptop off on purpose', 'false', REPO, [], 0, 'Hardware evidence: none', True),
    ('HW_RUNNER unset', '', REPO, [], 0, 'Hardware evidence: none', True),
    ('pull request from a fork', 'true', 'someone/fork', both('skipped', 'skipped'), 0, 'Hardware evidence: none', True),
    ('both laptop jobs passed', 'true', REPO, both('success', 'success'), 0, 'The spare laptop passed', False),
    ('input job skipped', 'true', REPO, both('success', 'skipped'), 1, 'Hardware evidence: not run', True),
    ('both laptop jobs skipped', 'true', REPO, both('skipped', 'skipped'), 1, 'Hardware evidence: not run', True),
    ('capabilities job failed', 'true', REPO, both('failure', 'skipped'), 1, None, False),
    ('input job cancelled', 'true', REPO, both('success', 'cancelled'), 1, None, False),
]


def main():
    root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else '.')
    ci_path = pathlib.Path(sys.argv[2]) if len(sys.argv) > 2 else root / '.github/workflows/ci.yml'
    script = gate_script(ci_path)

    bad = []
    with tempfile.TemporaryDirectory() as temp:
        work = pathlib.Path(temp)
        gh = work / 'gh'
        gh.write_text(FAKE_GH)
        gh.chmod(gh.stat().st_mode | stat.S_IXUSR)

        for name, hw_runner, head_repo, runs, code, says, labelled in CASES:
            got, summary, log, output = run(script, work, hw_runner=hw_runner, head_repo=head_repo, runs=runs)
            problems = []
            if got != code:
                problems.append(f"exit code {got}, expected {code}")
            if says and says not in summary:
                problems.append(f"the summary does not say '{says}'")
            if labelled and '--add-label needs-hardware-evidence' not in log:
                problems.append("the pull request was not labelled needs-hardware-evidence")
            if not labelled and '--add-label needs-hardware-evidence' in log:
                problems.append("the pull request was labelled needs-hardware-evidence")
            if problems:
                tail = ' | '.join(line for line in output.splitlines()[-4:] if line.strip())
                bad.append(f"{name}: {'; '.join(problems)}. Gate said: {tail}")

    for line in bad:
        print(f"::error file=.github/workflows/ci.yml::{line}")
    print("\n".join(bad) if bad else f"the hardware gate answered all {len(CASES)} cases as documented")
    sys.exit(1 if bad else 0)


if __name__ == '__main__':
    main()
