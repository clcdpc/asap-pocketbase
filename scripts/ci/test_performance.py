"""Focused synthetic-counter and real subprocess checks. No SQL/network/framework."""

import contextlib
import io
import json
import os
from pathlib import Path
import signal
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock

import performance as perf


SCRIPT = Path(__file__).with_name("performance.py").resolve()
LINUX = sys.platform == "linux"
SECRET = "synthetic-secret-must-not-be-exported"


def process_line(pid=10, name="dotnet", ticks=100, start=40, rss=50):
    fields = ["0"] * 22
    fields[0], fields[11], fields[12], fields[19], fields[21] = "S", str(ticks), "0", str(start), str(rss)
    return f"{pid} ({name}) " + " ".join(fields)


def running(pid):
    try:
        # A transient zombie is no longer executing; the container init reaps it.
        return Path(f"/proc/{pid}/stat").read_text().split(")", 1)[1].split()[0] != "Z"
    except FileNotFoundError:
        return False


class CounterTests(unittest.TestCase):
    def test_cpu_uses_deltas_and_excludes_guest_double_counting(self):
        before = perf.cpu_counters("cpu 100 0 50 500 20 5 5 10 1000 0\n")
        after = perf.cpu_counters("cpu 125 0 65 530 30 10 10 20 2000 1000\n")
        self.assertEqual(perf.cpu_percentages(before, after),
                         {"busy_percent": 50, "idle_percent": 30, "iowait_percent": 10, "steal_percent": 10})

    def test_cpu_reset_missing_and_zero_delta_are_unknown(self):
        full = perf.cpu_counters("cpu 10 0 10 50 5 0 0 5")
        for other in (full, perf.cpu_counters("cpu 0 0 0 0 0 0 0 0"), perf.cpu_counters("cpu 20 0 20 60"), {}):
            self.assertTrue(all(value is None for value in perf.cpu_percentages(full, other).values()))

    def test_disk_units_rates_await_and_queue_depth(self):
        before = perf.disk_counters("8 0 sda 100 0 1000 1000 200 0 2000 600 4 900 1800")["sda"]
        after = perf.disk_counters("8 0 sda 130 0 1512 1060 210 0 2256 640 2 1900 5800")["sda"]
        values = perf.disk_interval(before, after, 2)
        self.assertEqual(values["read_ops_per_second"], 15)
        self.assertEqual(values["read_bytes_per_second"], 128 * 1024)
        self.assertEqual(values["write_bytes_per_second"], 64 * 1024)
        self.assertEqual(values["read_await_ms"], 2)
        self.assertEqual(values["write_await_ms"], 4)
        self.assertEqual(values["busy_percent"], 50)
        self.assertEqual(values["average_queue_depth"], 2)
        self.assertEqual(values["in_flight"], 2)  # A decreasing gauge is not a reset.

    def test_disk_reset_and_missing_device_do_not_invent_rates(self):
        before = dict.fromkeys(perf.DISK_FIELDS, 5)
        after = dict(before, reads=1)
        self.assertEqual(perf.disk_interval(before, after, 15), {"in_flight": 5})
        self.assertEqual(perf.disk_interval({}, before, 15), {"in_flight": 5})
        self.assertEqual(perf.disk_counters("8 1 sda1 1 2 3\n"), {})

    def test_pressure_totals_become_interval_percentages(self):
        before = perf.pressure_counters("some avg10=1.0 avg60=2.0 avg300=3.0 total=1000000")
        after = perf.pressure_counters("some avg10=4.0 avg60=5.0 avg300=6.0 total=1500000")
        self.assertEqual(perf.pressure_interval(before, after, 5)["some"]["stall_percent"], 10)
        self.assertIsNone(perf.pressure_interval(after, before, 5)["some"]["stall_percent"])
        self.assertEqual(perf.pressure_counters(""), {})

    def test_process_cpu_is_per_identity_not_differenced_group_totals(self):
        before = dict([perf.process_stat(process_line(ticks=100)), perf.process_stat(process_line(pid=11, ticks=900))])
        after = dict([perf.process_stat(process_line(ticks=200)), perf.process_stat(process_line(pid=11, ticks=5, start=90))])
        value = perf.process_interval(before, after, 2, 100, 4096)["dotnet"]
        self.assertEqual(value, {"count": 2, "rss_bytes": 100 * 4096, "cpu_percent": 50})
        self.assertIsNone(perf.process_stat(process_line(name="unrelated (name)")))
        self.assertEqual(perf.process_stat(process_line(name="Asap.Tests"))[1]["group"], "testhost")

    def test_cgroup_throttle_units_and_resets(self):
        before = {"cpu": {"usage_usec": 100000, "nr_periods": 10, "nr_throttled": 2, "throttled_usec": 1000}, "events": {"oom": 2}}
        after = {"level": 0, "cpu": {"usage_usec": 1100000, "nr_periods": 20, "nr_throttled": 5, "throttled_usec": 301000}, "events": {"oom": 3}}
        result = perf.cgroup_interval(before, after, 2)
        self.assertEqual(result["cpu_percent"], 50)
        self.assertEqual(result["throttled_periods_percent"], 30)
        self.assertEqual(result["throttled_usec_delta"], 300000)
        self.assertEqual(result["memory_events_delta"], {"oom": 1})
        self.assertIsNone(perf.cgroup_interval(after, before, 2)["throttled_usec_delta"])

    def test_docker_units_and_missing_fields(self):
        result = perf.docker_stats(json.dumps({"cpu": "125.20%", "memory": "2KiB / 3.5GiB", "block_io": "5.5MB / 10kB", "Env": SECRET}))
        self.assertEqual(result["cpu_percent"], 125.2)
        self.assertEqual(result["memory_usage_bytes"], 2048)
        self.assertEqual(result["memory_limit_bytes"], 3.5 * 1024**3)
        self.assertEqual(result["block_read_bytes"], 5500000)
        self.assertEqual(result["block_write_bytes"], 10000)
        self.assertNotIn(SECRET, json.dumps(result))
        self.assertTrue(all(value is None for value in perf.docker_stats('{"memory":null,"cpu":"NaN%"}').values()))
        self.assertIsNone(perf.byte_quantity("not a quantity"))

    def test_sql_delta_baselines_and_restarts(self):
        source = perf.DockerMetrics(threading.Event(), set())
        state = {"state": "running", "started": "2026-01-01T00:00:00Z"}
        values = [json.dumps({"cpu": "10%", "memory": "1MiB / 2GiB", "block_io": f"{read}kB / 2kB"}) for read in (10, 12, 1)]
        with mock.patch.object(source, "inspect", return_value=state), mock.patch.object(perf, "optional_command", side_effect=values), mock.patch.object(perf.time, "monotonic", side_effect=[100, 160, 220]):
            first, second, third = source.sample(), source.sample(), source.sample()
        self.assertIsNone(first["block_read_bytes_delta"])
        self.assertEqual(second["block_read_bytes_delta"], 2000)
        self.assertAlmostEqual(second["block_read_bytes_per_second"], 2000 / 60)
        self.assertIsNone(third["block_read_bytes_delta"])
        source.previous = {"time": 220, "started": "old", "block_read_bytes": 0, "block_write_bytes": 0}
        with mock.patch.object(source, "inspect", return_value=state), mock.patch.object(perf, "optional_command", return_value=values[0]):
            self.assertIsNone(source.sample()["block_read_bytes_delta"])

    def test_inspect_is_allowlisted_and_container_state_is_retained(self):
        source = perf.DockerMetrics(threading.Event(), set())
        data = {"state": "exited", "started": "2026-01-01T00:00:00Z", "memory_bytes": 1048576,
                "cpuset_cpus": "0-2,4", "cpu_quota_usec": 200000, "cpu_period_usec": 100000,
                "Config": {"Env": SECRET}, "cmdline": SECRET}
        with mock.patch.object(perf, "optional_command", return_value=json.dumps(data)) as command:
            limits, sample = source.inspect(), source.sample()
        self.assertEqual(limits["memory_bytes"], 1048576)
        self.assertEqual(limits["cpuset_cpus"], "0-2,4")
        self.assertEqual(limits["cpu_quota_usec"] / limits["cpu_period_usec"], 2)
        self.assertEqual(sample["state"], "exited")
        self.assertNotIn(SECRET, json.dumps(limits))
        self.assertTrue(all("stats" not in call.args[0] for call in command.call_args_list))


class SyntheticProcTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.proc, self.sysfs = self.root / "proc", self.root / "sys"
        self.workspace = self.root / "workspace"
        self.workspace.mkdir()
        self.unavailable = set()

    def write(self, relative, text):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def seed(self):
        self.write("proc/stat", "cpu 100 0 50 500 20 5 5 10 1000 0\n")
        self.write("proc/cpuinfo", "model name : Synthetic CPU\nflags : hypervisor\n")
        self.write("proc/meminfo", "MemTotal: 8192 kB\nMemAvailable: 2048 kB\nSwapTotal: 4096 kB\nSwapFree: 1024 kB\n")
        self.write("proc/vmstat", "pswpin 10\npswpout 20\npgmajfault 30\n")
        self.write("proc/loadavg", "1.00 2.00 3.00 1/10 1")
        self.write("proc/diskstats", "8 0 sda 100 0 1000 1000 200 0 2000 600 4 900 1800")
        cg = (self.root / "cgroup").as_posix()
        work = self.workspace.as_posix()
        self.write("proc/self/mountinfo", f"1 0 8:1 / / rw - ext4 /dev/secret-volume rw\n2 1 0:1 / {cg} rw - cgroup2 cgroup rw\n3 1 8:1 / {work} rw - ext4 /dev/secret-volume rw\n")
        self.write("proc/self/cgroup", "0::/team/job\n")
        for path, quota in (("cgroup/team/job", "200000 100000"), ("cgroup/team", "100000 100000"), ("cgroup", "max 100000")):
            for name, value in (("cpu.max", quota), ("cpu.stat", "usage_usec 10\nnr_periods 5\nnr_throttled 1\nthrottled_usec 1000"),
                                ("cpuset.cpus.effective", "0-2,4"), ("memory.current", "1048576"), ("memory.max", "max"),
                                ("memory.events", "high 5\nmax 1\noom 0\noom_kill 0")):
                self.write(path + "/" + name, value)
        self.write("proc/10/stat", process_line())
        self.write("proc/10/cmdline", SECRET)
        self.write("proc/10/environ", SECRET)
        for name in ("cpu", "memory", "io"):
            self.write("proc/pressure/" + name, "some avg10=1 avg60=2 avg300=3 total=1000000")

    def metrics(self):
        return perf.LinuxMetrics(self.workspace, self.unavailable, self.proc, self.sysfs)

    def test_snapshot_memory_swap_rates_and_sanitized_metadata(self):
        self.seed()
        metrics = self.metrics()
        previous = metrics.snapshot()
        self.write("proc/vmstat", "pswpin 14\npswpout 22\npgmajfault 40\n")
        current = metrics.snapshot()
        current["monotonic"] = previous["monotonic"] + 2
        row = metrics.interval(previous, current)
        self.assertEqual(row["memory"]["used_bytes"], 6144 * 1024)
        self.assertEqual(row["memory"]["swap_used_bytes"], 3072 * 1024)
        self.assertAlmostEqual(row["memory"]["swap_in_bytes_per_second"], 2 * metrics.page_size)
        self.assertAlmostEqual(row["memory"]["major_faults_per_second"], 5)
        metadata = metrics.environment()
        exported = json.dumps({"row": row, "environment": metadata})
        for secret in (SECRET, self.root.as_posix(), str(self.root), "secret-volume"):
            self.assertNotIn(secret, exported)
        self.assertEqual(metadata["filesystems"]["workspace"]["type"], "ext4")
        self.assertTrue(metadata["virtualization"]["hypervisor_cpu_flag"])

    def test_cgroup_ancestors_and_quota_scope(self):
        self.seed()
        groups = self.metrics().read_cgroups()
        self.assertEqual([group["level"] for group in groups], [0, 1, 2])
        self.assertEqual([group["cpu_limit_cores"] for group in groups], [2, 1, None])
        self.assertEqual(groups[2]["cpu_limit_status"], "unlimited")
        self.assertEqual(groups[0]["cpuset_cpu_count"], 4)
        self.assertEqual(groups[0]["memory_limit_status"], "unlimited")

    def test_missing_interfaces_and_vm_counter_reset(self):
        self.proc.mkdir()
        metrics = self.metrics()
        before = metrics.snapshot()
        after = metrics.snapshot()
        row = metrics.interval(before, after)
        self.assertIsNone(row["cpu"]["busy_percent"])
        self.assertEqual(row["pressure"]["cpu"], {})
        self.assertTrue(any("pressure unavailable" in message for message in self.unavailable))
        before["vm"]["pswpin"], after["vm"]["pswpin"] = 100, 1
        self.assertIsNone(metrics.interval(before, after)["memory"]["swap_in_bytes"])

    @unittest.skipUnless(LINUX, "Linux block-device symlinks")
    def test_partition_mapping_selects_whole_device_only(self):
        self.seed()
        self.write("sys/devices/block/sda/sda1/partition", "1")
        (self.sysfs / "dev/block").mkdir(parents=True)
        (self.sysfs / "dev/block/8:1").symlink_to(self.sysfs / "devices/block/sda/sda1")
        self.assertEqual(self.metrics().devices["names"], ["sda"])

    @unittest.skipUnless(LINUX, "Linux block-device symlinks")
    def test_unmapped_overlay_excludes_stacked_disk_and_partition_duplicates(self):
        self.seed()
        self.write("proc/diskstats", "8 0 sda 1 0 1 1 1 0 1 1 0 1 1\n8 1 sda1 1 0 1 1 1 0 1 1 0 1 1\n253 0 dm-0 1 0 1 1 1 0 1 1 0 1 1")
        self.write("sys/devices/block/sda/sda1/partition", "1")
        blocks = self.sysfs / "class/block"
        blocks.mkdir(parents=True)
        (blocks / "sda").symlink_to(self.sysfs / "devices/block/sda")
        (blocks / "sda1").symlink_to(self.sysfs / "devices/block/sda/sda1")
        (blocks / "dm-0/slaves").mkdir(parents=True)
        (blocks / "dm-0/slaves/sda1").symlink_to(self.sysfs / "devices/block/sda/sda1")
        self.assertEqual(self.metrics().devices["names"], ["dm-0"])


