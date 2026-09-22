param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$SnapshotOnly,
    [string]$BeforeSnapshot,
    [ValidateRange(0, 120)][int]$WaitSeconds = 45,
    [ValidateRange(1, 30)][int]$ExpectedProfiles = 4
)
# Read-only host integration evidence. Requires Python 3.11+ for its standard TOML parser.
# Reads only provider configuration files and the Deskweave router ticket; never auth files.
# Does not invoke provider CLIs, connect to a pipe, launch an app, or change configuration.
# Run -SnapshotOnly before publishing. After publishing, pass its output directory with
# -BeforeSnapshot to wait for auto-connection and prove unrelated parsed values survived.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exe = Join-Path $root 'out\Deskweave.exe'
$app = @(Get-Process Deskweave -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
if ($app.Count -ne 1) { throw 'Expected exactly one running Deskweave from this checkout''s out directory.' }
$version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
$bridge = Join-Path $root 'out\Bridge\Deskweave.WorkspaceBridge.exe'
$ticket = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Deskweave\agent-workspaces.access\router.json'
$userProfile = [Environment]::GetFolderPath('UserProfile')
if (-not (Get-Command python -ErrorAction SilentlyContinue)) { throw 'Python 3.11 or newer is required for independent JSON/TOML inspection.' }
$mode = if ($SnapshotOnly) { 'snapshot' } else { 'verify' }
# The fixed script is passed on stdin; configuration contents never enter shell arguments/output.
$inspect = @'
import ctypes
import datetime
import hashlib
import json
import ntpath
import os
from pathlib import Path
import sys
import time
import tomllib

root, output_arg, mode, before_arg, wait_arg, count_arg, user, bridge, ticket, app_pid, version = sys.argv[1:]
root = Path(root).resolve()
output = Path(output_arg).resolve()
artifact_root = (root / 'artifacts').resolve()
if not output.is_relative_to(artifact_root) or output == artifact_root:
    raise SystemExit('Output must be a fresh folder beneath this checkout artifacts directory.')
if output.exists():
    raise SystemExit('Choose a fresh output folder; existing evidence is never replaced.')
output.mkdir(parents=True)
report_path = output / 'profiles-report.json'
checks = []
report = {
    'schema': 1, 'observedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'mode': mode, 'app': {'path': str(root / 'out' / 'Deskweave.exe'), 'pid': int(app_pid), 'version': version},
    'expectedBridge': bridge, 'expectedRouterTicket': ticket,
    'expectedProfileCount': int(count_arg), 'checks': checks,
    'privacy': 'Only paths, counts, match flags and SHA-256 digests are recorded; no configuration values or ticket capabilities.',
    'scope': 'Default and effective provider configurations plus existing .claude1..9/.codex1..9 configurations. No auth files or recursive profile discovery.',
    'limitations': 'Parsed-value hashes ignore formatting and key ordering. Router availability is a non-connecting pipe observation; it does not prove a model session can use tools.'
}

def check(ok, claim):
    checks.append({'passed': bool(ok), 'claim': claim})

def canonical_hash(value):
    def special(item):
        if isinstance(item, (datetime.datetime, datetime.date, datetime.time)):
            return {'__toml_type__': type(item).__name__, 'value': item.isoformat()}
        raise TypeError('Unhashable parsed configuration value')
    encoded = json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=False, default=special).encode('utf-8')
    return hashlib.sha256(encoded).hexdigest().upper()

def same_path(value, expected):
    return isinstance(value, str) and ntpath.isabs(value) and ntpath.normcase(ntpath.normpath(value)) == ntpath.normcase(ntpath.normpath(expected))

def discover():
    targets = []
    for provider, folder, filename, variable in [
        ('ClaudeCode', '.claude', '.claude.json', 'CLAUDE_CONFIG_DIR'),
        ('Codex', '.codex', 'config.toml', 'CODEX_HOME')
    ]:
        seen = set()
        def add(name, path):
            path = Path(path).absolute()
            key = ntpath.normcase(ntpath.normpath(str(path)))
            if key not in seen:
                targets.append({'provider': provider, 'profile': name, 'configuration': str(path)})
                seen.add(key)
        add('Default', Path(user) / filename if provider == 'ClaudeCode' else Path(user) / folder / filename)
        effective = os.environ.get(variable, '').strip()
        if effective:
            add('Current profile', Path(effective) / filename)
        for number in range(1, 10):
            path = Path(user) / (folder + str(number)) / filename
            if path.is_file():
                add('Profile ' + str(number), path)
    return targets

