#!/usr/bin/env python3
"""ST-112: holds the shape of the installer jobs in ci.yml, and of what the signing job runs.

A second layer behind the real lock, which is the signing job's own conditions (a push to main, the
switch, the 'signing' environment that only main may deploy to) and Azure's federated credential
trusting that environment alone. This catches the edit that would weaken those, or widen what can
reach the signing token, before it merges:

  - the installer jobs run on the hosted runner, never on the laptop's labels;
  - installer-sign is the only job that may ask for an OIDC token, runs only for a push to main with
    the switch on, always in the 'signing' environment, needs the unsigned build, and has no
    permission beyond reading the repository and asking for the token;
  - installer-sign runs exactly the reviewed steps, in order: a pinned checkout of the same commit, the
    payload download, then sign.ps1 and package.ps1 commands, each layer signed before the next one
    wraps it, the verification, and the upload. Each run step is one exact command with no other key,
    the signing settings are only on the sign.ps1 steps, no step changes its shell, working folder or
    failure handling, and the job's shell is Windows PowerShell with nothing at workflow level to
    change it;
  - every action the installer jobs use is pinned to a commit, with only the inputs listed here;
  - no signing setting outside installer-sign's signing steps, and no credential context named in any
    expression anywhere in the workflow;
  - the scripts the signing job runs fetch WiX and the signing client only through the pinned,
    hash-checked download, and start no build, restore, tool restore or other build script;
  - installer/tools.json pins each package to one version and a SHA-256;
  - the WiX authoring has no preprocessor directive, no variable but its own inputs, no extension
    beyond Util and Bal, and no custom action except the two that run at install time on the target
    machine (an error message, and the uninstall cleanup through WiX's own quiet-exec). WiX runs no
    custom action while it builds; this keeps the authoring from growing a way to run code in the
    signing job.

The checks are first tried on made-up samples that must pass and must fail, so a check that waved
everything through fails here rather than being trusted.

Usage: python3 scripts/ci/check_installer_jobs.py [repository root]
"""

import copy
import json
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

import yaml

# Names built from parts, so this file does not read like the thing it looks for.
CTX = 'sec' + 'rets'
IDT = 'id-' + 'token'
OPEN = '$' + '{{'
CLOSE = '}}'


def expr(inner):
    return OPEN + ' ' + inner + ' ' + CLOSE


BUILD, SIGN, CHECK = 'installer', 'installer-sign', 'installer-signed-check'
HOSTED = 'windows-latest'
ENVIRONMENT = 'signing'
CONDITION = "github.event_name == 'push' && github.ref == 'refs/heads/main' && vars.SCREENTAIL_SIGNING == 'true'"
SIGNING_ENV = ('AZURE_', 'SCREENTAIL_SIGNING')
SIGNING_VARS = re.compile(r'\bvars\.(AZURE_\w+|SCREENTAIL_SIGNING_\w+)', re.IGNORECASE)

CHECKOUT = 'actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1'
UPLOAD = 'actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9'
DOWNLOAD = 'actions/download-artifact@37930b1c2abaa49bbe596cd826c3c89aef350131'
# A subset of each action's documented inputs. Anything else (another repository, a token, another
# run's artifacts) is a change to argue for in review.
INPUTS = {
    CHECKOUT: {'ref', 'persist-credentials'},
    DOWNLOAD: {'name', 'path'},
    UPLOAD: {'name', 'path', 'retention-days', 'if-no-files-found'},
}
PINNED = re.compile(r'[\w.-]+/[\w.-]+@[0-9a-f]{40}')

VERSION = '"0.1.' + expr('github.run_number') + '"'
SIGN_SETTINGS = {
    'SCREENTAIL_REQUIRE_SIGNING': '1',
    'SCREENTAIL_SIGNING_ENDPOINT': expr('vars.SCREENTAIL_SIGNING_ENDPOINT'),
    'SCREENTAIL_SIGNING_ACCOUNT': expr('vars.SCREENTAIL_SIGNING_ACCOUNT'),
    'SCREENTAIL_SIGNING_PROFILE': expr('vars.SCREENTAIL_SIGNING_PROFILE'),
    'AZURE_TENANT_ID': expr('vars.AZURE_TENANT_ID'),
    'AZURE_CLIENT_ID': expr('vars.AZURE_CLIENT_ID'),
}
VERIFY_SETTINGS = {'SCREENTAIL_REQUIRE_SIGNING': '1'}
PAYLOAD = 'artifacts/installer/payload'
MSI = 'artifacts/installer/packages/ScreenTail.msi'
ENGINE = 'artifacts/installer/work/engine.exe'
SETUP = 'artifacts/installer/packages/ScreenTail-Setup.exe'

