"""Exercise the real native sampling loop against this test's own process.

No MQTT traffic or external broker is needed: only OS-dependent metadata helpers
are replaced. Sampling, clocks, readiness, end markers and fence checks are real.
"""
import json
import os
import pathlib
import shutil
import subprocess
import tempfile
import time
import unittest


POWERSHELL = os.environ.get('MQB_STAND_SHELL') or shutil.which('pwsh')
SCRIPT = pathlib.Path(__file__).resolve().parents[1] / 'BrokerStand.ps1'
HARNESS = r'''
param([string]$Source, [string]$OutputDirectory, [int]$CaptureSeconds, [bool]$WaitForFence)
$ErrorActionPreference = 'Stop'
$ast = [Management.Automation.Language.Parser]::ParseFile($Source, [ref]$null, [ref]$null)
$node = $ast.Find({ param($x) $x -is [Management.Automation.Language.FunctionDefinitionAst] -and $x.Name -eq 'Write-CaptureNative' }, $true)
Invoke-Expression $node.Extent.Text
function Write-JsonFile($value, [string]$path) { $value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding utf8NoBOM }
function Publish-NativeArtifact { }
function Get-NativeNetworkCounters { @{} }
function Get-NativeDiskIo { @{} }
function Get-NativeConnections { 0 }
$script:run = @{ unitId = "$PID"; requestedImage = (Get-Process -Id $PID).Path; imageId = 'test';
    affinityCpus = @(); memoryLimitBytes = 0; memoryLimitEnforcement = 'test';
    monotonicEpoch = [Diagnostics.Stopwatch]::GetTimestamp(); monotonicFrequency = [Diagnostics.Stopwatch]::Frequency;
    inputHashes = @(); effectiveConfigs = @() }
function Get-NativeRun { $script:run }
$inventory = @{cpuMode = 'singleCorePinned'; campaign = @{}}
$broker = @{name = 'CaptureProtocolTest'; mqttUri = 'mqtt://127.0.0.1:1'}
$CaptureUntilFence = $WaitForFence
try { Write-CaptureNative $broker } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
'''


@unittest.skipUnless(POWERSHELL, 'PowerShell 7 is required')
class NativeCaptureTests(unittest.TestCase):
    def run_capture(self, wait, seconds, callback=None, timeout=45, legacy_optin=False):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            harness = root / 'capture.ps1'
            harness.write_text(HARNESS, encoding='utf-8')
            env = dict(os.environ)
            env.pop('MQB_NATIVE_CAPTURE_UNTIL_FENCE', None)
            if legacy_optin:
                env['MQB_NATIVE_CAPTURE_UNTIL_FENCE'] = '1'
            # Use a script file to avoid command-line quoting differences between
            # PowerShell hosts on Windows and Linux.
            harness.write_text(HARNESS.replace('[bool]$WaitForFence', '[int]$WaitForFence'), encoding='utf-8')
            command = [POWERSHELL, '-NoProfile', '-File', str(harness), '-Source', str(SCRIPT),
                '-OutputDirectory', str(root), '-CaptureSeconds', str(seconds), '-WaitForFence', '1' if wait else '0']
            with subprocess.Popen(command, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE) as process:
                try:
                    if callback:
                        until = time.monotonic() + 10
                        while not (root / 'telemetry-ready.json').exists():
                            if process.poll() is not None or time.monotonic() >= until:
                                self.fail('Capture did not become ready')
                            time.sleep(.025)
                        callback(root)
                    stdout, stderr = process.communicate(timeout=timeout)
                    return process.returncode, stderr.decode('utf-8', errors='replace'), {
                        p.name: json.loads(p.read_text(encoding='utf-8-sig'))
                        for p in root.glob('*.json')}, (root / 'telemetry.ndjson').read_text(encoding='utf-8-sig')
                finally:
                    if process.poll() is None:
                        process.kill()
                        process.wait()

    def test_late_completion_is_covered_after_the_nominal_envelope(self):
        def fence(root):
            time.sleep(3.2)
            self.assertFalse((root / 'sampler-ended.json').exists())
            ready = json.loads((root / 'telemetry-ready.json').read_text(encoding='utf-8-sig'))
            latest = json.loads((root / 'telemetry.ndjson').read_text(encoding='utf-8-sig').splitlines()[-1])
            (root / 'workload-end-fence.json').write_text(json.dumps({
                'unitId': ready['unitId'], 'monotonicSeconds': latest['monotonicSeconds'] + .1}))
        code, error, files, raw = self.run_capture(True, 2, fence)
        self.assertEqual(0, code, error)
        end, fence = files['sampler-ended.json'], files['workload-end-fence.json']
        self.assertGreater(end['sampleCount'], 2)
        self.assertGreaterEqual(end['lastSampleMonotonicSeconds'], fence['monotonicSeconds'])
        self.assertEqual('workload-fence', end['completionMode'])

    def test_wrong_process_fence_is_rejected(self):
        def fence(root):
            (root / 'workload-end-fence.json').write_text(json.dumps({'unitId': 'not-this-process', 'monotonicSeconds': 0}))
        code, error, files, raw = self.run_capture(True, 2, fence)
        self.assertNotEqual(0, code)
        self.assertIn('another broker process', error)
        self.assertNotIn('sampler-ended.json', files)

    def test_missing_fence_fails_at_the_bounded_limit(self):
        code, error, files, raw = self.run_capture(True, 1)
        self.assertNotEqual(0, code)
        self.assertIn('bounded capture limit', error)
        self.assertEqual(34, len(raw.splitlines()))
        self.assertNotIn('sampler-ended.json', files)

    def test_fixed_capture_remains_fixed(self):
        code, error, files, raw = self.run_capture(False, 2)
        self.assertEqual(0, code, error)
        self.assertEqual(2, files['sampler-ended.json']['sampleCount'])
        self.assertEqual('fixed-samples', files['sampler-ended.json']['completionMode'])

    def test_legacy_controller_optin_keeps_one_sample_preflight_fixed(self):
        def fence(root):
            time.sleep(2.2)
            self.assertFalse((root / 'sampler-ended.json').exists())
            ready = json.loads((root / 'telemetry-ready.json').read_text(encoding='utf-8-sig'))
            (root / 'workload-end-fence.json').write_text(json.dumps({'unitId': ready['unitId'], 'monotonicSeconds': 0}))
        code, error, files, raw = self.run_capture(False, 2, fence, legacy_optin=True)
        self.assertEqual(0, code, error)
        self.assertEqual('workload-fence', files['sampler-ended.json']['completionMode'])
        code, error, files, raw = self.run_capture(False, 1, legacy_optin=True)
        self.assertEqual(0, code, error)
        self.assertEqual('fixed-samples', files['sampler-ended.json']['completionMode'])


if __name__ == '__main__':
    unittest.main()