class SummaryTests(unittest.TestCase):
    def test_incomplete_lines_and_missing_metrics(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "samples.jsonl"
            path.write_text('{"kind":"host","interval_seconds":15}\n{"kind":"host","interval_seconds":null}\n{"kind":', encoding="utf-8")
            rows = perf.read_samples(path)
            text = perf.summary(rows, 20, 23, {"Docker unavailable"})
            self.assertEqual(len(rows), 2)
            self.assertIn("command exit: 23", text)
            self.assertIn("unavailable", text)
            self.assertIn("Docker unavailable", text)

    def test_averages_are_time_weighted_and_await_is_operation_weighted(self):
        rows = [{"kind": "host", "interval_seconds": 10, "cpu": {"busy_percent": 20},
                 "disks": {"sda": {"read_operations": 10, "read_time_ms": 100, "read_await_ms": 10}}},
                {"kind": "host", "interval_seconds": 30, "cpu": {"busy_percent": 80},
                 "disks": {"sda": {"read_operations": 90, "read_time_ms": 180, "read_await_ms": 2}}}]
        text = perf.summary(rows, 40, 0, set())
        self.assertIn("65.00 / 80.00", text)
        self.assertIn("await 2.80 ms (operation-weighted), 10.00 ms peak interval", text)


class LifecycleTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.output = self.root / "artifacts"
        self.env = dict(os.environ, PATH="", PYTHONDONTWRITEBYTECODE="1", ASAP_TEST_SQL_SA_PASSWORD=SECRET,
                        ASAP_TEST_SQL_CONNECTION_STRING=SECRET, GITHUB_RUN_ID=SECRET,
                        GITHUB_STEP_SUMMARY=str(self.root / "step-summary.md"))

    def arguments(self, source, *extra):
        return [sys.executable, "-B", str(SCRIPT), "--output", str(self.output), "--interval", "0.05", "--", sys.executable, "-c", source, *extra]

    def invoke(self, source, *extra):
        return subprocess.run(self.arguments(source, *extra), cwd=self.root, env=self.env,
                              capture_output=True, text=True, timeout=12)

    def test_success_failure_streams_samples_and_no_secret_export(self):
        for code in (0, 23):
            with self.subTest(code=code):
                result = self.invoke(f"import sys,time; print('original stdout',flush=True); print('original stderr',file=sys.stderr,flush=True); time.sleep(.2); sys.exit({code})", SECRET)
                self.assertEqual(result.returncode, code, result.stderr)
                self.assertIn("original stdout", result.stdout)
                self.assertIn("original stderr", result.stderr)
                self.assertGreaterEqual(len(perf.read_samples(self.output / "samples.jsonl")), 2)
                files = list(self.output.iterdir())
                self.assertEqual({path.name for path in files}, {"environment.json", "samples.jsonl", "summary.md",
                    "sql-waits.jsonl", "sql-io.jsonl", "sql-activity.jsonl", "test-timings.md", "diagnostic-summary.md"})
                for path in files:
                    text = path.read_text(encoding="utf-8")
                    self.assertNotIn(SECRET, text)
                    self.assertNotIn(str(self.root), text)
                    self.assertNotIn("original stdout", text)
                summary = (self.output / "summary.md").read_text(encoding="utf-8")
                self.assertIn(f"command exit: {code}", summary)
                self.assertIn(summary, (self.root / "step-summary.md").read_text(encoding="utf-8"))
                size = (self.output / "samples.jsonl").stat().st_size
                time.sleep(.1)
                self.assertEqual((self.output / "samples.jsonl").stat().st_size, size)

    def test_unwritable_output_does_not_prevent_command_or_mask_failure(self):
        self.output.write_text("cannot be a directory", encoding="utf-8")
        result = self.invoke("import sys; print('still executed'); sys.exit(79)")
        self.assertEqual(result.returncode, 79)
        self.assertIn("still executed", result.stdout)
        self.assertIn("::warning::", result.stderr)

    def test_monitor_failure_does_not_change_result(self):
        with mock.patch.object(perf, "Monitor", side_effect=RuntimeError(SECRET)), contextlib.redirect_stderr(io.StringIO()) as errors:
            self.assertEqual(perf.run([sys.executable, "-c", "raise SystemExit(31)"], self.output), 31)
        self.assertNotIn(SECRET, errors.getvalue())

    def test_summary_failure_does_not_mask_command_failure(self):
        class BrokenFinalization:
            environment = {}
            def finish(self, *_):
                raise OSError(SECRET)
        with mock.patch.object(perf, "Monitor", return_value=BrokenFinalization()), contextlib.redirect_stderr(io.StringIO()) as errors:
            self.assertEqual(perf.run([sys.executable, "-c", "raise SystemExit(29)"], self.output), 29)
        self.assertNotIn(SECRET, errors.getvalue())

    @unittest.skipUnless(LINUX, "Bash fallback contract")
    def test_bash_fallback_without_python_or_with_broken_collector(self):
        wrapper = SCRIPT.with_name("run-with-performance.sh")
        command = [sys.executable, "-c", "import sys; print(sys.argv[1]); raise SystemExit(43)", "argument with spaces & symbols"]
        result = subprocess.run(["/bin/bash", str(wrapper), *command], env=self.env, capture_output=True, text=True, timeout=5)
        self.assertEqual(result.returncode, 43)
        self.assertIn("argument with spaces & symbols", result.stdout)
        broken = self.root / "run-with-performance.sh"
        shutil.copyfile(wrapper, broken)
        broken.with_name("performance.py").write_text("not valid python at all", encoding="utf-8")
        self.env["PATH"] = os.environ["PATH"]
        result = subprocess.run(["/bin/bash", str(broken), *command], env=self.env, capture_output=True, text=True, timeout=5)
        self.assertEqual(result.returncode, 43)
        self.assertIn("::warning::", result.stderr)

    def test_monitor_thread_is_joined_on_command_failure(self):
        with mock.patch.dict(os.environ, self.env, clear=True), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(perf.run([sys.executable, "-c", "raise SystemExit(19)"], self.output, .05), 19)
        self.assertFalse(any(thread.name in ("ci-performance", "ci-sql-diagnostics") for thread in threading.enumerate()))

    @unittest.skipUnless(LINUX, "POSIX signal and process-group semantics")
    def test_sigint_sigterm_forwarded_cleanup_and_partial_artifacts(self):
        for signum in (signal.SIGINT, signal.SIGTERM):
            ready = self.root / ("ready-" + str(signum))
            child = subprocess.Popen(self.arguments("import os,time; from pathlib import Path; Path(" + repr(str(ready)) + ").write_text(str(os.getpid())); time.sleep(60)"), cwd=self.root, env=self.env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            try:
                deadline = time.monotonic() + 5
                while not ready.exists() and time.monotonic() < deadline:
                    time.sleep(.02)
                self.assertTrue(ready.exists())
                pid = int(ready.read_text())
                child.send_signal(signum)
                stdout, stderr = child.communicate(timeout=8)
                self.assertEqual(child.returncode, 128 + signum, stdout + stderr)
                self.assertFalse(running(pid))
                text = (self.output / "summary.md").read_text()
                self.assertIn("Interrupted by signal: " + str(signum), text)
                self.assertTrue(perf.read_samples(self.output / "samples.jsonl"))
            finally:
                if child.poll() is None:
                    child.kill()
                    child.communicate()

    @unittest.skipUnless(LINUX, "POSIX optional command cleanup")
    def test_optional_command_timeout_reaps_client(self):
        pidfile = self.root / "optional.pid"
        source = f"import os,time; from pathlib import Path; Path({str(pidfile)!r}).write_text(str(os.getpid())); time.sleep(60)"
        self.assertIsNone(perf.optional_command([sys.executable, "-c", source], threading.Event(), timeout=.3))
        self.assertFalse(running(int(pidfile.read_text())))

    @unittest.skipUnless(LINUX, "POSIX process-group cleanup")
    def test_optional_command_cleans_descendant_after_client_exits(self):
        pidfile = self.root / "descendant.pid"
        descendant = f"import os,time; from pathlib import Path; Path({str(pidfile)!r}).write_text(str(os.getpid())); time.sleep(60)"
        client = f"import subprocess,sys,time; from pathlib import Path; subprocess.Popen([sys.executable,'-c',{descendant!r}]); p=Path({str(pidfile)!r}); end=time.monotonic()+2\nwhile not p.exists() and time.monotonic()<end: time.sleep(.01)"
        self.assertIsNone(perf.optional_command([sys.executable, "-c", client], threading.Event(), timeout=.4))
        self.assertFalse(running(int(pidfile.read_text())))

    @unittest.skipUnless(LINUX, "POSIX forced termination")
    def test_cancellation_escalates_when_command_ignores_signal(self):
        ready = self.root / "ignoring.pid"
        source = f"import os,signal,time; from pathlib import Path; signal.signal(signal.SIGTERM,signal.SIG_IGN); Path({str(ready)!r}).write_text(str(os.getpid())); time.sleep(60)"
        child = subprocess.Popen(self.arguments(source), cwd=self.root, env=self.env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            deadline = time.monotonic() + 5
            while not ready.exists() and time.monotonic() < deadline:
                time.sleep(.02)
            self.assertTrue(ready.exists())
            started = time.monotonic()
            child.terminate()
            stdout, stderr = child.communicate(timeout=8)
            self.assertEqual(child.returncode, 137, stdout + stderr)
            self.assertLess(time.monotonic() - started, 7)
            self.assertFalse(running(int(ready.read_text())))
        finally:
            if child.poll() is None:
                child.kill()
                child.communicate()

    @unittest.skipUnless(LINUX, "POSIX executable Docker fixture")
    def test_stalled_docker_does_not_delay_command_exit_or_leave_process(self):
        pidfile = self.root / "docker.pid"
        docker = self.root / "docker"
        docker.write_text(f"#!{sys.executable}\nimport os,sys,time,json\nfrom pathlib import Path\n"
                          "if sys.argv[1]=='version': print('29.0|29.0')\n"
                          "elif sys.argv[1]=='inspect': print(json.dumps({'state':'running','started':'2026-01-01T00:00:00Z'}))\n"
                          f"else: Path({str(pidfile)!r}).write_text(str(os.getpid())); time.sleep(60)\n")
        docker.chmod(0o755)
        self.env["PATH"] = str(self.root)
        source = f"import time; from pathlib import Path; p=Path({str(pidfile)!r}); end=time.monotonic()+3\nwhile not p.exists() and time.monotonic()<end: time.sleep(.02)\nraise SystemExit(37)"
        started = time.monotonic()
        result = self.invoke(source)
        self.assertEqual(result.returncode, 37, result.stderr)
        self.assertLess(time.monotonic() - started, 4)
        self.assertTrue(pidfile.exists())
        self.assertFalse(running(int(pidfile.read_text())))


if __name__ == "__main__":
    unittest.main()
