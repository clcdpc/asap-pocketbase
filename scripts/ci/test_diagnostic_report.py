"""Observable report calculations, incomplete evidence and UTC correlation."""

import json
from pathlib import Path
import tempfile
import unittest

import diagnostic_report as report


def stamp(seconds):
    return report.utc(report.instant("2026-10-29T12:00:00Z") + seconds)


def trx_text():
    intervals = [(0, 10), (0, 20), (30, 35), (35, 100)]
    results = []
    definitions = []
    for index, (start, end) in enumerate(intervals):
        results.append(f'<UnitTestResult testId="{index}" executionId="{index}" testName="Test{index}" duration="00:00:{end-start:02d}" startTime="{stamp(start)}" endTime="{stamp(end)}" outcome="Passed" />')
        definitions.append(f'<UnitTest id="{index}"><TestMethod className="Group{index // 2}" /></UnitTest>')
    # The last duration needs proper minutes rather than 65 seconds.
    results[-1] = results[-1].replace("00:00:65", "00:01:05")
    return '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">' + f'<Times start="{stamp(-2)}" finish="{stamp(103)}" />' + '<Results>' + ''.join(results) + '</Results><TestDefinitions>' + ''.join(definitions) + '</TestDefinitions></TestRun>'


def write_jsonl(path, rows):
    path.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")


def write_evidence(output):
    output.mkdir(parents=True, exist_ok=True)
    (output / "test-results.trx").write_text(trx_text(), encoding="utf-8")
    write_jsonl(output / "samples.jsonl", [
        {"kind": "host", "timestamp_utc": stamp(15), "interval_seconds": 15,
         "cpu": {"busy_percent": 20, "iowait_percent": 2}, "memory": {"available_bytes": 2048 * 1024**2},
         "pressure": {"cpu": {"some": {"stall_percent": 1}}, "memory": {"some": {"stall_percent": 0}}, "io": {"some": {"stall_percent": 2}}},
         "disks": {"sda": {"read_await_ms": 3, "write_await_ms": 5}}, "processes": {"sqlservr": {"cpu_percent": 15}}},
        {"kind": "host", "timestamp_utc": stamp(30), "interval_seconds": 15,
         "cpu": {"busy_percent": 80, "iowait_percent": 8}, "memory": {"available_bytes": 1024 * 1024**2},
         "pressure": {"cpu": {"some": {"stall_percent": 6}}, "memory": {"some": {"stall_percent": 1}}, "io": {"some": {"stall_percent": 10}}},
         "disks": {"sda": {"read_await_ms": 15, "write_await_ms": 30}}, "processes": {"sqlservr": {"cpu_percent": 75}}}])
    write_jsonl(output / "sql-waits.jsonl", [{"timestamp_utc": stamp(60), "interval_start_utc": stamp(0), "interval_seconds": 60, "available": True, "rows": [
        {"wait_type": "WRITELOG", "category": "log_io", "idle": False, "delta": {"wait_time_ms": 500, "waiting_tasks_count": 20, "signal_wait_time_ms": 5}},
        {"wait_type": "PAGEIOLATCH_SH", "category": "page_read_io", "idle": False, "delta": {"wait_time_ms": 100, "waiting_tasks_count": 100, "signal_wait_time_ms": 2}},
        {"wait_type": "LAZYWRITER_SLEEP", "idle": True, "delta": {"wait_time_ms": 99999, "waiting_tasks_count": 1000, "signal_wait_time_ms": 0}}]}])
    files = []
    for reads, stall in ((1, 100), (9, 18)):
        files.append({"database_kind": "synthetic_test", "file_kind": "log", "database_id": 5, "file_id": len(files)+1,
            "read_latency_ms": stall / reads, "write_latency_ms": 4,
            "delta": {"num_of_reads": reads, "num_of_bytes_read": reads*8192, "io_stall_read_ms": stall,
                      "num_of_writes": 2, "num_of_bytes_written": 8192, "io_stall_write_ms": 8, "io_stall": stall+8}})
    write_jsonl(output / "sql-io.jsonl", [{"timestamp_utc": stamp(60), "interval_start_utc": stamp(0), "interval_seconds": 60, "rows": files}])
    write_jsonl(output / "sql-activity.jsonl", [{"timestamp_utc": stamp(60), "missing_sections": ["memory"],
        "requests": {"active_requests": 3, "waiting_requests": 2, "blocked_sessions": 1, "max_blocked_wait_ms": 300},
        "schedulers": {"runnable_tasks": 4, "max_scheduler_runnable_tasks": 2, "queued_work_items": 0},
        "request_waits": [{"wait_type": "LCK_M_X", "request_count": 1}], "memory": None}])
    write_jsonl(output / "fixture-timings.jsonl", [{"group": "MigrationCliTests", "operation": "dacpac_deploy_including_database_creation",
        "duration_seconds": 2, "succeeded": True}, {"group": "MigrationCliTests", "operation": "dacpac_deploy_including_database_creation",
        "duration_seconds": 3, "succeeded": False}])
    report.write_reports(output)