# installer-sign, step by step. ('uses', action, inputs) or (kind, command, env).
SEQUENCE = [
    ('uses', CHECKOUT, {'ref': expr('github.sha'), 'persist-credentials': False}),
    ('uses', DOWNLOAD, {'name': 'screentail-installer-payload', 'path': PAYLOAD}),
    ('run', './installer/sign.ps1 -Folder ' + PAYLOAD, SIGN_SETTINGS),
    ('run', './installer/package.ps1 -Step Msi -Version ' + VERSION, None),
    ('run', './installer/sign.ps1 -Path ' + MSI, SIGN_SETTINGS),
    ('run', './installer/package.ps1 -Step Bundle -Version ' + VERSION, None),
    ('run', './installer/package.ps1 -Step Detach -Version ' + VERSION, None),
    ('run', './installer/sign.ps1 -Path ' + ENGINE, SIGN_SETTINGS),
    ('run', './installer/package.ps1 -Step Reattach -Version ' + VERSION, None),
    ('run', './installer/sign.ps1 -Path ' + SETUP, SIGN_SETTINGS),
    ('run', './installer/sign.ps1 -Verify -Folder ' + PAYLOAD + ' -Path ' + ','.join([MSI, ENGINE, SETUP]), VERIFY_SETTINGS),
    ('uses', UPLOAD, {'name': 'screentail-installer', 'path': MSI + '\n' + SETUP + '\n', 'retention-days': 7, 'if-no-files-found': 'error'}),
]
SIGN_JOB_KEYS = {'name', 'needs', 'if', 'runs-on', 'environment', 'permissions', 'timeout-minutes', 'defaults', 'steps'}
RUN_STEP_KEYS = {'name', 'env', 'run'}
USES_STEP_KEYS = {'name', 'uses', 'with'}
UNSAFE = (';', '|', '&', '`', '$(', '\n', '\r')

# What the scripts the signing job runs must do, and must never do.
SCRIPTS = ('installer/package.ps1', 'installer/sign.ps1', 'installer/tools.ps1')
MUST_CONTAIN = {
    'installer/tools.ps1': [
        "Get-FileHash -Algorithm SHA256 -Path $package",
        "if ($hash -ne $pin.sha256) {",
        "nuget verify $package --all",
        "Join-Path $env:ProgramFiles 'dotnet\\dotnet.exe'",
    ],
    'installer/package.ps1': [
        "Remove-Item Env:ACTIONS_ID_TOKEN_REQUEST_URL, Env:ACTIONS_ID_TOKEN_REQUEST_TOKEN",
        "Get-PinnedPackage 'wix' $tools",
        "Get-Extension 'WixToolset.Util.wixext'",
        "Get-Extension 'WixToolset.Bal.wixext'",
    ],
    'installer/sign.ps1': [
        "Get-PinnedPackage 'Microsoft.ArtifactSigning.Client'",
    ],
}
NEVER = [
    (re.compile(r'(?:\bdotnet(?:\.exe)?["\']?|\$dotnet)\s+(?:restore|build|publish|tool|run|msbuild|test|pack|exec|workload)\b', re.IGNORECASE), 'runs a dotnet build, restore or tool command'),
    (re.compile(r'tool\s+restore|dotnet-tools', re.IGNORECASE), 'restores a .NET tool'),
    (re.compile(r'(?:[&.]\s*|Join-Path\s+\S+\s+)["\']?[\w$:/\\.-]*(?:build|smoke-test)\.ps1', re.IGNORECASE), 'starts the build or the smoke test'),
    (re.compile(r'Invoke-Expression|\biex\b', re.IGNORECASE), 'evaluates a string as code'),
]

PACKAGES = {'wix', 'WixToolset.Util.wixext', 'WixToolset.Bal.wixext', 'Microsoft.ArtifactSigning.Client'}
WIX_FAMILY = ('wix', 'WixToolset.Util.wixext', 'WixToolset.Bal.wixext')

AUTHORING = ('installer/Package.wxs', 'installer/Bundle.wxs')
NAMESPACES = {
    'http://wixtoolset.org/schemas/v4/wxs',
    'http://wixtoolset.org/schemas/v4/wxs/util',
    'http://wixtoolset.org/schemas/v4/wxs/bal',
}
AUTHORING_VARS = {'Version', 'Payload', 'Msi', 'VcRedist', 'VcRedistBuild'}
QUIET_EXEC = {'Id', 'BinaryRef', 'DllEntry', 'Execute', 'Impersonate', 'Return'}


def expressions(text):
    # Every expression in a string, read the way Actions reads one: up to the closing braces that are
    # not inside a quoted string, where a doubled quote is a quote.
    found, i = [], 0
    while True:
        j = text.find(OPEN, i)
        if j < 0:
            return found
        k, quoted, inner = j + 3, False, []
        while k < len(text):
            c = text[k]
            if quoted:
                if c == "'" and text[k + 1:k + 2] == "'":
                    inner.append("''")
                    k += 2
                    continue
                quoted = c != "'"
            elif c == "'":
                quoted = True
            elif text.startswith(CLOSE, k):
                break
            inner.append(c)
            k += 1
        found.append(''.join(inner))
        i = k + 2


def names_context(text):
    # The credential context named anywhere in an expression, outside its quoted strings.
    for inner in expressions(text):
        bare = re.sub(r"'(?:[^']|'')*'", "''", inner)
        if re.search(r'\b' + CTX + r'\b', bare, re.IGNORECASE):
            return True
    return False


