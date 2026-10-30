#!/usr/bin/env python3
"""Rebuild diagnostic Markdown from flushed evidence, including partial TRX/JSONL."""

import argparse
from collections import defaultdict
import datetime as dt
import json
import math
from pathlib import Path
import re
import xml.etree.ElementTree as ET


def jsonl(path):
    rows = []
    try:
        for line in path.read_text(encoding="utf-8").splitlines():
            try:
                row = json.loads(line)
                if isinstance(row, dict):
                    rows.append(row)
            except ValueError:
                pass
    except OSError:
        pass
    return rows


def instant(value):
    try:
        result = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
        return result.astimezone(dt.timezone.utc).timestamp() if result.tzinfo else None
    except (ValueError, TypeError, AttributeError, OverflowError):
        return None


def utc(value):
    return dt.datetime.fromtimestamp(value, dt.timezone.utc).isoformat(timespec="milliseconds") if value is not None else "unavailable"


def number(value):
    return isinstance(value, (float, int)) and not isinstance(value, bool) and math.isfinite(value)


def fmt(value):
    return f"{value:.3f}" if number(value) else "unavailable"


def cell(value):
    return str(value).replace("|", "\\|").replace("\r", " ").replace("\n", " ").replace("`", "'").replace("<", "&lt;").replace(">", "&gt;")


def percentile(values, percent):
    if not values:
        return None
    ordered = sorted(values)
    index = (len(ordered) - 1) * percent / 100
    lower, upper = math.floor(index), math.ceil(index)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (index - lower)


def seconds(value):
    match = re.fullmatch(r"(?:(\d+)\.)?(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", value or "")
    if match:
        return int(match[1] or 0) * 86400 + int(match[2]) * 3600 + int(match[3]) * 60 + float(match[4])
    return None


def read_trx(path):
    tests, definitions, seen, times = [], {}, set(), {}
    issue = None
    try:
        for _, element in ET.iterparse(path, events=("end",)):
            tag = element.tag.rsplit("}", 1)[-1]
            if tag == "UnitTest":
                method = next((child for child in element.iter() if child.tag.rsplit("}", 1)[-1] == "TestMethod"), None)
                if method is not None:
                    definitions[element.get("id")] = method.get("className", "unknown class")
                element.clear()
            elif tag == "UnitTestResult":
                data = element.attrib
                identity = data.get("executionId"), data.get("testId")
                start, end = instant(data.get("startTime")), instant(data.get("endTime"))
                duration = seconds(data.get("duration"))
                if duration is None and start is not None and end is not None and end >= start:
                    duration = end - start
                if identity not in seen and duration is not None:
                    tests.append({"name": data.get("testName", "unnamed"), "id": data.get("testId"),
                                  "seconds": duration, "start": start, "end": end, "outcome": data.get("outcome", "unknown")})
                    seen.add(identity)
                element.clear()  # Discard captured output/errors; never copy them into summaries.
            elif tag == "Times":
                times = dict(element.attrib)
    except (ET.ParseError, OSError) as error:
        issue = "TRX unavailable or incomplete (" + type(error).__name__ + "); only fully parsed results are included."
    for test in tests:
        test["class"] = definitions.get(test["id"], "unknown class (definition unavailable)")
    return {"tests": tests, "times": times, "issue": issue}


def timeline(tests):
    intervals = sorted((row["start"], row["end"]) for row in tests
                       if row["start"] is not None and row["end"] is not None and row["end"] >= row["start"])
    merged = []
    for start, end in intervals:
        if not merged or start > merged[-1][1]:
            merged.append([start, end])
        else:
            merged[-1][1] = max(merged[-1][1], end)
    events = sorted([(start, 1) for start, end in intervals if end > start] +
                    [(end, -1) for start, end in intervals if end > start])
    active, peak = 0, 0
    for _, change in events:
        active += change
        peak = max(peak, active)
    return {"covered": sum(end - start for start, end in merged),
            "span": merged[-1][1] - merged[0][0] if merged else None,
            "interval_sum": sum(end - start for start, end in intervals), "peak": peak, "valid": len(intervals),
            "gaps": [(before[1], after[0]) for before, after in zip(merged, merged[1:]) if after[0] - before[1] >= 5]}


