#!/usr/bin/env python3
"""Holds the shape of every workflow job that can run on the spare laptop (2026-11-15 review).

The laptop's self-hosted runner signs in as the account that also runs the demo, so whatever a job
there executes can read what that account holds. Two rules keep what a job runs to what this
repository says:

  - every `uses:` is pinned to a full 40-character commit, so a moved tag cannot run on the laptop
    (an action in this repository, `./...`, is the repository's own code and is allowed);
  - every `run:` step runs in Windows PowerShell with `-NoProfile`, whether its shell comes from the
    step, the job's defaults or the workflow's defaults, so the account's profile script never runs
    before a step.

A job counts as a laptop job when its runs-on names `self-hosted` or the laptop's label. The checks
are tried first on made-up jobs that must pass and must fail, so a check that passes everything fails.

Usage: check_laptop_jobs.py [repo root] [workflow file ...]
"""

import copy
import pathlib
import re
import sys

import yaml

LAPTOP_LABELS = {'self-hosted', 'screentail-win'}
PINNED = re.compile(r'^[^@\s]+@[0-9a-f]{40}$')
SHELL = re.compile(r'^powershell(\.exe)?\s+-NoProfile(\s|$)', re.IGNORECASE)


def labels(runs_on):
    if isinstance(runs_on, str):
        return {runs_on}
    if isinstance(runs_on, list):
        return {str(label) for label in runs_on}
    if isinstance(runs_on, dict):
        return labels(runs_on.get('labels', [])) | labels(runs_on.get('group', []))
    return set()


def is_laptop(job):
    runs_on = job.get('runs-on', '')
    # An expression could pick the laptop at run time, and nobody could say here that it does not.
    if isinstance(runs_on, str) and '${{' in runs_on:
        return True
    return bool({label.lower() for label in labels(runs_on)} & LAPTOP_LABELS)


def default_shell(node):
    return ((node or {}).get('defaults') or {}).get('run', {}).get('shell')


def problems_in(workflow, where):
    bad = []
    workflow_shell = default_shell(workflow)
    for name, job in (workflow.get('jobs') or {}).items():
        if not isinstance(job, dict) or not is_laptop(job):
            continue
        if 'uses' in job:
            bad.append(f"{where}: laptop job {name!r} calls a reusable workflow, which this check cannot follow.")
            continue
        job_shell = default_shell(job) or workflow_shell
        for shell, level in ((workflow_shell, 'the workflow defaults'), (default_shell(job), 'its defaults')):
            if shell is not None and not SHELL.match(str(shell)):
                bad.append(f"{where}: laptop job {name!r}: {level} set shell {shell!r}, which does not start with 'powershell -NoProfile'.")
        for index, step in enumerate(job.get('steps') or []):
            label = step.get('name') or step.get('uses') or f'step {index + 1}'
            uses = step.get('uses')
            if uses is not None and not str(uses).startswith('./') and not PINNED.match(str(uses)):
                bad.append(f"{where}: laptop job {name!r}, {label!r}: {uses!r} is not pinned to a 40-character commit.")
            if 'run' in step:
                shell = step.get('shell') or job_shell
                if shell is None:
                    bad.append(f"{where}: laptop job {name!r}, {label!r}: no shell is set, so the runner's default (with the profile) runs it.")
                elif not SHELL.match(str(shell)):
                    bad.append(f"{where}: laptop job {name!r}, {label!r}: shell {shell!r} does not start with 'powershell -NoProfile'.")
    return bad


def self_test():
    good_sha = 'actions/checkout@' + 'a' * 40
    good = {
        'defaults': None,
        'jobs': {
            'hosted': {'runs-on': 'windows-latest', 'steps': [{'uses': 'actions/checkout@v7'}, {'run': 'x', 'shell': 'pwsh'}]},
            'laptop': {
                'runs-on': ['self-hosted', 'Windows', 'screentail-win'],
                'defaults': {'run': {'shell': "powershell -NoProfile -ExecutionPolicy Bypass -Command \". '{0}'\""}},
                'steps': [
                    {'uses': good_sha + ''},
                    {'uses': './.github/actions/local'},
                    {'run': 'x'},
                    {'run': 'y', 'shell': 'powershell -NoProfile -Command ". \'{0}\'"'},
                ],
            },
        },
    }
    if problems_in(good, 'sample'):
        sys.exit(f"Self-test: a correct sample failed: {problems_in(good, 'sample')}")

    def broken(change):
        sample = copy.deepcopy(good)
        change(sample['jobs']['laptop'], sample)
        return sample

    must_fail = {
        'an action by tag': broken(lambda j, w: j['steps'].append({'uses': 'actions/upload-artifact@v7'})),
        'an action by a short commit': broken(lambda j, w: j['steps'].append({'uses': 'actions/checkout@3d3c42e'})),
        'a job default with the profile': broken(lambda j, w: j['defaults']['run'].update(shell="powershell -ExecutionPolicy Bypass -Command \". '{0}'\"")),
        'a step with the profile': broken(lambda j, w: j['steps'].append({'run': 'x', 'shell': 'powershell'})),
        'a step in pwsh': broken(lambda j, w: j['steps'].append({'run': 'x', 'shell': 'pwsh -NoProfile'})),
        'no shell anywhere': broken(lambda j, w: j.pop('defaults')),
        'the label alone': broken(lambda j, w: j.update({'runs-on': 'screentail-win', 'steps': [{'uses': 'actions/checkout@v7'}]})),
        'runs-on from an expression': broken(lambda j, w: j.update({'runs-on': '${{ inputs.where }}', 'steps': [{'uses': 'actions/checkout@v7'}]})),
    }
    for case, sample in must_fail.items():
        if not problems_in(sample, 'sample'):
            sys.exit(f"Self-test: {case} passed, and it must fail.")


def main():
    root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else '.')
    files = [pathlib.Path(f) for f in sys.argv[2:]] or sorted((root / '.github/workflows').glob('*.yml'))
    self_test()

    bad, laptop_jobs = [], 0
    for path in files:
        workflow = yaml.safe_load(path.read_text())
        laptop_jobs += sum(1 for job in (workflow.get('jobs') or {}).values() if isinstance(job, dict) and is_laptop(job))
        bad += problems_in(workflow, path.name)

    for line in bad:
        print(f"::error::{line}")
    print("\n".join(bad) if bad else f"{laptop_jobs} laptop job(s) in {len(files)} workflow(s) pin every action and run every step without the profile")
    sys.exit(1 if bad else 0)


if __name__ == '__main__':
    main()