def strings(value):
    # Every key and value in parsed YAML, so nothing depends on how a dump would quote them.
    if isinstance(value, dict):
        for k, v in value.items():
            yield from strings(k)
            yield from strings(v)
    elif isinstance(value, list):
        for v in value:
            yield from strings(v)
    elif isinstance(value, str):
        yield value


def signing_keys(env):
    return [k for k in (env or {}) if isinstance(env, dict) and str(k).upper().startswith(SIGNING_ENV)]


def asks_for_token(permissions):
    if permissions == 'write-all':
        return True
    return isinstance(permissions, dict) and permissions.get(IDT) not in (None, 'none')


def check_workflow(workflow):
    bad = []
    jobs = workflow.get('jobs') or {}

    if 'defaults' in workflow:
        bad.append('the workflow sets defaults, which would change how the signing steps run')
    if asks_for_token(workflow.get('permissions')):
        bad.append(f'the workflow-level permissions grant {IDT}; only {SIGN} may ask for one')
    if signing_keys(workflow.get('env')):
        bad.append('the workflow env names a signing setting')
    if any(names_context(t) for t in strings(workflow)):
        bad.append(f'an expression in the workflow names the {CTX} context')

    for name in (BUILD, SIGN, CHECK):
        if name not in jobs:
            bad.append(f'job {name} is missing')
        elif jobs[name].get('runs-on') != HOSTED:
            bad.append(f'{name} must run on the hosted {HOSTED}, never the laptop')

    for name, job in jobs.items():
        if not isinstance(job, dict):
            continue
        if signing_keys(job.get('env')):
            bad.append(f'job {name} has a signing setting in its job env')
        if name != SIGN and asks_for_token(job.get('permissions')):
            bad.append(f'job {name} may ask for an OIDC token; only {SIGN} may')
        for step in job.get('steps') or []:
            label = step.get('name') or step.get('uses') or step.get('run') or '?'
            if name in (BUILD, SIGN, CHECK) and 'uses' in step and not PINNED.fullmatch(str(step['uses'])):
                bad.append(f'{name} uses {step["uses"]}, which is not pinned to a commit')
            if name in (BUILD, CHECK) and 'uses' in step and PINNED.fullmatch(str(step['uses'])):
                if step['uses'] not in INPUTS:
                    bad.append(f'{name} uses {step["uses"]}; only the pinned checkout, download and upload are allowed')
                elif set(step.get('with') or {}) - INPUTS[step['uses']]:
                    bad.append(f'{name} step {label!r} gives {step["uses"]} an input that is not allowed')
            if name != SIGN:
                if signing_keys(step.get('env')):
                    bad.append(f'job {name} step {label!r} has signing settings outside {SIGN}')
                if any(SIGNING_VARS.search(t) for t in strings(step)):
                    bad.append(f'job {name} step {label!r} reads a signing variable outside {SIGN}')

    bad += check_sign_job(jobs.get(SIGN) or {})

    signed_check = jobs.get(CHECK) or {}
    if signed_check and signed_check.get('needs') not in (SIGN, [SIGN]):
        bad.append(f'{CHECK} must need {SIGN}')
    return bad


def check_sign_job(job):
    bad = []
    if not job:
        return bad
    extra = set(job) - SIGN_JOB_KEYS
    if extra:
        bad.append(f'{SIGN} has job keys that are not allowed: {sorted(extra)}')
    if job.get('needs') not in (BUILD, [BUILD]):
        bad.append(f'{SIGN} must need {BUILD} and nothing else')
    if ' '.join(str(job.get('if', '')).split()) != CONDITION:
        bad.append(f'{SIGN} must run exactly when {CONDITION}')
    if job.get('environment') != ENVIRONMENT:
        bad.append(f"{SIGN} must always run in the '{ENVIRONMENT}' environment")
    if job.get('permissions') != {'contents': 'read', IDT: 'write'}:
        bad.append(f'{SIGN} must have exactly contents: read and {IDT}: write')
    if job.get('defaults') != {'run': {'shell': 'powershell'}}:
        bad.append(f'{SIGN} defaults must be exactly run.shell: powershell')

    steps = job.get('steps') or []
    if len(steps) != len(SEQUENCE):
        bad.append(f'{SIGN} has {len(steps)} steps; the reviewed sequence has {len(SEQUENCE)}')
    for index, (step, expected) in enumerate(zip(steps, SEQUENCE)):
        label = f'{SIGN} step {index + 1} ({step.get("name") or step.get("uses") or "?"})'
        for key in ('shell', 'working-directory', 'continue-on-error'):
            if key in step:
                bad.append(f'{label} sets {key}')
        kind = expected[0]
        if kind == 'uses':
            _, action, inputs = expected
            if set(step) - USES_STEP_KEYS:
                bad.append(f'{label} has keys that are not allowed on an action step: {sorted(set(step) - USES_STEP_KEYS)}')
            if step.get('uses') != action:
                bad.append(f'{label} must use {action}')
            if (step.get('with') or {}) != inputs:
                bad.append(f'{label} must give exactly the inputs {inputs}')
            continue
        _, command, env = expected
        if set(step) - RUN_STEP_KEYS:
            bad.append(f'{label} has keys that are not allowed on a run step: {sorted(set(step) - RUN_STEP_KEYS)}')
        run = step.get('run')
        if not isinstance(run, str) or any(c in run for c in UNSAFE) or run != command:
            bad.append(f'{label} must be exactly: {command}')
        if (step.get('env') or None) != env:
            bad.append(f'{label} must have exactly the env {env}')
    return bad