def timing_report(trx):
    tests = trx["tests"]
    durations = [test["seconds"] for test in tests]
    time = timeline(tests)
    groups = defaultdict(list)
    for test in tests:
        groups[test["class"]].append(test["seconds"])
    lines = ["# Individual test timings", "", f"Completed results with usable durations: {len(tests)}.",
             f"Median / p90 / p95: {fmt(percentile(durations, 50))} / {fmt(percentile(durations, 90))} / {fmt(percentile(durations, 95))} s (linear interpolation).",
             f"Sum of observed test durations: {fmt(sum(durations) if tests else None)} s; first-start to last-end span: {fmt(time['span'])} s.",
             f"Union of recorded test intervals: {fmt(time['covered'] if time['valid'] else None)} s; overlapping interval time: {fmt(time['interval_sum'] - time['covered'] if time['valid'] else None)} s; peak recorded concurrency: {time['peak']}.",
             f"UTC start/end coverage: {time['valid']} / {len(tests)} results. Summed durations are execution time across tests, not elapsed suite time.", ""]
    if trx["issue"]:
        lines += [trx["issue"], ""]
    lines += ["## 25 slowest tests", "", "| Class | Test | Duration (s) | Start UTC | End UTC | Outcome |", "| --- | --- | ---: | --- | --- | --- |"]
    for test in sorted(tests, key=lambda row: row["seconds"], reverse=True)[:25]:
        lines.append(f"| {cell(test['class'])} | {cell(test['name'])} | {fmt(test['seconds'])} | {utc(test['start'])} | {utc(test['end'])} | {cell(test['outcome'])} |")
    lines += ["", "## Test classes ranked by summed execution time", "", "| Class | Count | Sum (s) | Median (s) | p90 (s) | p95 (s) |", "| --- | ---: | ---: | ---: | ---: | ---: |"]
    for name, values in sorted(groups.items(), key=lambda item: sum(item[1]), reverse=True):
        lines.append(f"| {cell(name)} | {len(values)} | {fmt(sum(values))} | {fmt(percentile(values, 50))} | {fmt(percentile(values, 90))} | {fmt(percentile(values, 95))} |")
    lines += ["", "## Gaps with no recorded test interval (at least 5 s)", "", "| Start UTC | End UTC | Gap (s) |", "| --- | --- | ---: |"]
    for start, end in sorted(time["gaps"], key=lambda pair: pair[1] - pair[0], reverse=True)[:25]:
        lines.append(f"| {utc(start)} | {utc(end)} | {fmt(end - start)} |")
    if not time["gaps"]:
        lines += ["", "No qualifying internal gap observed; missing timestamps do not prove continuous execution."]
    completions = sorted({test["end"] for test in tests if test["end"] is not None})
    lines += ["", "## Longest gaps between recorded completions (at least 5 s)", "", "| Previous completion UTC | Next completion UTC | Gap (s) | Recorded active test coverage (s) |", "| --- | --- | ---: | ---: |"]
    for start, end in sorted([(a, b) for a, b in zip(completions, completions[1:]) if b - a >= 5], key=lambda pair: pair[1] - pair[0], reverse=True)[:10]:
        clipped = [{**test, "start": max(start, test["start"]), "end": min(end, test["end"])} for test in tests
                   if test["start"] is not None and test["end"] is not None and test["end"] > start and test["start"] < end]
        lines.append(f"| {utc(start)} | {utc(end)} | {fmt(end - start)} | {fmt(timeline(clipped)['covered'])} |")
    execution, finish = instant(trx["times"].get("execution") or trx["times"].get("start")), instant(trx["times"].get("finish"))
    starts = [test["start"] for test in tests if test["start"] is not None]
    ends = [test["end"] for test in tests if test["end"] is not None]
    lines += ["", f"Reporter execution-start to first test: {fmt(max(0, min(starts) - execution) if execution is not None and starts else None)} s; last test to reporter finish: {fmt(max(0, finish - max(ends)) if finish is not None and ends else None)} s.",
              "A completion gap may contain running tests. An uncovered gap may contain fixture work, discovery, cleanup or unreported/incomplete tests; it does not establish idle CPU or a scheduler stall.",
              "TRX may only be written at graceful shutdown. Hard termination can leave no TRX; completed XML elements from a truncated report are recovered where possible."]
    return "\n".join(lines) + "\n"