def inspect(target):
    result = dict(target, exists=False, entryState='missing-configuration')
    path = Path(target['configuration'])
    try:
        if not path.is_file():
            return result
        result['exists'] = True
        # One read supplies both the raw hash and parser, avoiding mixed snapshots of a rewrite.
        raw = path.read_bytes()
        result['fileSha256'] = hashlib.sha256(raw).hexdigest().upper()
        value = json.loads(raw.decode('utf-8-sig')) if target['provider'] == 'ClaudeCode' else tomllib.loads(raw.decode('utf-8-sig'))
        if not isinstance(value, dict):
            raise ValueError('Configuration root is not an object')
        key = 'mcpServers' if target['provider'] == 'ClaudeCode' else 'mcp_servers'
        servers = value.get(key, {})
        if not isinstance(servers, dict):
            raise ValueError('MCP configuration is not an object')
        entry = servers.get('deskweave')
        result['deskweavePresent'] = 'deskweave' in servers
        result['entryState'] = 'missing-entry' if not result['deskweavePresent'] else 'stale'
        if isinstance(entry, dict):
            arguments = entry.get('args')
            result['bridgeMatches'] = same_path(entry.get('command'), bridge)
            result['routerArgumentsMatch'] = isinstance(arguments, list) and len(arguments) == 2 and arguments[0] == '--workspace' and same_path(arguments[1], ticket)
            result['enabled'] = entry.get('enabled', True) is not False and entry.get('disabled', False) is not True
            result['stdio'] = entry.get('type', 'stdio') == 'stdio' and 'url' not in entry
            if all(result[key] for key in ('bridgeMatches', 'routerArgumentsMatch', 'enabled', 'stdio')):
                result['entryState'] = 'current'
        unrelated = {name: content for name, content in servers.items() if name != 'deskweave'}
        other_settings = {name: content for name, content in value.items() if name != key}
        result['unrelatedMcpCount'] = len(unrelated)
        result['unrelatedMcpSha256'] = canonical_hash(unrelated)
        result['otherSettingsSha256'] = canonical_hash(other_settings)
        result['unrelatedConfigurationSha256'] = canonical_hash({'mcp': unrelated, 'settings': other_settings})
    except Exception as error:
        # Parser messages can quote source lines. Record only the type, never the message.
        result['entryState'] = 'unreadable'
        result['errorType'] = type(error).__name__
    return result

def router_status():
    status = {'exists': Path(ticket).is_file(), 'readable': False, 'pipeExists': False}
    try:
        raw = Path(ticket).read_bytes()
        if len(raw) > 4096:
            return status
        value = json.loads(raw.decode('utf-8-sig'))
        pipe = value.get('pipe')
        if not isinstance(pipe, str) or not pipe.startswith('Deskweave.Workspace.') or not isinstance(value.get('capability'), str):
            return status
        status['readable'] = True
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        wait_pipe = kernel.WaitNamedPipeW
        wait_pipe.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32]
        wait_pipe.restype = ctypes.c_int
        available = wait_pipe('\\\\.\\pipe\\' + pipe, 1)
        error = ctypes.get_last_error() if not available else 0
        # Busy or timeout still proves that a named-pipe server exists, without connecting.
        status['pipeExists'] = bool(available) or error in (121, 231)
        status['observationCode'] = error
    except Exception as error:
        status['errorType'] = type(error).__name__
    return status