def code_of(text):
    # The script without its comment blocks and comment lines, which run nothing.
    text = re.sub(r'<#.*?#>', '', text, flags=re.DOTALL)
    return '\n'.join(line for line in text.split('\n') if not line.lstrip().startswith('#'))


def check_scripts(texts):
    bad = []
    for path in SCRIPTS:
        text = texts.get(path)
        if text is None:
            bad.append(f'{path} is missing')
            continue
        code = code_of(text)
        for needed in MUST_CONTAIN.get(path, []):
            if needed not in code:
                bad.append(f'{path} no longer contains: {needed}')
        for pattern, what in NEVER:
            match = pattern.search(code)
            if match:
                bad.append(f'{path} {what}: {match.group(0)!r}')
    return bad


def check_tools(text):
    bad = []
    try:
        packages = json.loads(text)['packages']
    except (ValueError, KeyError, TypeError):
        return ['installer/tools.json is not a list of packages']
    ids = [p.get('id') for p in packages]
    if sorted(ids) != sorted(PACKAGES):
        bad.append(f'installer/tools.json must pin exactly {sorted(PACKAGES)}, once each')
    for p in packages:
        if not re.fullmatch(r'\d+\.\d+\.\d+', str(p.get('version'))):
            bad.append(f'{p.get("id")} is not pinned to one exact version')
        if not re.fullmatch(r'[0-9a-f]{64}', str(p.get('sha256'))):
            bad.append(f'{p.get("id")} has no lowercase SHA-256 pinned')
    versions = {p.get('version') for p in packages if p.get('id') in WIX_FAMILY}
    if len(versions) > 1:
        bad.append('WiX and its extensions must be the same version')
    return bad


def check_authoring(path, text):
    bad = []
    for match in re.finditer(r'<\?(\S+)', text):
        if match.group(1) != 'xml':
            bad.append(f'{path} has a preprocessor directive <?{match.group(1)}')
    if re.search(r'<!(DOCTYPE|ENTITY)', text):
        # Not parsed at all: an entity is how XML expands or fetches what the file does not say.
        return bad + [f'{path} declares a document type or entity']
    for match in re.finditer(r'\$\(([^)]*)\)', text):
        ref = match.group(1)
        prefix, _, name = ref.partition('.')
        if prefix != 'var' or name not in AUTHORING_VARS:
            bad.append(f'{path} uses $({ref}); only $(var.X) for {sorted(AUTHORING_VARS)} is allowed')
    try:
        root = ET.fromstring(text)
    except ET.ParseError as error:
        return bad + [f'{path} is not well-formed XML: {error}']
    for element in root.iter():
        namespace, _, local = element.tag[1:].partition('}') if element.tag.startswith('{') else ('', '', element.tag)
        if namespace not in NAMESPACES:
            bad.append(f'{path} uses an element from {namespace or "no namespace"}: {local}')
        for attribute in element.attrib:
            if attribute.startswith('{') and attribute[1:].partition('}')[0] not in NAMESPACES:
                bad.append(f'{path} uses an attribute from another namespace: {attribute}')
        if local == 'Binary':
            bad.append(f'{path} embeds a binary of its own')
        if local == 'CustomAction':
            keys = set(element.attrib)
            error_only = keys == {'Id', 'Error'}
            quiet_exec = (keys == QUIET_EXEC and element.get('BinaryRef') == 'Wix4UtilCA_X64'
                          and element.get('DllEntry') == 'WixQuietExec' and element.get('Execute') == 'deferred')
            if not (error_only or quiet_exec):
                bad.append(f'{path} has custom action {element.get("Id")} that is neither an error message nor the WiX quiet-exec')
    return bad


def check(workflow, files):
    bad = check_workflow(workflow)
    bad += check_scripts(files)
    bad += check_tools(files.get('installer/tools.json', ''))
    for path in AUTHORING:
        bad += check_authoring(path, files.get(path, ''))
    return bad


# --- The samples ---------------------------------------------------------------------------------