def get(row, *keys):
    for key in keys:
        row = row.get(key) if isinstance(row, dict) else None
    return row


def peak(rows, *keys):
    values = [get(row, *keys) for row in rows]
    return max((value for value in values if number(value)), default=None)


def resource_window(host, start, end):
    selected = []
    for row in host:
        timestamp, seconds = instant(row.get("timestamp_utc")), row.get("interval_seconds")
        if timestamp is not None and number(seconds) and seconds > 0:
            overlap = min(timestamp, end) - max(timestamp - seconds, start)
            if overlap > 0:
                selected.append((row, overlap))
    def mean(*keys):
        values = [(get(row, *keys), weight) for row, weight in selected if number(get(row, *keys))]
        return sum(value * weight for value, weight in values) / sum(weight for _, weight in values) if values else None
    rows = [row for row, _ in selected]
    disks = []
    for device in sorted({key for row in rows for key in row.get("disks", {})}):
        disks.append(cell(device) + " " + fmt(peak(rows, "disks", device, "read_await_ms")) + "/" + fmt(peak(rows, "disks", device, "write_await_ms")))
    available = [get(row, "memory", "available_bytes") for row in rows if number(get(row, "memory", "available_bytes"))]
    throttle = [group.get("throttled_periods_percent") for row in rows for group in row.get("cgroups", []) if number(group.get("throttled_periods_percent"))]
    return [fmt(mean("cpu", "busy_percent")), fmt(mean("cpu", "iowait_percent")),
            fmt(min(available) / 1024**2 if available else None),
            "/".join(fmt(mean("pressure", kind, "some", "stall_percent")) for kind in ("cpu", "memory", "io")),
            fmt(mean("processes", "sqlservr", "cpu_percent")), fmt(max(throttle) if throttle else None),
            "; ".join(disks) or "unavailable", fmt(sum(weight for _, weight in selected))]


