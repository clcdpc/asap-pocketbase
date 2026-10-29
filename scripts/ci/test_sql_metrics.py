"""Synthetic SQL snapshots, privacy, resets, partial evidence and collector lifecycle."""

import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import threading
import time
import textwrap
import unittest
from unittest import mock

import performance as perf
import sql_metrics as sql


EPOCH = "a" * 64
IDENTITY = "b" * 64
SECRET = "must-not-escape-SQL-stdin"
STAMP = "2026-10-29T12:00:00.000Z"


def snapshot(count=100, epoch=EPOCH, identity=IDENTITY):
    return {"meta": {"engine_epoch": epoch, "sql_timestamp_utc": STAMP, "uptime_ms": count},
            "waits": [{"wait_type": "WRITELOG", "waiting_tasks_count": count, "wait_time_ms": count * 10, "signal_wait_time_ms": count}],
            "io": [{"database_id": 5, "file_id": 1, "file_identity": identity, "database_kind": "synthetic_test", "file_kind": "log",
                    "sample_ms": count * 1000, **{key: count * multiplier for key, multiplier in zip(sql.IO_COUNTERS, (1, 8192, 3, 2, 4096, 8, 11))}}],
            "requests": {"active_requests": 2, "waiting_requests": 1, "blocked_sessions": 1, "max_blocked_wait_ms": 24},
            "request_waits": [{"wait_type": "LCK_M_X", "request_count": 1, "max_wait_ms": 24}],
            "schedulers": {"online_schedulers": 4, "runnable_tasks": 3},
            "memory": {"physical_memory_in_use_kb": 4096, "process_physical_memory_low": 0}}


def wire(data):
    return "\n".join(f"CI_{name.upper()}_BEGIN\n{json.dumps(value)}\nCI_{name.upper()}_END" for name, value in data.items())