def sample_workflow():
    steps = []
    for kind, value, extra in SEQUENCE:
        if kind == 'uses':
            steps.append({'name': value.split('@')[0], 'uses': value, 'with': copy.deepcopy(extra)})
        else:
            step = {'name': value.split(' ')[0], 'run': value}
            if extra:
                step['env'] = dict(extra)
            steps.append(step)
    return {
        'permissions': {'contents': 'read'},
        'env': {'DOTNET_NOLOGO': '1'},
        'jobs': {
            BUILD: {
                'runs-on': HOSTED,
                'defaults': {'run': {'shell': 'powershell'}},
                'steps': [
                    {'uses': CHECKOUT, 'with': {'persist-credentials': False}},
                    {'run': './installer/build.ps1 -Stage Payload'},
                    {'if': CONDITION, 'uses': UPLOAD, 'with': {'name': 'screentail-installer-payload', 'path': PAYLOAD}},
                ],
            },
            SIGN: {
                'name': 'signing',
                'needs': BUILD,
                'if': CONDITION,
                'runs-on': HOSTED,
                'environment': ENVIRONMENT,
                'permissions': {'contents': 'read', IDT: 'write'},
                'timeout-minutes': 30,
                'defaults': {'run': {'shell': 'powershell'}},
                'steps': steps,
            },
            CHECK: {
                'needs': SIGN,
                'runs-on': HOSTED,
                'steps': [
                    {'uses': CHECKOUT, 'with': {'ref': expr('github.sha'), 'persist-credentials': False}},
                    {'uses': DOWNLOAD, 'with': {'name': 'screentail-installer', 'path': 'x'}},
                    {'run': './installer/smoke-test.ps1'},
                ],
            },
            'other': {'runs-on': 'ubuntu-latest', 'permissions': {'checks': 'read'}, 'steps': [{'env': {'GH_TOKEN': expr('github.token')}, 'run': 'gh api x'}]},
        },
    }


SAMPLE_FILES = {
    'installer/tools.ps1': "\n".join([
        "function Get-Dotnet { Join-Path $env:ProgramFiles 'dotnet\\dotnet.exe' }",
        "$hash = (Get-FileHash -Algorithm SHA256 -Path $package).Hash",
        "if ($hash -ne $pin.sha256) {",
        "& $dotnet nuget verify $package --all | Out-Host",
    ]),
    'installer/package.ps1': "\n".join([
        "# build.ps1 calls this. Run build.ps1 -Stage Payload first.",
        "Remove-Item Env:ACTIONS_ID_TOKEN_REQUEST_URL, Env:ACTIONS_ID_TOKEN_REQUEST_TOKEN -ErrorAction SilentlyContinue",
        ". (Join-Path $PSScriptRoot 'tools.ps1')",
        "$wix = Join-Path (Get-PinnedPackage 'wix' $tools) 'tools\\net6.0\\any\\wix.dll'",
        "$util = Get-Extension 'WixToolset.Util.wixext' 'wixext5\\WixToolset.Util.wixext.dll'",
        "$bal = Get-Extension 'WixToolset.Bal.wixext' 'wixext5\\WixToolset.BootstrapperApplications.wixext.dll'",
        "& $dotnet $wix @Arguments | Out-Host",
    ]),
    'installer/sign.ps1': "\n".join([
        ". (Join-Path $PSScriptRoot 'tools.ps1')",
        "$dlib = Join-Path (Get-PinnedPackage 'Microsoft.ArtifactSigning.Client' $Into) 'bin\\x64\\Azure.CodeSigning.Dlib.dll'",
    ]),
    'installer/tools.json': json.dumps({'packages': [
        {'id': 'wix', 'version': '5.0.2', 'sha256': 'a' * 64},
        {'id': 'WixToolset.Util.wixext', 'version': '5.0.2', 'sha256': 'b' * 64},
        {'id': 'WixToolset.Bal.wixext', 'version': '5.0.2', 'sha256': 'c' * 64},
        {'id': 'Microsoft.ArtifactSigning.Client', 'version': '1.0.146', 'sha256': 'd' * 64},
    ]}),
    'installer/Package.wxs': """<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:util="http://wixtoolset.org/schemas/v4/wxs/util">
  <Package Name="ScreenTail" Version="$(var.Version)">
    <CustomAction Id="Refuse" Error="No." />
    <CustomAction Id="Clean" BinaryRef="Wix4UtilCA_X64" DllEntry="WixQuietExec" Execute="deferred" Impersonate="no" Return="ignore" />
    <Files Include="$(var.Payload)\\**" />
    <util:CloseApplication Id="C" Target="x.exe" />
  </Package>
</Wix>
""",
    'installer/Bundle.wxs': """<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:bal="http://wixtoolset.org/schemas/v4/wxs/bal">
  <Bundle Name="ScreenTail" Version="$(var.Version)">
    <BootstrapperApplication><bal:WixInternalUIBootstrapperApplication /></BootstrapperApplication>
    <Chain><MsiPackage SourceFile="$(var.Msi)" /></Chain>
  </Bundle>
</Wix>
""",
}


def sign_steps(w):
    return w['jobs'][SIGN]['steps']


def first_sign(w):
    return sign_steps(w)[2]


def first_build(w):
    return sign_steps(w)[3]


def edit_file(path, change):
    return lambda w, f: f.__setitem__(path, change(f[path]))


def on_workflow(change):
    return lambda w, f: change(w)