def consolidated(output, trx):
    waits, io, activity, fixtures, samples = [jsonl(output / name) for name in
        ("sql-waits.jsonl", "sql-io.jsonl", "sql-activity.jsonl", "fixture-timings.jsonl", "samples.jsonl")]
    host = [row for row in samples if row.get("kind") == "host"]
    lines = ["# CI diagnostic summary", "", "## Measured evidence", "",
             "All correlation windows use UTC. SQL/guest rates use independent monotonic intervals; SQL server UTC is also retained to expose clock offsets.",
             "The companion test-timings.md ranks the 25 slowest tests and all classes, with median, p90, p95, overlap and gap analysis.", ""]
    lines += timing_report(trx).splitlines()[2:]  # Preserve the complete per-test evidence in this standalone report too.
    ranked = {}
    for sample in waits:
        for row in sample.get("rows", []):
            change = row.get("delta", {})
            if row.get("idle") or not all(number(change.get(key)) for key in ("wait_time_ms", "waiting_tasks_count", "signal_wait_time_ms")):
                continue
            value = ranked.setdefault(row["wait_type"], {"category": row.get("category"), "wait_time_ms": 0, "waiting_tasks_count": 0, "signal_wait_time_ms": 0})
            for key in ("wait_time_ms", "waiting_tasks_count", "signal_wait_time_ms"):
                value[key] += change[key]
    lines += ["", "## SQL waits ranked by accumulated interval time", "", "| Wait | Category | Wait (ms) | Tasks | Signal (ms) |", "| --- | --- | ---: | ---: | ---: |"]
    for name, row in sorted(ranked.items(), key=lambda pair: pair[1]["wait_time_ms"], reverse=True)[:25]:
        lines.append(f"| {cell(name)} | {cell(row['category'])} | {fmt(row['wait_time_ms'])} | {row['waiting_tasks_count']} | {fmt(row['signal_wait_time_ms'])} |")
    counts = sorted(ranked.items(), key=lambda pair: pair[1]["waiting_tasks_count"], reverse=True)[:10]
    lines += ["", "Ranked by interval task count: " + ("; ".join(f"{cell(name)}={row['waiting_tasks_count']}" for name, row in counts) or "unavailable"),
              "Routine background/idle waits are excluded here. All observed nonzero raw counters, idle classification, interval deltas and reset reasons remain in sql-waits.jsonl. Wait times accumulate across workers and may exceed wall time."]
    lines += ["", "Priority wait families (only observed comparable counters):", "", "| Family | Wait (ms) | Tasks | Signal (ms) |", "| --- | ---: | ---: | ---: |"]
    for category in ("log_io", "page_read_io", "locks", "memory_latches", "cpu_scheduling", "general_io", "memory_grants", "workers"):
        values = [row for row in ranked.values() if row["category"] == category]
        lines.append("| " + category + " | " + " | ".join(fmt(sum(row[key] for row in values)) if values else "unavailable/no observed nonzero wait" for key in ("wait_time_ms", "waiting_tasks_count", "signal_wait_time_ms")) + " |")
    lines += ["", "## SQL file I/O latency", "", "| Database category / file kind | Reads | Read bytes | Read stall (ms) | Read latency (ms/op) | Peak interval file read latency | Writes | Write bytes | Write stall (ms) | Write latency (ms/op) | Peak interval file write latency |", "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"]
    groups = defaultdict(list)
    for sample in io:
        for row in sample.get("rows", []):
            if number(get(row, "delta", "num_of_reads")):
                groups[(row.get("database_kind"), row.get("file_kind"))].append(row)
    for label, rows in sorted(groups.items()):
        values = []
        for direction, operations, bytekey in (("read", "num_of_reads", "num_of_bytes_read"), ("write", "num_of_writes", "num_of_bytes_written")):
            count = sum(row["delta"][operations] for row in rows)
            size = sum(row["delta"][bytekey] for row in rows)
            stall = sum(row["delta"]["io_stall_" + direction + "_ms"] for row in rows)
            values += [str(count), str(size), fmt(stall), fmt(stall / count if count else None), fmt(peak(rows, direction + "_latency_ms"))]
        lines.append("| " + cell(" / ".join(label)) + " | " + " | ".join(values) + " |")
    baselines = sum(not number(get(row, "delta", "num_of_reads")) for sample in io for row in sample.get("rows", []))
    lines += ["", f"File observations without comparable deltas: {baselines}. Latency is total delta stall / total delta operations, not an average of file averages. Peaks are sampled per-file interval means, not individual I/O tail latency.",
              "Database/file IDs are paired with hashed incarnation/handle identity. New/reopened files, restarts, counter decreases and unidentified incarnations establish baselines. Deleted files and databases that live entirely between samples lose unobserved I/O.",
              "", "## SQL contention and memory observations", "", "| Metric | Sampled maximum | Observed UTC |", "| --- | ---: | --- |"]
    for label, keys in (("Active user requests", ("requests", "active_requests")),
                        ("Running/runnable user requests", ("requests", "running_or_runnable_requests")),
                        ("Waiting user requests", ("requests", "waiting_requests")),
                        ("Blocked user sessions", ("requests", "blocked_sessions")),
                        ("Current blocked wait (ms)", ("requests", "max_blocked_wait_ms")),
                        ("Total scheduler runnable tasks", ("schedulers", "runnable_tasks")),
                        ("One scheduler runnable tasks", ("schedulers", "max_scheduler_runnable_tasks")),
                        ("Scheduler queued work items", ("schedulers", "queued_work_items")),
                        ("SQL physical memory (KiB)", ("memory", "physical_memory_in_use_kb")),
                        ("SQL physical memory low flag", ("memory", "process_physical_memory_low")),
                        ("SQL virtual memory low flag", ("memory", "process_virtual_memory_low"))):
        maximum = peak(activity, *keys)
        observed = next((row.get("timestamp_utc") for row in activity if maximum is not None and get(row, *keys) == maximum), "unavailable")
        lines.append(f"| {label} | {fmt(maximum)} | {cell(observed)} |")
    request_waits = defaultdict(int)
    for sample in activity:
        for row in sample.get("request_waits", []):
            if number(row.get("request_count")):
                request_waits[row["wait_type"]] = max(request_waits[row["wait_type"]], row["request_count"])
    lines += ["", "Current request waits (maximum concurrent requests observed per type): " + ("; ".join(f"{cell(name)}={count}" for name, count in sorted(request_waits.items(), key=lambda pair: pair[1], reverse=True)[:20]) or "unavailable"),
              "Blocking duration is the longest sampled current blocked-request wait; changing waits, negative blocker IDs and blockers between snapshots prevent reconstruction of full blocking episodes. DMV sections are sequential, not one atomic snapshot. The diagnostic request is excluded.",
              "", "## Central fixture operations", "", "| Group | Operation | Count | Successful | Incomplete/failed | Total (s) | Peak (s) |", "| --- | --- | ---: | ---: | ---: | ---: | ---: |"]
    fixture_groups = defaultdict(list)
    for row in fixtures:
        if number(row.get("duration_seconds")):
            fixture_groups[(row.get("group", "unknown"), row.get("operation", "unknown"))].append(row)
    for (group, operation), rows in sorted(fixture_groups.items()):
        successful = sum(row.get("succeeded") is True for row in rows)
        lines.append(f"| {cell(group)} | {cell(operation)} | {len(rows)} | {successful} | {len(rows) - successful} | {fmt(sum(row['duration_seconds'] for row in rows))} | {fmt(peak(rows, 'duration_seconds'))} |")
    lines += ["", "Fixture spans may nest/overlap test durations; do not add them to suite wall time. Coverage is limited to shared baseline/journey initialization, DACPAC deployment, Hangfire setup, the shared journey seed and migration test DACPAC helper; scattered per-test seeding and migration subprocess internals are not separately instrumented.",
              "", "## UTC correlation windows", "", "| Window | Start UTC | End UTC | Guest CPU mean % | I/O wait mean % | Min available MiB | CPU/memory/I/O PSI mean % | SQL process CPU mean % | Peak cgroup throttle % | Guest disk peak interval read/write await (ms) | Host coverage (s) |", "| --- | --- | --- | ---: | ---: | ---: | --- | ---: | ---: | --- | ---: |"]
    windows = [("Test: " + test["name"], test["start"], test["end"]) for test in sorted(trx["tests"], key=lambda row: row["seconds"], reverse=True)[:5]]
    windows += [("No recorded test interval", start, end) for start, end in sorted(timeline(trx["tests"])["gaps"], key=lambda pair: pair[1] - pair[0], reverse=True)[:3]]
    file_windows = [(row.get("write_latency_ms"), "SQL file write latency", sample) for sample in io for row in sample.get("rows", [])]
    file_windows += [(row.get("read_latency_ms"), "SQL file read latency", sample) for sample in io for row in sample.get("rows", [])]
    for value, label, sample in sorted((entry for entry in file_windows if number(entry[0])), key=lambda entry: entry[0], reverse=True)[:3]:
        windows.append((label + " " + fmt(value) + " ms/op", instant(sample.get("interval_start_utc")), instant(sample.get("timestamp_utc"))))
    wait_windows = [(sum(get(row, "delta", "wait_time_ms") or 0 for row in sample.get("rows", []) if not row.get("idle")), sample) for sample in waits]
    for value, sample in sorted(wait_windows, key=lambda pair: pair[0], reverse=True)[:3]:
        if value > 0:
            windows.append(("SQL interval waits " + fmt(value) + " ms", instant(sample.get("interval_start_utc")), instant(sample.get("timestamp_utc"))))
    for label, start, end in windows:
        if start is not None and end is not None and end > start:
            lines.append("| " + " | ".join([cell(label), utc(start), utc(end)] + resource_window(host, start, end)) + " |")
    lines += ["", "Guest interval averages are weighted by their overlap with each window, assuming uniform activity inside a sample. Disk peaks are whole overlapping interval averages, kept separate by device. Short tests share coarse samples; this is temporal association, not attribution.",
              "", "## Evidence gaps and interpretation", ""]
    for label, rows in (("SQL waits", waits), ("SQL I/O", io), ("SQL activity", activity), ("fixture operations", fixtures), ("guest samples", host)):
        lines.append(f"- {label}: {len(rows)} flushed records.")
    missing = sorted({name for row in activity for name in row.get("missing_sections", [])})
    lines += ["- Unavailable SQL sections: " + (", ".join(missing) or "none reported (inspect record counts above)."),
              "- Initial samples are baselines; no startup cumulative SQL totals are interpreted as CI work. A failed sample can extend the next valid interval. Wait resets without an observable counter decrease cannot always be detected.",
              "- SQL sampling is approximately 60 seconds plus bounded collection time. No forced final SQL query runs on shutdown; work after the last completed sample and short contention spikes can be missed. Raw cumulative counters are retained for review.",
              "- Guest disk await and SQL file stalls measure different layers. SQL CPU comes from the existing process/container collectors; no duplicate process sampler is added.",
              "- TRX/fixture output can be absent after abrupt termination. Completed JSONL lines and fully parsed TRX results remain usable. Upload may be prevented by host loss or a hard job timeout.",
              "- Guest telemetry cannot identify physical-host scheduling/storage contention; optional Windows reports stay local and require synchronized UTC clocks.",
              "", "## Hypotheses to investigate", "",
              "Compare WRITELOG/PAGEIOLATCH/general I/O intervals with guest disk await and I/O PSI; lock waits with blocked sessions; SOS_SCHEDULER_YIELD with runnable queues and CPU pressure; RESOURCE_SEMAPHORE/THREADPOOL with memory/worker observations. Slow tests or fixture operations with quiet SQL and guest samples suggest application-side waiting or unobserved host stalls. These are investigation paths, not established causes."]
    resource_report = output / "summary.md"
    if resource_report.exists():
        lines += ["", "## Existing CPU, memory, disk, PSI, process, Docker and cgroup report", "", resource_report.read_text(encoding="utf-8")]
    return "\n".join(lines) + "\n"


def write_reports(output):
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    trx = read_trx(output / "test-results.trx")
    for name, text in (("test-timings.md", timing_report(trx)), ("diagnostic-summary.md", consolidated(output, trx))):
        temporary = output / (name + ".tmp")
        temporary.write_text(text, encoding="utf-8")
        temporary.replace(output / name)
    return output / "diagnostic-summary.md"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", default=".artifacts/ci-performance")
    args = parser.parse_args()
    try:
        path = write_reports(args.output)
        print("CI diagnostic report generated.")
        import os
        destination = os.environ.get("GITHUB_STEP_SUMMARY")
        if destination:
            with open(destination, "a", encoding="utf-8") as stream:
                stream.write(path.read_text(encoding="utf-8"))
    except Exception as error:
        print("::warning::CI diagnostic reporting unavailable (" + type(error).__name__ + "); existing evidence retained.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
