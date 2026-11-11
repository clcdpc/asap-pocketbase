"""Execute the install helper with fake downloads; no network or apt mutation."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("install-browser-dependencies.sh").resolve()
DNS_ERROR = "E: Failed to fetch https://archive.ubuntu.com/package.deb  Temporary failure resolving 'azure.archive.ubuntu.com'"


@unittest.skipUnless(os.name == "posix" and shutil.which("bash"), "POSIX Bash is required")
class BrowserDependencyTests(unittest.TestCase):
    def run_install(self, attempts, tee_failure=False):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name, body in {
                "npx": """import json, os, pathlib, sys
root = pathlib.Path(os.environ['INSTALL_TEST_ROOT'])
calls = root / 'calls'
prior = calls.read_text().splitlines() if calls.exists() else []
with calls.open('a') as stream:
    stream.write(json.dumps(sys.argv[1:]) + '\\n')
attempts = json.loads(os.environ['INSTALL_TEST_ATTEMPTS'])
status, message = attempts[min(len(prior), len(attempts) - 1)]
print(message, flush=True)
sys.exit(status)
""",
                "sleep": """import os, pathlib, sys
with (pathlib.Path(os.environ['INSTALL_TEST_ROOT']) / 'sleeps').open('a') as stream:
    stream.write(' '.join(sys.argv[1:]) + '\\n')
""",
            }.items():
                path = root / name
                path.write_text("#!/usr/bin/env python3\n" + body)
                path.chmod(0o755)
            if tee_failure:
                tee = root / "tee"
                tee.write_text("#!/usr/bin/env python3\nimport sys\nsys.stdin.read()\nsys.exit(74)\n")
                tee.chmod(0o755)
            env = {**os.environ, "PATH": f"{root}{os.pathsep}{os.environ['PATH']}",
                   "RUNNER_TEMP": str(root), "INSTALL_TEST_ROOT": str(root),
                   "INSTALL_TEST_ATTEMPTS": json.dumps(attempts)}
            result = subprocess.run(["bash", str(SCRIPT)], env=env, text=True,
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=10)
            calls = [json.loads(line) for line in (root / "calls").read_text().splitlines()]
            sleeps = (root / "sleeps").read_text().splitlines() if (root / "sleeps").exists() else []
            self.assertEqual(list(root.glob("asap-browser-install.*")), [], "Temporary logs must be cleaned up")
            for call in calls:
                self.assertEqual(call, ["--no-install", "playwright", "install", "--with-deps", "chromium"])
            return result, calls, sleeps

    def test_success_needs_one_attempt(self):
        result, calls, sleeps = self.run_install([(0, "Installed")])
        self.assertEqual(result.returncode, 0)
        self.assertEqual(len(calls), 1)
        self.assertEqual(sleeps, [])

    def test_transient_dns_failure_recovers(self):
        result, calls, sleeps = self.run_install([(1, DNS_ERROR), (0, "Installed")])
        self.assertEqual(result.returncode, 0)
        self.assertEqual(len(calls), 2)
        self.assertEqual(sleeps, ["30"])
        self.assertIn(DNS_ERROR, result.stdout)
        self.assertIn("Installed", result.stdout)

    def test_persistent_dns_failure_is_bounded_and_preserves_final_status(self):
        result, calls, sleeps = self.run_install([(1, DNS_ERROR), (23, DNS_ERROR)])
        self.assertEqual(result.returncode, 23)
        self.assertEqual(len(calls), 2)
        self.assertEqual(sleeps, ["30"])
        self.assertEqual(result.stdout.count(DNS_ERROR), 2)

    def test_non_dns_errors_fail_without_retry(self):
        for message in ["E: Unable to locate package", "Hash Sum mismatch", "Permission denied"]:
            with self.subTest(message=message):
                result, calls, sleeps = self.run_install([(7, message)])
                self.assertEqual(result.returncode, 7)
                self.assertEqual(len(calls), 1)
                self.assertEqual(sleeps, [])

    def test_second_attempt_error_is_not_hidden_by_first_attempt_dns_log(self):
        result, calls, sleeps = self.run_install([(1, DNS_ERROR), (9, "Permission denied")])
        self.assertEqual(result.returncode, 9)
        self.assertEqual(len(calls), 2)
        self.assertEqual(sleeps, ["30"])

    def test_interruption_does_not_retry_even_after_dns_output(self):
        for status in [130, 143]:
            with self.subTest(status=status):
                result, calls, sleeps = self.run_install([(status, DNS_ERROR)])
                self.assertEqual(result.returncode, status)
                self.assertEqual(len(calls), 1)
                self.assertEqual(sleeps, [])

    def test_logging_failure_cannot_report_success(self):
        result, calls, sleeps = self.run_install([(0, "Installed")], tee_failure=True)
        self.assertEqual(result.returncode, 74)
        self.assertEqual(len(calls), 1)
        self.assertEqual(sleeps, [])


if __name__ == "__main__":
    unittest.main()