MUST_FAIL = {
    # The signing job's commands.
    'a sign step with a separator': on_workflow(lambda w: first_sign(w).update(run=first_sign(w)['run'] + '; curl evil')),
    'a sign step with a newline': on_workflow(lambda w: first_sign(w).update(run=first_sign(w)['run'] + '\ncurl evil')),
    'a sign step with a pipe': on_workflow(lambda w: first_sign(w).update(run=first_sign(w)['run'] + ' | tee y')),
    'a sign step with a subexpression': on_workflow(lambda w: first_sign(w).update(run='./installer/sign.ps1 -Path $(evil)')),
    'a sign step with a backtick': on_workflow(lambda w: first_sign(w).update(run='./installer/sign.ps1 -Path `evil')),
    'a sign step with another flag': on_workflow(lambda w: first_sign(w).update(run='./installer/sign.ps1 -Query')),
    'the package built before the payload is signed': on_workflow(lambda w: sign_steps(w).insert(2, sign_steps(w).pop(3))),
    'the build script in the signing job': on_workflow(lambda w: sign_steps(w).insert(2, {'run': './installer/build.ps1 -Stage Payload'})),
    'a restore in the signing job': on_workflow(lambda w: sign_steps(w).insert(2, {'run': 'dotnet restore'})),
    'a step dropped from the signing job': on_workflow(lambda w: sign_steps(w).pop(7)),
    'signing settings on a package step': on_workflow(lambda w: first_build(w).update(env={'SCREENTAIL_SIGNING_ENDPOINT': 'x'})),
    'an extra variable on a sign step': on_workflow(lambda w: first_sign(w)['env'].update(PSModulePath='x')),
    'a sign step without the require switch': on_workflow(lambda w: first_sign(w)['env'].pop('SCREENTAIL_REQUIRE_SIGNING')),
    # How a step runs.
    'a custom shell on a step': on_workflow(lambda w: first_sign(w).update(shell='bash')),
    'a working folder on a step': on_workflow(lambda w: first_build(w).update(**{'working-directory': 'elsewhere'})),
    'continue-on-error on a step': on_workflow(lambda w: first_sign(w).update(**{'continue-on-error': True})),
    'another shell in the job defaults': on_workflow(lambda w: w['jobs'][SIGN].update(defaults={'run': {'shell': 'pwsh'}})),
    'a working folder in the job defaults': on_workflow(lambda w: w['jobs'][SIGN]['defaults']['run'].update(**{'working-directory': 'x'})),
    'workflow-level defaults': on_workflow(lambda w: w.update(defaults={'run': {'shell': 'bash'}})),
    'an if on a run step': on_workflow(lambda w: first_build(w).update(**{'if': 'always()'})),
    'an id on a run step': on_workflow(lambda w: first_build(w).update(id='x')),
    'a timeout on a run step': on_workflow(lambda w: first_sign(w).update(**{'timeout-minutes': 5})),
    'an env on an action step': on_workflow(lambda w: sign_steps(w)[0].update(env={'X': '1'})),
    # The actions.
    'an unpinned action in the signing job': on_workflow(lambda w: sign_steps(w)[0].update(uses='actions/checkout@v7')),
    'a third-party action in the signing job': on_workflow(lambda w: sign_steps(w)[0].update(uses='someone/login@' + 'a' * 40)),
    'another input on the checkout': on_workflow(lambda w: sign_steps(w)[0]['with'].update(repository='someone/else')),
    'a checkout that keeps its credentials': on_workflow(lambda w: sign_steps(w)[0]['with'].update(**{'persist-credentials': True})),
    'a checkout of another commit': on_workflow(lambda w: sign_steps(w)[0]['with'].update(ref='main')),
    'a payload from another run': on_workflow(lambda w: sign_steps(w)[1]['with'].update(**{'run-id': '1'})),
    'an unpinned action in the build job': on_workflow(lambda w: w['jobs'][BUILD]['steps'].append({'uses': 'actions/upload-artifact@v7'})),
    'an unpinned action in the signed check': on_workflow(lambda w: w['jobs'][CHECK]['steps'].append({'uses': 'actions/checkout@v7'})),
    'another action in the build job': on_workflow(lambda w: w['jobs'][BUILD]['steps'].append({'uses': 'someone/cache@' + 'b' * 40})),
    'another input on the build checkout': on_workflow(lambda w: w['jobs'][BUILD]['steps'][0]['with'].update(token='x')),
    # The job.
    'signing on pull requests': on_workflow(lambda w: w['jobs'][SIGN].update(**{'if': "github.ref == 'refs/heads/main'"})),
    'a condition that is always true': on_workflow(lambda w: w['jobs'][SIGN].update(**{'if': CONDITION + ' || true'})),
    'signing on the laptop': on_workflow(lambda w: w['jobs'][SIGN].update(**{'runs-on': ['self-hosted', 'Windows']})),
    'the build on the laptop': on_workflow(lambda w: w['jobs'][BUILD].update(**{'runs-on': 'self-hosted'})),
    'the signed check on the laptop': on_workflow(lambda w: w['jobs'][CHECK].update(**{'runs-on': 'self-hosted'})),
    'no signing environment': on_workflow(lambda w: w['jobs'][SIGN].pop('environment')),
    'a conditional signing environment': on_workflow(lambda w: w['jobs'][SIGN].update(environment=expr("vars.X && 'signing' || ''"))),
    'another environment': on_workflow(lambda w: w['jobs'][SIGN].update(environment='production')),
    'signing without the build': on_workflow(lambda w: w['jobs'][SIGN].update(needs=[])),
    'a broader permission': on_workflow(lambda w: w['jobs'][SIGN]['permissions'].update(contents='write')),
    'another permission': on_workflow(lambda w: w['jobs'][SIGN]['permissions'].update(packages='write')),
    'a job env on the signing job': on_workflow(lambda w: w['jobs'][SIGN].update(env={'X': '1'})),
    'continue-on-error on the signing job': on_workflow(lambda w: w['jobs'][SIGN].update(**{'continue-on-error': True})),
    'a container for the signing job': on_workflow(lambda w: w['jobs'][SIGN].update(container='evil/image')),
    'the signing job missing': on_workflow(lambda w: w['jobs'].pop(SIGN)),
    'the signed check missing': on_workflow(lambda w: w['jobs'].pop(CHECK)),
    'the signed check not after signing': on_workflow(lambda w: w['jobs'][CHECK].update(needs=BUILD)),
    # Tokens and settings elsewhere.
    'an OIDC token for the build job': on_workflow(lambda w: w['jobs'][BUILD].update(permissions={IDT: 'write'})),
    'an OIDC token for the signed check': on_workflow(lambda w: w['jobs'][CHECK].update(permissions={'contents': 'read', IDT: 'write'})),
    'every permission for another job': on_workflow(lambda w: w['jobs']['other'].update(permissions='write-all')),
    'an OIDC token for the whole workflow': on_workflow(lambda w: w['permissions'].update(**{IDT: 'write'})),
    'a signing setting in a job env': on_workflow(lambda w: w['jobs'][BUILD].update(env={'AZURE_TENANT_ID': 'x'})),
    'a signing setting in the workflow env': on_workflow(lambda w: w['env'].update(SCREENTAIL_SIGNING_PROFILE='x')),
    'a signing setting on a build step': on_workflow(lambda w: w['jobs'][BUILD]['steps'][1].update(env={'AZURE_CLIENT_ID': 'x'})),
    'a signing variable read by a build step': on_workflow(lambda w: w['jobs'][BUILD]['steps'][1].update(run='echo ' + expr('vars.AZURE_CLIENT_ID'))),
    'the context in a build step': on_workflow(lambda w: w['jobs'][BUILD]['steps'][1].update(run='echo ' + expr(CTX + '.X'))),
    'the context by index': on_workflow(lambda w: w['jobs'][BUILD]['steps'].append({'run': 'echo ' + expr(CTX + "['X']")})),
    'the whole context as JSON': on_workflow(lambda w: w['jobs']['other']['steps'].append({'env': {'ALL': expr('toJSON(' + CTX + ')')}, 'run': 'x'})),
    'the context behind a quoted brace': on_workflow(lambda w: w['jobs']['other']['steps'].append({'run': 'echo ' + expr("format('{0}', " + CTX + ".X)")})),
    'the context in a step name': on_workflow(lambda w: w['jobs']['other']['steps'].append({'name': expr(CTX + '.X'), 'run': 'x'})),
    'the context in a job key': on_workflow(lambda w: w['jobs']['other'].update(**{'runs-on': expr(CTX + '.RUNNER')})),
    'the context in the signing job': on_workflow(lambda w: first_sign(w)['env'].update(AZURE_CLIENT_ID=expr(CTX + '.AZURE_CLIENT_ID'))),
    # The scripts the signing job runs.
    'a build hidden after a comment block': edit_file('installer/package.ps1', lambda t: t + '\n<# note #> dotnet build x.csproj'),
    'a tool restore in package.ps1': edit_file('installer/package.ps1', lambda t: t + '\ndotnet tool restore'),
    'a build in package.ps1': edit_file('installer/package.ps1', lambda t: t + '\n& $dotnet build x.csproj'),
    'the build script from sign.ps1': edit_file('installer/sign.ps1', lambda t: t + "\n& (Join-Path $PSScriptRoot 'build.ps1')"),
    'the smoke test from package.ps1': edit_file('installer/package.ps1', lambda t: t + '\n& ./installer/smoke-test.ps1'),
    'a string run as code': edit_file('installer/tools.ps1', lambda t: t + '\nInvoke-Expression $x'),
    'no hash check': edit_file('installer/tools.ps1', lambda t: t.replace('if ($hash -ne $pin.sha256) {', '')),
    'no signature check': edit_file('installer/tools.ps1', lambda t: t.replace('nuget verify', 'nuget help')),
    'WiX not from the pinned download': edit_file('installer/package.ps1', lambda t: t.replace("Get-PinnedPackage 'wix' $tools", "Get-ChildItem artifacts")),
    'the token variables kept for WiX': edit_file('installer/package.ps1', lambda t: t.replace('Remove-Item Env:ACTIONS_ID_TOKEN_REQUEST_URL', 'Write-Host')),
    'a missing script': lambda w, f: f.pop('installer/tools.ps1'),
    # The pins.
    'a package without a hash': edit_file('installer/tools.json', lambda t: t.replace('"' + 'a' * 64 + '"', '""')),
    'a short hash': edit_file('installer/tools.json', lambda t: t.replace('a' * 64, 'a' * 40)),
    'an uppercase hash': edit_file('installer/tools.json', lambda t: t.replace('a' * 64, 'A' * 64)),
    'a version range': edit_file('installer/tools.json', lambda t: t.replace('"1.0.146"', '"[1.0,)"')),
    'an extra package': edit_file('installer/tools.json', lambda t: t.replace('"packages": [', '"packages": [{"id": "Evil", "version": "1.0.0", "sha256": "' + 'e' * 64 + '"}, ')),
    'a missing package': edit_file('installer/tools.json', lambda t: json.dumps({'packages': json.loads(t)['packages'][:3]})),
    'WiX and an extension at different versions': edit_file('installer/tools.json', lambda t: t.replace('"5.0.2", "sha256": "' + 'b' * 64, '"5.0.3", "sha256": "' + 'b' * 64)),
    # The authoring.
    'a custom action that runs a program': edit_file('installer/Package.wxs', lambda t: t.replace('<CustomAction Id="Refuse" Error="No." />', '<CustomAction Id="Run" Directory="X" ExeCommand="calc.exe" />')),
    'a script custom action': edit_file('installer/Package.wxs', lambda t: t.replace('<CustomAction Id="Refuse" Error="No." />', '<CustomAction Id="Run" Script="vbscript">x</CustomAction>')),
    'a quiet-exec from another binary': edit_file('installer/Package.wxs', lambda t: t.replace('Wix4UtilCA_X64', 'Mine')),
    'a binary of its own': edit_file('installer/Package.wxs', lambda t: t.replace('<Files', '<Binary Id="B" SourceFile="x.dll" /><Files')),
    'an include': edit_file('installer/Package.wxs', lambda t: t.replace('<Wix ', '<?include x.wxi ?>\n<Wix ')),
    'a define': edit_file('installer/Bundle.wxs', lambda t: t.replace('<Wix ', '<?define X = 1 ?>\n<Wix ')),
    'an environment variable': edit_file('installer/Package.wxs', lambda t: t.replace('$(var.Payload)', '$(env.PAYLOAD)')),
    'a document type': edit_file('installer/Bundle.wxs', lambda t: t.replace('<Wix ', '<!DOCTYPE Wix [<!ENTITY x "y">]>\n<Wix ')),
    'another extension': edit_file('installer/Bundle.wxs', lambda t: t.replace('<Chain>', '<Chain xmlns:x="http://example.com/ext"><x:Thing />')),
}