class TrxTests(unittest.TestCase):
    def test_real_reporter_shape_quantiles_class_mapping_concurrency_and_gaps(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "test-results.trx"
            path.write_text(trx_text())
            data = report.read_trx(path)
        self.assertEqual([test["class"] for test in data["tests"]], ["Group0", "Group0", "Group1", "Group1"])
        text = report.timing_report(data)
        self.assertIn("15.000 / 51.500 / 58.250", text)
        self.assertIn("durations: 100.000 s", text)
        self.assertIn("intervals: 90.000 s", text)
        self.assertIn("overlapping interval time: 10.000 s", text)
        self.assertIn("concurrency: 2", text)
        self.assertIn("| Group1 | 2 | 70.000", text)
        self.assertIn("first test: 2.000 s", text)
        self.assertIn("reporter finish: 3.000 s", text)
        time = report.timeline(data["tests"])
        self.assertEqual(time["gaps"], [(report.instant(stamp(20)), report.instant(stamp(30)))])

    def test_partial_trx_recovers_completed_results_and_missing_definitions(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "partial.trx"
            path.write_text(trx_text().split('</Results>')[0] + '<UnitTestResult testId="partial')
            data = report.read_trx(path)
        self.assertEqual(len(data["tests"]), 4)
        self.assertIn("incomplete", data["issue"])
        self.assertIn("unknown class", data["tests"][0]["class"])
        self.assertIn("only fully parsed results", report.timing_report(data))

    def test_duration_days_and_missing_timezone_are_explicit(self):
        self.assertEqual(report.seconds("1.02:03:04.500"), 93784.5)
        self.assertIsNone(report.instant("2026-10-29T12:00:00"))
        self.assertIsNone(report.seconds("NaN"))
        self.assertEqual(report.percentile([10], 95), 10)

    def test_top_25_limit_and_no_raw_output_in_markdown(self):
        tests = [{"name": f"name{i}|<script>", "class": "Class", "seconds": i, "start": None, "end": None, "outcome": "Passed"} for i in range(30)]
        text = report.timing_report({"tests": tests, "times": {}, "issue": None})
        self.assertEqual(text.count("&lt;script&gt;"), 25)
        self.assertNotIn("|<script>", text)
        self.assertIn("name29", text)
        self.assertNotIn("name0\\|", text)


class ConsolidatedTests(unittest.TestCase):
    def test_rankings_weighted_file_latency_activity_and_fixture_counts(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            write_evidence(output)
            text = (output / "diagnostic-summary.md").read_text()
        self.assertIn("| WRITELOG | log_io | 500.000 | 20 | 5.000 |", text)
        self.assertNotIn("LAZYWRITER_SLEEP", text)
        self.assertIn("PAGEIOLATCH_SH=100; WRITELOG=20", text)
        self.assertIn("| synthetic_test / log | 10 | 81920 | 118.000 | 11.800 | 100.000", text)
        self.assertIn("| Blocked user sessions | 1.000 |", text)
        self.assertIn("dacpac_deploy_including_database_creation | 2 | 1 | 1 | 5.000 | 3.000", text)
        self.assertIn("Unavailable SQL sections: memory", text)
        self.assertIn("Hypotheses to investigate", text)

    def test_window_uses_overlap_weights_and_preserves_device_layers(self):
        rows = [{"timestamp_utc": stamp(10), "interval_seconds": 10, "cpu": {"busy_percent": 20}, "disks": {"sda": {"read_await_ms": 5}}},
                {"timestamp_utc": stamp(40), "interval_seconds": 30, "cpu": {"busy_percent": 80}, "disks": {"sdb": {"write_await_ms": 15}}}]
        values = report.resource_window(rows, report.instant(stamp(5)), report.instant(stamp(20)))
        self.assertEqual(values[0], "60.000")  # 5 seconds at 20%, 10 seconds at 80%.
        self.assertIn("sda 5.000/unavailable", values[6])
        self.assertIn("sdb unavailable/15.000", values[6])
        self.assertEqual(values[-1], "15.000")

    def test_missing_and_partial_inputs_still_create_both_reports(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            (output / "sql-waits.jsonl").write_text('{"rows":[]}\n{"rows":')
            path = report.write_reports(output)
            self.assertTrue(path.exists())
            self.assertTrue((output / "test-timings.md").exists())
            self.assertIn("TRX unavailable or incomplete", path.read_text())
            self.assertIn("SQL waits: 1 flushed records", path.read_text())


if __name__ == "__main__":
    unittest.main()
