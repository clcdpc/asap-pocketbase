"""Test isolated apt setup without network, privilege changes, or host writes."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("install-browser-dependencies.sh").resolve()
SOURCE = """# Preserve runner repository policy.
Types: deb deb-src
URIs: http://azure.archive.ubuntu.com/ubuntu/
Suites: noble noble-updates noble-backports
Components: main restricted universe multiverse
Architectures: amd64
Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg
X-Test-Policy: preserve this field
 and continuation

Types: deb
URIs: mirror+file:/etc/apt/apt-mirrors.txt
Suites: noble-security
Components: main restricted universe multiverse
Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg
"""

FAKE = r'''
import json, os, pathlib, shutil, sys
root = pathlib.Path(os.environ['INSTALL_TEST_ROOT'])
name = pathlib.Path(sys.argv[0]).name
args = sys.argv[1:]
with (root / 'calls').open('a') as stream:
    stream.write(json.dumps({'command': name, 'args': args,
                             'sudo': os.environ.get('INSTALL_TEST_SUDO', '')}) + '\n')
if name == 'sudo':
    if args[0] == '--':
        args = args[1:]
    if args[0] == 'rm':
        target = pathlib.Path(args[-1]).resolve()
        assert target != root and root in target.parents, args
        shutil.rmtree(target)
        sys.exit(int(os.environ['INSTALL_TEST_CLEANUP_STATUS']))
    assert args[0] == 'env', args
    os.execvpe(args[0], args, {**os.environ, 'INSTALL_TEST_SUDO': 'yes'})
elif name == 'node':
    setup = pathlib.Path(os.environ['ASAP_BROWSER_APT_ROOT'])
    assert root in setup.resolve().parents, setup
    snapshot = {'root': str(setup), 'files': {
        str(p.relative_to(setup)): p.read_text() for p in setup.rglob('*') if p.is_file()}}
    (root / 'snapshot').write_text(json.dumps(snapshot))
    sys.exit(int(os.environ['INSTALL_TEST_NATIVE_STATUS']))
elif name == 'npx':
    sys.exit(int(os.environ['INSTALL_TEST_BROWSER_STATUS']))
elif name in ('getent', 'curl'):
    sys.exit(int(os.environ['INSTALL_TEST_DIAGNOSTIC_STATUS']))
else:
    raise AssertionError('Unexpected command: ' + name)
'''


@unittest.skipUnless(os.name == "posix" and shutil.which("bash"), "POSIX Bash required")
class BrowserDependencyTests(unittest.TestCase):
    def run_install(self, source=SOURCE, native=0, browser=0, diagnostic=0, cleanup=0):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            commands = root / "bin"
            commands.mkdir()
            for name in ("sudo", "node", "npx", "getent", "curl", "apt-get", "sleep"):
                path = commands / name
                path.write_text(f"#!{sys.executable}\n" + FAKE)
                path.chmod(0o755)
            original = root / "original.sources"
            if source is not None:
                original.write_text(source)
            env = {**os.environ, "PATH": f"{commands}{os.pathsep}{os.environ['PATH']}",
                   "RUNNER_TEMP": str(root), "TMPDIR": str(root),
                   "INSTALL_TEST_ROOT": str(root), "INSTALL_TEST_SUDO": "",
                   "INSTALL_TEST_NATIVE_STATUS": str(native),
                   "INSTALL_TEST_BROWSER_STATUS": str(browser),
                   "INSTALL_TEST_DIAGNOSTIC_STATUS": str(diagnostic),
                   "INSTALL_TEST_CLEANUP_STATUS": str(cleanup)}
            result = subprocess.run(["bash", str(SCRIPT), str(original)], env=env,
                                    text=True, stdout=subprocess.PIPE,
                                    stderr=subprocess.STDOUT, timeout=15)
            log = root / "calls"
            calls = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
            snapshot_file = root / "snapshot"
            snapshot = json.loads(snapshot_file.read_text()) if snapshot_file.exists() else None
            if snapshot:
                self.assertFalse(Path(snapshot["root"]).exists(), "Temporary apt setup leaked")
            self.assertEqual([p.name for p in root.iterdir() if p.is_dir()], ["bin"], result.stdout)
            if source is not None:
                self.assertEqual(original.read_text(), source, "Original sources changed")
            self.assertFalse(any(c["command"] in ("apt-get", "sleep") for c in calls))
            return result, calls, snapshot

    def test_success_preserves_sources_and_separates_privileges(self):
        result, calls, snapshot = self.run_install()
        self.assertEqual(result.returncode, 0, result.stdout)
        sources = [text for name, text in snapshot["files"].items() if name.endswith(".sources")]
        self.assertEqual(sources, [SOURCE.replace("http://azure.archive.ubuntu.com/ubuntu/",
                                                  "https://archive.ubuntu.com/ubuntu/").replace(
                                                      "mirror+file:/etc/apt/apt-mirrors.txt",
                                                      "https://archive.ubuntu.com/ubuntu/")])
        wrappers = [text for name, text in snapshot["files"].items() if Path(name).name == "apt-get"]
        self.assertEqual(len(wrappers), 1)
        for required in ("/usr/bin/apt-get", "Dir::Etc::sourcelist=$ASAP_BROWSER_APT_ROOT/ubuntu.sources",
                         "Dir::Etc::sourceparts=-", "Dir::State::lists=$ASAP_BROWSER_APT_ROOT/lists",
                         "APT::Update::Error-Mode=any", '"$@"'):
            self.assertIn(required, wrappers[0])
        self.assertNotIn("Acquire::Retries", wrappers[0])
        native = [c for c in calls if c["command"] == "node"]
        self.assertEqual(len(native), 1)
        self.assertEqual(native[0]["sudo"], "yes")
        self.assertEqual(native[0]["args"], [str(SCRIPT.parents[2] / "node_modules/playwright/cli.js"),
                                            "install-deps", "chromium"])
        browser = [c for c in calls if c["command"] == "npx"]
        self.assertEqual(len(browser), 1)
        self.assertEqual(browser[0]["sudo"], "")
        self.assertEqual(browser[0]["args"], ["--no-install", "playwright", "install", "chromium"])
        self.assertLess(calls.index(native[0]), calls.index(browser[0]))
        privileged = [c["args"] for c in calls if c["command"] == "sudo"]
        self.assertEqual(len(privileged), 2)
        self.assertEqual(privileged[0][:2], ["--", "env"])
        self.assertEqual(privileged[0][2].split(":")[0], f"PATH={snapshot['root']}")
        self.assertEqual(privileged[0][3], f"ASAP_BROWSER_APT_ROOT={snapshot['root']}")
        self.assertEqual(privileged[1], ["--", "rm", "-rf", "--", snapshot["root"]])
        self.assertEqual([c["args"] for c in calls if c["command"] == "getent"],
                         [["ahostsv4", host] for host in ("azure.archive.ubuntu.com",
                                                         "archive.ubuntu.com", "security.ubuntu.com")])
        curl = [c for c in calls if c["command"] == "curl"]
        self.assertEqual(len(curl), 1)
        self.assertEqual(curl[0]["args"][-1], "https://archive.ubuntu.com/ubuntu/dists/noble/InRelease")

    def test_rejects_unknown_missing_or_invalid_sources(self):
        for source in (None, "", SOURCE.replace("azure.archive.ubuntu.com", "untrusted.example"),
                       SOURCE.replace("URIs:", "Missing-URIs:"), SOURCE.replace("noble", "jammy"),
                       SOURCE.replace("ubuntu-archive-keyring.gpg", "other-key.gpg")):
            with self.subTest(source=source):
                result, calls, snapshot = self.run_install(source=source)
                self.assertNotEqual(result.returncode, 0)
                self.assertIsNone(snapshot)
                self.assertFalse(any(c["command"] in ("node", "npx") for c in calls))

    def test_native_failure_prevents_download_without_retry(self):
        for status in (7, 100, 130, 143):
            with self.subTest(status=status):
                result, calls, _ = self.run_install(native=status)
                self.assertEqual(result.returncode, status, result.stdout)
                self.assertEqual(sum(c["command"] == "node" for c in calls), 1)
                self.assertFalse(any(c["command"] == "npx" for c in calls))

    def test_browser_failure_is_preserved_without_retry(self):
        for status in (9, 130, 143):
            with self.subTest(status=status):
                result, calls, _ = self.run_install(browser=status)
                self.assertEqual(result.returncode, status, result.stdout)
                self.assertEqual(sum(c["command"] == "node" for c in calls), 1)
                self.assertEqual(sum(c["command"] == "npx" for c in calls), 1)

    def test_diagnostic_failure_does_not_control_install_result(self):
        for native in (0, 23):
            with self.subTest(native=native):
                result, calls, _ = self.run_install(native=native, diagnostic=6)
                self.assertEqual(result.returncode, native, result.stdout)
                self.assertTrue(any(c["command"] == "getent" for c in calls))
                self.assertTrue(any(c["command"] == "curl" for c in calls))
                self.assertEqual(sum(c["command"] == "node" for c in calls), 1)

    def test_cleanup_preserves_original_failure(self):
        result, _, _ = self.run_install(native=23, cleanup=74)
        self.assertEqual(result.returncode, 23, result.stdout)

    def test_diagnostic_cancellation_stops_setup_without_retry(self):
        for status in (130, 143):
            with self.subTest(status=status):
                result, calls, snapshot = self.run_install(diagnostic=status)
                self.assertEqual(result.returncode, status, result.stdout)
                self.assertIsNone(snapshot)
                self.assertEqual(sum(c["command"] == "getent" for c in calls), 1)
                self.assertFalse(any(c["command"] in ("node", "npx") for c in calls))


if __name__ == "__main__":
    unittest.main()