class SqlCounterTests(unittest.TestCase):
    def setUp(self):
        self.unavailable = set()
        self.metrics = sql.SqlMetrics(mock.Mock(), threading.Event(), self.unavailable)

    def observe(self, count=100, now=1000, **kwargs):
        return self.metrics.observe(snapshot(count, **kwargs), STAMP, now)

    def test_wait_deltas_signal_counts_and_new_wait_from_zero(self):
        first = self.observe()
        self.assertTrue(all(value is None for value in first["waits"]["rows"][0]["delta"].values()))
        data = snapshot(120)
        data["waits"].append({"wait_type": "PAGEIOLATCH_SH", "waiting_tasks_count": 2, "wait_time_ms": 25, "signal_wait_time_ms": 3})
        second = self.metrics.observe(data, STAMP, 1060)
        self.assertEqual(second["waits"]["interval_seconds"], 60)
        self.assertEqual(second["waits"]["rows"][0]["delta"], {"waiting_tasks_count": 20, "wait_time_ms": 200, "signal_wait_time_ms": 20})
        self.assertEqual(second["waits"]["rows"][1]["delta"]["wait_time_ms"], 25)

    def test_wait_reset_invalidates_all_waits_including_ones_that_increased(self):
        before = snapshot()
        before["waits"].append({"wait_type": "LCK_M_X", "waiting_tasks_count": 50, "wait_time_ms": 50, "signal_wait_time_ms": 1})
        self.metrics.observe(before, STAMP, 1000)
        after = snapshot(120)
        after["waits"].append({"wait_type": "LCK_M_X", "waiting_tasks_count": 1, "wait_time_ms": 1, "signal_wait_time_ms": 1})
        reset = self.metrics.observe(after, STAMP, 1060)["waits"]
        self.assertIsNone(reset["interval_seconds"])
        self.assertTrue(all(row["delta_status"] == "wait_counter_reset" and row["delta"]["wait_time_ms"] is None for row in reset["rows"]))
        self.assertEqual(self.observe(130, 1120)["waits"]["rows"][0]["delta_status"], "wait_counter_reset")  # LCK disappeared after reset.

    def test_restart_never_uses_old_baselines_even_if_new_counters_are_larger(self):
        self.observe()
        reset = self.observe(500, 1060, epoch="c" * 64)
        for name, key in (("waits", "wait_time_ms"), ("io", "num_of_reads")):
            self.assertIsNone(reset[name]["rows"][0]["delta"][key])
        self.assertEqual(self.observe(510, 1120, epoch="c" * 64)["io"]["rows"][0]["delta"]["num_of_reads"], 10)

    def test_io_rates_and_latency_are_per_operation_not_time_or_file_average(self):
        self.observe()
        second = self.observe(120, 1060)["io"]["rows"][0]
        self.assertEqual(second["delta"]["num_of_reads"], 20)
        self.assertEqual(second["delta"]["num_of_bytes_read"], 20 * 8192)
        self.assertEqual(second["read_latency_ms"], 3)
        self.assertEqual(second["write_latency_ms"], 4)

    def test_reused_ids_reopened_files_and_missing_identity_establish_baselines(self):
        for identity in ("c" * 64, None):
            self.metrics.previous.clear()
            self.observe()
            row = self.observe(200, 1060, identity=identity)["io"]["rows"][0]
            self.assertEqual(row["delta_status"], "new_or_reopened_file_baseline")
            self.assertIsNone(row["write_latency_ms"])
        self.metrics.previous.clear()
        self.observe()
        absent = snapshot(120)
        absent["io"] = []
        self.metrics.observe(absent, STAMP, 1060)
        self.assertIsNone(self.observe(140, 1120)["io"]["rows"][0]["delta"]["num_of_reads"])

    def test_counter_decrease_zero_operations_and_missing_counter(self):
        self.observe()
        reset = self.observe(1, 1060)["io"]["rows"][0]
        self.assertTrue(all(value is None for value in reset["delta"].values()))
        self.assertIsNone(self.observe(1, 1120)["io"]["rows"][0]["read_latency_ms"])
        partial = snapshot(2)
        del partial["io"][0]["num_of_bytes_read"]
        self.assertIsNone(self.metrics.observe(partial, STAMP, 1180)["io"]["rows"][0]["delta"]["num_of_reads"])

    def test_failed_section_retains_baseline_and_exposes_longer_interval(self):
        self.observe()
        partial = snapshot(120)
        del partial["waits"]
        missing = self.metrics.observe(partial, STAMP, 1060)
        self.assertFalse(missing["waits"]["available"])
        later = self.observe(140, 1120)
        self.assertEqual(later["waits"]["interval_seconds"], 120)
        self.assertEqual(later["waits"]["rows"][0]["delta"]["wait_time_ms"], 400)
        self.assertEqual(later["io"]["interval_seconds"], 60)
        self.metrics.observe({}, STAMP, 1180)
        self.assertIsNone(self.observe(160, 1240)["waits"]["rows"][0]["delta"]["wait_time_ms"])

    def test_allowlist_never_exports_query_parameters_paths_names_or_credentials(self):
        data = snapshot()
        for value in data.values():
            for row in value if isinstance(value, list) else [value]:
                row.update(query_text=SECRET, database_name=SECRET, physical_name=SECRET, server_name=SECRET, password=SECRET)
        exported = json.dumps(self.metrics.observe(data, STAMP, 1000))
        self.assertNotIn(SECRET, exported)
        self.assertIn('"blocked_sessions": 1', exported)
        self.assertIsNone(sql.integer(True))

    def test_categories_keep_diagnostic_waits_and_retain_idle_raw_counters(self):
        for name, expected in (("WRITELOG", "log_io"), ("PAGEIOLATCH_EX", "page_read_io"), ("LCK_M_X", "locks"),
                               ("PAGELATCH_UP", "memory_latches"), ("SOS_SCHEDULER_YIELD", "cpu_scheduling"),
                               ("IO_COMPLETION", "general_io"), ("RESOURCE_SEMAPHORE", "memory_grants"), ("THREADPOOL", "workers")):
            self.assertEqual(sql.wait_category(name), expected)
            self.assertFalse(sql.idle_wait(name))
        data = snapshot()
        data["waits"][0]["wait_type"] = "LAZYWRITER_SLEEP"
        row = self.metrics.observe(data, STAMP, 1000)["waits"]["rows"][0]
        self.assertTrue(row["idle"])
        self.assertEqual(row["wait_time_ms"], 1000)

    def test_sqlcmd_wrapped_json_and_partial_sections(self):
        text = wire(snapshot())
        text = text.replace('"waiting_tasks_count"', '\n"waiting_tasks_count"')
        self.assertEqual(sql.parse_sections(text), snapshot())
        partial = wire({"meta": snapshot()["meta"], "waits": snapshot()["waits"]}) + '\nCI_IO_BEGIN\n[{"database_id":'
        self.assertEqual(set(sql.parse_sections(partial)), {"meta", "waits"})
        self.assertEqual(sql.parse_sections("CI_WAITS_BEGIN\nnot json\nCI_WAITS_END"), {})

    def test_password_only_in_stdin_and_fixed_disposable_target(self):
        self.metrics.command.return_value = wire(snapshot())
        with mock.patch.dict(os.environ, ASAP_TEST_SQL_SA_PASSWORD=SECRET):
            rows = self.metrics.sample()
        arguments, kwargs = self.metrics.command.call_args
        self.assertNotIn(SECRET, " ".join(arguments[0]))
        self.assertNotIn("-P", arguments[0][-1])
        self.assertIn("asap-sql", arguments[0])
        self.assertEqual(kwargs["input_text"].splitlines()[0], SECRET)
        self.assertLessEqual(kwargs["timeout"], 8)
        self.assertTrue(kwargs["preserve_partial"])
        self.assertNotIn(SECRET, json.dumps(rows))

    def test_missing_credentials_do_not_attempt_any_connection(self):
        with mock.patch.dict(os.environ, ASAP_TEST_SQL_SA_PASSWORD=""):
            self.metrics.sample()
        self.metrics.command.assert_not_called()


