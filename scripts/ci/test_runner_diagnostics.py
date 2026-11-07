"""Sanitized lifecycle evidence and real workload interruption contracts."""

import contextlib
import io
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest

import runner_diagnostics as diag


SECRET = "synthetic-secret-never-export"
SCRIPT = Path(__file__).with_name("performance.py").resolve()


class EvidenceTests(unittest.TestCase):
    def test_journal_preserves_transition_and_drops_messages_and_unapproved_fields(self):
        row = {"UNIT": "clc-runner.service", "MESSAGE": "Stopping " + SECRET,
               "__REALTIME_TIMESTAMP": "1794012280000000", "_PID": "1",
               "MESSAGE_ID": "a" * 32, "JOB_TYPE": "stop", "JOB_RESULT": "done",
               "_CMDLINE": SECRET, "ENVIRONMENT": SECRET, "RESULT": SECRET}
        event = diag.journal_event(row)
        self.assertEqual(event["transition"], "stopping")
        self.assertEqual(event["journal_us"], row["__REALTIME_TIMESTAMP"])
        self.assertEqual(event["job_type"], "stop")
        self.assertNotIn(SECRET, json.dumps(event))
        self.assertIsNone(diag.journal_event({"_SYSTEMD_UNIT": "arbitrary.service", "MESSAGE": SECRET}))
        self.assertIsNone(diag.journal_event([]))
        self.assertEqual(diag.journal_event({"_TRANSPORT": "kernel", "MESSAGE": "Out of memory: " + SECRET,
                                           "__REALTIME_TIMESTAMP": SECRET}), {"event": "kernel_oom", "journal_us": None})

    def test_package_records_keep_changes_and_known_restart_units_only(self):
        event = diag.package_event("2026-11-07 00:44:39 upgrade libc6:amd64 2.39-0ubuntu8.6 2.39-0ubuntu8.7", "dpkg")
        self.assertEqual(event["package"], "libc6:amd64")
        self.assertEqual(event["new_version"], "2.39-0ubuntu8.7")
        event = diag.package_event("2026-11-07 00:44:39,123 INFO Packages that will be upgraded: " + SECRET, "unattended")
        self.assertEqual(event, {"event": "unattended_packages", "package_time": "2026-11-07 00:44:39"})
        self.assertEqual(diag.package_event(" systemctl restart clc-runner.service " + SECRET, "unattended_dpkg"),
                         {"event": "update_log_restart_command", "units": ["clc-runner.service"]})
        self.assertIsNone(diag.package_event(SECRET, "dpkg"))
        self.assertIsNone(diag.package_event("systemctl restart " + SECRET, "unattended_dpkg"))

    def test_probes_emit_safe_ancestry_cursor_state_and_deduplicated_package_evidence(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()) as console:
            root = Path(directory)
            proc = root / "proc"
            for pid, name, parent in (("10", "needrestart", "20"), ("20", "unattended-upgr", "1"), ("1", SECRET, "0")):
                path = proc / pid
                path.mkdir(parents=True)
                (path / "comm").write_text(name)
                (path / "status").write_text("PPid:\t" + parent)
                (path / "cmdline").write_text(SECRET)
                (path / "environ").write_text(SECRET)
            (proc / "meminfo").write_text("MemTotal: 100 kB\nMemAvailable: 50 kB\n")
            (proc / "vmstat").write_text("oom_kill 0\n")
            calls = []

            def probe(arguments, stop, timeout):
                calls.append(arguments)
                self.assertEqual(timeout, 1)
                if "journalctl" in arguments:
                    if any(arg.startswith("--after-cursor=") for arg in arguments):
                        return ""
                    return json.dumps({"UNIT": "apt-daily-upgrade.service", "__CURSOR": "s=abc;i=123",
                                       "MESSAGE": SECRET}) + "\nmalformed\n[]"
                if arguments[0] == "systemctl":
                    return "Id=clc-runner.service\nMainPID=44\nActiveState=active\nResult=" + SECRET + "\nEnvironment=" + SECRET
                return "2026-11-07 00:44:39 upgrade libc6:amd64 2.39-0ubuntu8.6 2.39-0ubuntu8.7"

            collector = diag.Collector(root / "events.jsonl", probe, proc)
            try:
                collector.sample()
                collector.sample()
            finally:
                collector.stream.close()
            rows = [json.loads(line) for line in (root / "events.jsonl").read_text().splitlines()]
            self.assertEqual(sum(row["event"] == "package_change" for row in rows), 1)
            processes = next(row["processes"] for row in rows if row["event"] == "update_processes")
            self.assertEqual(processes[0]["parent_name"], "unattended-upgr")
            self.assertTrue(any("--after-cursor=s=abc;i=123" in call for call in calls))
            self.assertNotIn(SECRET, console.getvalue())
            self.assertNotIn(SECRET, (root / "events.jsonl").read_text())
            self.assertTrue(all(row["observed_utc"].endswith("+00:00") for row in rows))

    def test_missing_tools_are_visible_and_finish_stops_collection(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            output = Path(directory) / "events.jsonl"
            collector = diag.Collector(output, lambda *args, **kwargs: None, Path(directory) / "absent").start()
            deadline = time.monotonic() + 2
            while "resource_state" not in output.read_text() and time.monotonic() < deadline:
                time.sleep(.01)
            collector.finish()
            self.assertFalse(collector.thread.is_alive())
            rows = [json.loads(line) for line in output.read_text().splitlines()]
            state = next(row for row in rows if row["event"] == "service_state")
            self.assertFalse(state["journal_available"])
            self.assertFalse(state["systemctl_available"])
            self.assertEqual(rows[-1]["event"], "collector_stopped")


class WorkloadTests(unittest.TestCase):
    def test_opt_in_collection_preserves_failure_and_does_not_export_environment(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "evidence"
            env = dict(os.environ, PATH="", ASAP_CI_RUNNER_DIAGNOSTICS="true", GH_TOKEN=SECRET)
            result = subprocess.run([sys.executable, "-B", str(SCRIPT), "--output", str(output), "--",
                                     sys.executable, "-c", "raise SystemExit(23)"], env=env,
                                    capture_output=True, text=True, timeout=12)
            self.assertEqual(result.returncode, 23, result.stderr)
            self.assertIn("[runner-lifecycle]", result.stdout)
            self.assertNotIn(SECRET, result.stdout + result.stderr)
            self.assertIn("collector_stopped", (output / "runner-lifecycle.jsonl").read_text())

    @unittest.skipUnless(sys.platform == "linux", "POSIX signal semantics")
    def test_opt_in_collection_preserves_sigterm_and_final_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ready = root / "ready"
            output = root / "evidence"
            source = f"from pathlib import Path; import time; Path({str(ready)!r}).touch(); time.sleep(60)"
            env = dict(os.environ, PATH="", ASAP_CI_RUNNER_DIAGNOSTICS="true")
            process = subprocess.Popen([sys.executable, "-B", str(SCRIPT), "--output", str(output), "--",
                                        sys.executable, "-c", source], env=env, stdout=subprocess.PIPE,
                                       stderr=subprocess.PIPE, text=True)
            try:
                deadline = time.monotonic() + 5
                while not ready.exists() and time.monotonic() < deadline:
                    time.sleep(.02)
                self.assertTrue(ready.exists())
                process.send_signal(signal.SIGTERM)
                stdout, stderr = process.communicate(timeout=8)
                self.assertEqual(process.returncode, 143, stdout + stderr)
                self.assertIn("collector_stopped", (output / "runner-lifecycle.jsonl").read_text())
                self.assertIn("Interrupted by signal: 15", (output / "summary.md").read_text())
            finally:
                if process.poll() is None:
                    process.kill()
                    process.communicate()


if __name__ == "__main__":
    unittest.main()