try:
    before = None
    if before_arg != '-':
        before_path = Path(before_arg).resolve()
        if before_path.is_dir():
            before_path /= 'profiles-report.json'
        if not before_path.is_relative_to(artifact_root):
            raise ValueError('Before snapshot must be inside this checkout artifacts directory')
        before = json.loads(before_path.read_text(encoding='utf-8-sig'))
        report['beforeSnapshot'] = str(before_path)
        check(before.get('schema') == 1 and before.get('mode') == 'snapshot' and before.get('status') == 'passed', 'Baseline is a successful snapshot from this fixture')
        check(same_path(before.get('expectedBridge'), bridge) and same_path(before.get('expectedRouterTicket'), ticket), 'Baseline belongs to the same bridge and router ticket paths')
    began = time.monotonic()
    deadline = began + (0 if mode == 'snapshot' else int(wait_arg))
    while True:
        profiles = [inspect(target) for target in discover()]
        router = router_status()
        if mode == 'snapshot' or (profiles and all(profile['entryState'] == 'current' for profile in profiles) and router['pipeExists']) or time.monotonic() >= deadline:
            break
        time.sleep(min(1, max(0, deadline - time.monotonic())))
    report['waitedSeconds'] = round(time.monotonic() - began, 3)
    report['profiles'] = profiles
    report['router'] = router
    report['profileCount'] = len(profiles)
    report['existingConfigurationCount'] = sum(profile['exists'] for profile in profiles)
    report['currentEntryCount'] = sum(profile['entryState'] == 'current' for profile in profiles)
    report['providerCounts'] = {provider: {'total': sum(profile['provider'] == provider for profile in profiles), 'current': sum(profile['provider'] == provider and profile['entryState'] == 'current' for profile in profiles)} for provider in ('ClaudeCode', 'Codex')}
    check(len(profiles) == int(count_arg), 'Discovery finds the expected number of intended provider profiles')
    check(all(profile['exists'] and profile['entryState'] != 'unreadable' for profile in profiles), 'Every intended provider configuration exists and parses independently')
    check(Path(bridge).is_file(), 'Expected published bridge exists')
    check(router['readable'] and router['pipeExists'], 'Current router ticket names an existing pipe without opening a client connection')
    if mode != 'snapshot':
        check(all(profile['entryState'] == 'current' for profile in profiles), 'Every intended profile enables the exact published bridge and router ticket through stdio')
    if before:
        previous = {ntpath.normcase(profile['configuration']): profile for profile in before['profiles']}
        current = {ntpath.normcase(profile['configuration']): profile for profile in profiles}
        check(set(previous) == set(current), 'Profile configuration paths are unchanged from the baseline')
        comparisons = []
        for path, old in previous.items():
            new = current.get(path)
            comparison = {'provider': old['provider'], 'profile': old['profile'], 'configuration': old['configuration']}
            for field in ('unrelatedMcpSha256', 'otherSettingsSha256', 'unrelatedConfigurationSha256'):
                comparison[field + 'Preserved'] = bool(new and old.get(field) and old[field] == new.get(field))
            comparisons.append(comparison)
            check(comparison['unrelatedMcpSha256Preserved'], old['provider'] + ' ' + old['profile'] + ': unrelated MCP entries are unchanged')
            check(comparison['otherSettingsSha256Preserved'], old['provider'] + ' ' + old['profile'] + ': other parsed configuration settings are unchanged')
        report['comparisons'] = comparisons
    report['status'] = 'passed' if all(item['passed'] for item in checks) else 'failed'
except Exception as error:
    report['status'] = 'failed'
    report['failureType'] = type(error).__name__
finally:
    report_path.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
print(report['status'].upper() + ': profile ' + mode + '; report: ' + str(report_path))
if 'profiles' in report:
    print('Profiles: ' + str(report['profileCount']) + '; current Deskweave entries: ' + str(report['currentEntryCount']))
for item in checks:
    print(('PASS ' if item['passed'] else 'FAIL ') + item['claim'])
sys.exit(0 if report['status'] == 'passed' else 1)
'@
$baseline = if ($BeforeSnapshot) { $BeforeSnapshot } else { '-' }
$inspect | & python - $root $OutputDirectory $mode $baseline $WaitSeconds $ExpectedProfiles $userProfile $bridge $ticket $app[0].Id $version
if ($LASTEXITCODE -ne 0) { throw 'Read-only profile validation failed. Inspect profiles-report.json in the output folder; no configuration was changed by this fixture.' }