class SqlLifecycleTests(unittest.TestCase):
    @unittest.skipUnless(sys.platform == "linux", "Linux Bash/Docker bootstrap contract")
    def test_workflow_bootstrap_forwards_password_without_secret_arguments(self):
        workflow = Path(__file__).resolve().parents[2] / ".github/workflows/dotnet.yml"
        text = workflow.read_text()
        def command(name, style):
            match = re.search(r"      - name: " + re.escape(name) + r"\n        shell: bash\n        run: " + re.escape(style) + r"\n(.*?)(?=\n      - name:)", text, re.S)
            body = textwrap.dedent(match[1]).strip()
            return " ".join(body.splitlines()) if style == ">-" else body
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            calls = root / "calls"
            docker = root / "docker"
            docker.write_text(f"#!{sys.executable}\nimport os,sys\nfrom pathlib import Path\n"
                f"assert all({SECRET!r} not in arg for arg in sys.argv)\n"
                f"if sys.argv[1]=='run': assert os.environ['MSSQL_SA_PASSWORD']=={SECRET!r}; assert 'MSSQL_SA_PASSWORD' in sys.argv\n"
                f"elif sys.argv[1]=='exec': assert sys.stdin.read()=={SECRET!r}+'\\n'; assert '-P' not in sys.argv[-1]\n"
                f"with Path({str(calls)!r}).open('a') as stream: stream.write(sys.argv[1]+'\\n')\n", encoding="utf-8")
            docker.chmod(0o755)
            env = dict(os.environ, PATH=str(root), ASAP_TEST_SQL_SA_PASSWORD=SECRET)
            for name, style in (("Start SQL Server 2022", ">-"), ("Wait for SQL Server", "|")):
                result = subprocess.run(["/bin/bash", "-c", command(name, style)], env=env, capture_output=True, text=True, timeout=4)
                self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(calls.read_text(), "run\nexec\n")

    @unittest.skipUnless(sys.platform == "linux", "Linux executable Docker fixture")
    def test_full_wrapper_uses_one_workload_and_flushes_sql_deltas(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            state, workload = root / "counter", root / "workload-count"
            docker = root / "docker"
            script = (f"#!{sys.executable}\nimport json,sys\nfrom pathlib import Path\n"
                      "kind=sys.argv[1]\n"
                      "if kind=='version': print('29.0|29.0')\n"
                      "elif kind=='inspect': print(json.dumps({'state':'running','started':'2026-01-01T00:00:00Z'}))\n"
                      "elif kind=='stats': print(json.dumps({'cpu':'10%','memory':'1MiB / 2GiB','block_io':'1kB / 1kB'}))\n"
                      "else:\n"
                      f" p=Path({str(state)!r}); count=int(p.read_text())+1 if p.exists() else 1; p.write_text(str(count))\n"
                      f" data=json.loads({json.dumps(snapshot())!r})\n"
                      " assert sys.stdin.read().splitlines()[0]==" + repr(SECRET) + "\n"
                      " for row in data['waits']:\n  row['wait_time_ms']+=count*10; row['waiting_tasks_count']+=count\n"
                      " for name,value in data.items(): print('CI_'+name.upper()+'_BEGIN\\n'+json.dumps(value)+'\\nCI_'+name.upper()+'_END',flush=True)\n")
            docker.write_text(script, encoding="utf-8")
            docker.chmod(0o755)
            output = root / "evidence"
            source = f"from pathlib import Path; import time; p=Path({str(workload)!r}); p.write_text(p.read_text()+'x' if p.exists() else 'x'); time.sleep(.5); raise SystemExit(27)"
            args = [sys.executable, "-B", str(Path(perf.__file__).resolve()), "--output", str(output), "--interval", ".04", "--sql-interval", ".04", "--", sys.executable, "-c", source]
            env = dict(os.environ, PATH=str(root), ASAP_TEST_SQL_SA_PASSWORD=SECRET)
            result = subprocess.run(args, env=env, capture_output=True, text=True, timeout=6)
            self.assertEqual(result.returncode, 27, result.stderr)
            self.assertEqual(workload.read_text(), "x")
            rows = perf.read_samples(output / "sql-waits.jsonl")
            self.assertGreaterEqual(len(rows), 2)
            self.assertEqual(rows[1]["rows"][0]["delta"]["wait_time_ms"], 10)
            self.assertTrue((output / "diagnostic-summary.md").exists())
            self.assertNotIn(SECRET, "".join(path.read_text() for path in output.iterdir()))

    @unittest.skipUnless(sys.platform == "linux", "Linux executable Docker fixture")
    def test_stalled_sql_does_not_stall_host_or_leave_client_after_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            pidfile = root / "sql-helper.pid"
            docker = root / "docker"
            docker.write_text(f"#!{sys.executable}\nimport os,sys,time,json\nfrom pathlib import Path\n"
                "if sys.argv[1]=='version': print('29.0|29.0')\n"
                "elif sys.argv[1]=='inspect': print(json.dumps({'state':'running','started':'2026-01-01T00:00:00Z'}))\n"
                "elif sys.argv[1]=='stats': print('{}')\n"
                f"else: Path({str(pidfile)!r}).write_text(str(os.getpid())); time.sleep(60)\n", encoding="utf-8")
            docker.chmod(0o755)
            output = root / "evidence"
            args = [sys.executable, "-B", str(Path(perf.__file__).resolve()), "--output", str(output), "--interval", ".03", "--", sys.executable, "-c", "import time; time.sleep(.4); raise SystemExit(33)"]
            env = dict(os.environ, PATH=str(root), ASAP_TEST_SQL_SA_PASSWORD=SECRET)
            started = time.monotonic()
            result = subprocess.run(args, env=env, capture_output=True, text=True, timeout=5)
            self.assertEqual(result.returncode, 33, result.stderr)
            self.assertLess(time.monotonic() - started, 3)
            host = [row for row in perf.read_samples(output / "samples.jsonl") if row["kind"] == "host"]
            self.assertGreaterEqual(len(host), 4)
            self.assertTrue(pidfile.exists())
            self.assertFalse(Path("/proc").joinpath(pidfile.read_text(), "stat").exists())

    def test_collector_flushes_partial_samples_and_stops_its_thread(self):
        with tempfile.TemporaryDirectory() as directory:
            stop, unavailable = threading.Event(), set()
            command = mock.Mock(side_effect=[wire(snapshot()), wire(snapshot(120))])
            with mock.patch.dict(os.environ, ASAP_TEST_SQL_SA_PASSWORD=SECRET):
                collector = sql.SqlCollector(directory, .02, command, stop, unavailable)
                deadline = time.monotonic() + 2
                while command.call_count < 2 and time.monotonic() < deadline:
                    time.sleep(.01)
                stop.set()
                collector.finish()
            self.assertFalse(collector.thread.is_alive())
            for name in ("waits", "io", "activity"):
                path = Path(directory) / ("sql-" + name + ".jsonl")
                self.assertGreaterEqual(len(perf.read_samples(path)), 2)
                self.assertNotIn(SECRET, path.read_text())

    def test_sql_cli_failure_and_timeout_keep_completed_sections_and_reap_helper(self):
        payload = wire({"meta": snapshot()["meta"], "waits": snapshot()["waits"]})
        for source in (f"import sys; print({payload!r},flush=True); sys.exit(7)",
                       f"import time; print({payload!r},flush=True); time.sleep(10)"):
            output = perf.optional_command([sys.executable, "-u", "-c", source], threading.Event(), timeout=.2, input_text=SECRET, preserve_partial=True)
            self.assertEqual(set(sql.parse_sections(output)), {"meta", "waits"})


if __name__ == "__main__":
    unittest.main()