CAUGHT = [expr("format('{0}', " + CTX + ".X)"), expr(CTX + "['X']"), expr('toJSON(' + CTX + ')'),
          expr(CTX.upper()[:3] + CTX[3:] + '.X'), expr('toJSON(  ' + CTX + '  )'), expr("format('" + CLOSE + "', " + CTX + ".X)"),
          'pre ' + expr('github.sha') + ' mid ' + expr(CTX + '.Y')]
PASSED = ['rotate the ' + CTX + ' every quarter', 'echo none here # ' + CTX, expr("'" + CTX + ".X'"),
          expr('vars.AZURE_CLIENT_ID'), expr("format('" + CTX + " {0}', github.sha)")]


def self_test():
    bad = []
    for text in CAUGHT:
        if not names_context(text):
            bad.append(f'the expression scanner missed {text!r}')
    for text in PASSED:
        if names_context(text):
            bad.append(f'the expression scanner flagged {text!r}, which names no such context')
    good = check(sample_workflow(), dict(SAMPLE_FILES))
    if good:
        bad.append(f'the made-up good sample failed: {good}')
    for label, change in MUST_FAIL.items():
        workflow, files = sample_workflow(), dict(SAMPLE_FILES)
        change(workflow, files)
        if not check(workflow, files):
            bad.append(f'the check passed a sample with {label}')
    return bad


def main():
    root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else '.')
    selfbad = self_test()
    if selfbad:
        for line in selfbad:
            print(f'::error file=scripts/ci/check_installer_jobs.py::{line}')
        return 1
    print(f'self-test: the scanner caught {len(CAUGHT)} and passed {len(PASSED)}; the good sample passed and all {len(MUST_FAIL)} bad ones failed')

    workflow = yaml.safe_load((root / '.github/workflows/ci.yml').read_text())
    files = {}
    for path in SCRIPTS + AUTHORING + ('installer/tools.json',):
        file = root / path
        if file.exists():
            files[path] = file.read_text()
    bad = check(workflow, files)
    for line in bad:
        print(f'::error file=.github/workflows/ci.yml::{line}')
    print('\n'.join(bad) if bad else 'the installer jobs are on hosted runners, only main can sign, and the signing job runs exactly the reviewed steps')
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
