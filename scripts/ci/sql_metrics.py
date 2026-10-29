"""Low-frequency, allow-listed DMV snapshots of the existing disposable asap-sql.

Credentials travel only through stdin to SQLCMDPASSWORD inside the container.
Raw CLI output/errors are never persisted. All cumulative counters are retained;
only comparable complete snapshots produce deltas.
"""

import datetime
import json
import os
from pathlib import Path
import re
import threading
import time


SECTIONS = ("meta", "waits", "io", "requests", "request_waits", "schedulers", "memory")
WAIT_COUNTERS = ("waiting_tasks_count", "wait_time_ms", "signal_wait_time_ms")
IO_COUNTERS = ("num_of_reads", "num_of_bytes_read", "io_stall_read_ms", "num_of_writes",
               "num_of_bytes_written", "io_stall_write_ms", "io_stall")
ACTIVITY_FIELDS = {
    "requests": ("active_requests", "running_or_runnable_requests", "waiting_requests", "blocked_sessions", "max_blocked_wait_ms"),
    "schedulers": ("online_schedulers", "runnable_tasks", "max_scheduler_runnable_tasks", "queued_work_items", "active_workers", "current_workers"),
    "memory": ("physical_memory_in_use_kb", "locked_page_allocations_kb", "memory_utilization_percentage", "page_fault_count", "process_physical_memory_low", "process_virtual_memory_low"),
}
IDLE_WAITS = frozenset("""BROKER_EVENTHANDLER BROKER_RECEIVE_WAITFOR BROKER_TASK_STOP
BROKER_TO_FLUSH BROKER_TRANSMITTER CHECKPOINT_QUEUE CHKPT CLR_AUTO_EVENT CLR_MANUAL_EVENT
DIRTY_PAGE_POLL DISPATCHER_QUEUE_SEMAPHORE FT_IFTS_SCHEDULER_IDLE_WAIT
HADR_FILESTREAM_IOMGR_IOCOMPLETION HADR_NOTIFICATION_DEQUEUE HADR_TIMER_TASK HADR_WORK_QUEUE
LAZYWRITER_SLEEP LOGMGR_QUEUE ONDEMAND_TASK_QUEUE QDS_ASYNC_QUEUE QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP
QDS_PERSIST_TASK_MAIN_LOOP_SLEEP REQUEST_FOR_DEADLOCK_SEARCH RESOURCE_QUEUE SERVER_IDLE_CHECK
SP_SERVER_DIAGNOSTICS_SLEEP SQLTRACE_BUFFER_FLUSH SQLTRACE_INCREMENTAL_FLUSH_SLEEP
SQLTRACE_WAIT_ENTRIES WAITFOR WAITFOR_TASKSHUTDOWN XE_DISPATCHER_JOIN XE_DISPATCHER_WAIT XE_TIMER_EVENT""".split())


def wait_category(name):
    for prefix, category in (("WRITELOG", "log_io"), ("PAGEIOLATCH_", "page_read_io"),
                             ("LCK_M_", "locks"), ("PAGELATCH_", "memory_latches"),
                             ("SOS_SCHEDULER_YIELD", "cpu_scheduling"), ("RESOURCE_SEMAPHORE", "memory_grants"),
                             ("THREADPOOL", "workers")):
        if name.startswith(prefix):
            return category
    return "general_io" if name in ("IO_COMPLETION", "ASYNC_IO_COMPLETION", "WRITE_COMPLETION", "BACKUPIO") else "other"


def idle_wait(name):
    return name in IDLE_WAITS or name.startswith(("SLEEP_", "WAIT_XTP_"))


def integer(value):
    # Reject booleans, floats, negatives and arbitrary strings at the export boundary.
    return value if type(value) is int and value >= 0 else None


def counters(row, keys):
    return {key: integer(row.get(key)) for key in keys}


def token(value, pattern):
    return value if isinstance(value, str) and re.fullmatch(pattern, value) else None


def parse_sections(output):
    """sqlcmd can wrap long JSON rows; markers require a fully completed section."""
    result, current, parts = {}, None, []
    for line in (output or "").splitlines():
        marker = re.fullmatch(r"CI_(META|WAITS|IO|REQUESTS|REQUEST_WAITS|SCHEDULERS|MEMORY)_(BEGIN|END|UNAVAILABLE)", line.strip())
        if marker:
            name, action = marker[1].lower(), marker[2]
            if action == "END" and current == name:
                try:
                    value = json.loads("".join(parts))
                    if isinstance(value, list if name in ("waits", "io", "request_waits") else dict):
                        result[name] = value
                except ValueError:
                    pass
            current, parts = (name, []) if action == "BEGIN" else (None, [])
        elif current is not None:
            parts.append(line.strip())
    return result


class SqlMetrics:
    # Fixed local target. No server/connection-string override is accepted.
    COMMAND = ["docker", "exec", "--interactive", "asap-sql", "/bin/sh", "-c",
               "IFS= read -r SQLCMDPASSWORD || exit 1; export SQLCMDPASSWORD; "
               "exec timeout --signal=TERM --kill-after=1s 7s /opt/mssql-tools18/bin/sqlcmd "
               "-S localhost -U sa -C -d master -l 2 -t 3 -b -y 0 -w 65535"]

    def __init__(self, command, stop, unavailable):
        self.command, self.stop, self.unavailable = command, stop, unavailable
        self.previous = {}

    def sample(self):
        password = os.environ.get("ASAP_TEST_SQL_SA_PASSWORD", "")
        started = datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="milliseconds")
        begin = time.monotonic()
        output = None
        if password and not any(char in password for char in "\r\n\0"):
            query = Path(__file__).with_name("sql-diagnostics.sql").read_text(encoding="utf-8")
            output = self.command(self.COMMAND, self.stop, timeout=8, input_text=password + "\n" + query, preserve_partial=True)
        else:
            self.unavailable.add("SQL DMV credentials unavailable; no alternative connection attempted")
        return self.observe(parse_sections(output), started, time.monotonic(), time.monotonic() - begin)

    def observe(self, sections, started, now, collection_seconds=0):
        meta = sections.get("meta", {})
        epoch = token(meta.get("engine_epoch"), r"[0-9a-fA-F]{64}")
        timestamp = datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="milliseconds")
        sql_time = token(meta.get("sql_timestamp_utc"), r"[0-9T:.+-]+Z")
        common = {"timestamp_utc": timestamp, "sample_started_utc": started,
                  "sql_timestamp_utc": sql_time, "engine_epoch": epoch,
                  "uptime_ms": integer(meta.get("uptime_ms")), "collection_seconds": collection_seconds}
        missing = [name for name in SECTIONS if name not in sections]
        if not epoch and "meta" not in missing:
            missing.append("meta")
        for name in missing:
            self.unavailable.add("SQL " + name + " DMV section unavailable (missing, denied, timed out or incomplete)")
        if not epoch:
            self.previous.clear()
        rows = {}
        for name in ("waits", "io"):
            old = self.previous.get(name)
            comparable = bool(epoch and old and old["epoch"] == epoch and now > old["time"])
            reason = "interval" if comparable else "baseline_or_engine_restart"
            data = sections.get(name)
            record = {**common, "kind": "sql_" + name, "available": data is not None,
                      "interval_seconds": now - old["time"] if comparable else None,
                      "interval_start_utc": old["timestamp"] if comparable else None, "rows": []}
            rows[name] = record
            if data is None:
                continue  # A failed section does not destroy the last good baseline.
            current = {}
            for source in data:
                if not isinstance(source, dict):
                    continue
                if name == "waits":
                    identity = token(source.get("wait_type"), r"[A-Z][A-Z0-9_]{0,127}")
                    if identity:
                        current[identity] = {"wait_type": identity, "idle": idle_wait(identity),
                                             "category": wait_category(identity), **counters(source, WAIT_COUNTERS)}
                else:
                    dbid, fileid = integer(source.get("database_id")), integer(source.get("file_id"))
                    if dbid is not None and fileid is not None:
                        current[(dbid, fileid)] = {"database_id": dbid, "file_id": fileid,
                            "file_identity": token(source.get("file_identity"), r"[0-9a-fA-F]{64}"),
                            "database_kind": source.get("database_kind") if source.get("database_kind") in ("tempdb", "system", "synthetic_test") else "other",
                            "file_kind": source.get("file_kind") if source.get("file_kind") in ("data", "log") else "other",
                            "sample_ms": integer(source.get("sample_ms")), **counters(source, IO_COUNTERS)}
            keys = WAIT_COUNTERS if name == "waits" else IO_COUNTERS
            if comparable and name == "waits":
                # Any decrease/disappearing positive wait implies a global wait reset.
                reset = any(current.get(key, {}).get(field, 0) is not None and value.get(field) is not None
                            and current.get(key, {}).get(field, 0) < value[field]
                            for key, value in old["rows"].items() for field in keys)
                if reset:
                    comparable, reason = False, "wait_counter_reset"
                    record["interval_seconds"] = record["interval_start_utc"] = None
            for key, value in current.items():
                before = old["rows"].get(key) if comparable else None
                status = reason
                if name == "waits" and comparable and before is None:
                    before = dict.fromkeys(keys, 0)  # Complete prior snapshot omitted only zero waits.
                if name == "io" and comparable:
                    if not before or not value["file_identity"] or value["file_identity"] != before["file_identity"]:
                        before, status = None, "new_or_reopened_file_baseline"
                    elif value["sample_ms"] is None or before["sample_ms"] is None or value["sample_ms"] < before["sample_ms"]:
                        before, status = None, "file_counter_reset"
                changes = {field: value[field] - before[field] if before and value[field] is not None
                           and before.get(field) is not None and value[field] >= before[field] else None for field in keys}
                if any(change is None for change in changes.values()):
                    if before:
                        status = "counter_reset_or_missing"
                    changes = dict.fromkeys(keys)
                value.update(delta=changes, delta_status=status)
                if name == "io":
                    for direction, operations in (("read", "num_of_reads"), ("write", "num_of_writes")):
                        count = changes[operations]
                        value[direction + "_latency_ms"] = changes["io_stall_" + direction + "_ms"] / count if count else None
                record["rows"].append(value)
            if epoch:
                self.previous[name] = {"epoch": epoch, "time": now, "timestamp": timestamp, "rows": current}
        activity = {**common, "kind": "sql_activity", "missing_sections": missing}
        for name, keys in ACTIVITY_FIELDS.items():
            source = sections.get(name)
            activity[name] = counters(source, keys) if isinstance(source, dict) else None
        activity["request_waits"] = [
            {"wait_type": source["wait_type"], **counters(source, ("request_count", "max_wait_ms"))}
            for source in sections.get("request_waits", []) if isinstance(source, dict)
            and token(source.get("wait_type"), r"[A-Z][A-Z0-9_]{0,127}")]
        rows["activity"] = activity
        return rows


class SqlCollector:
    def __init__(self, output, interval, command, cancelled, unavailable):
        self.output, self.interval, self.unavailable = Path(output), interval, unavailable
        self.stop = threading.Event()
        self.metrics = SqlMetrics(command, cancelled, unavailable)
        self.latest_activity = {}
        self.thread = threading.Thread(target=self.collect, name="ci-sql-diagnostics", daemon=True)
        self.thread.start()

    def collect(self):
        streams = {}
        try:
            for name in ("waits", "io", "activity"):
                streams[name] = (self.output / ("sql-" + name + ".jsonl")).open("w", encoding="utf-8", buffering=1)
            while not self.stop.is_set():
                try:
                    rows = self.metrics.sample()
                    self.latest_activity = rows["activity"]
                    for name, row in rows.items():
                        streams[name].write(json.dumps(row, separators=(",", ":"), allow_nan=False) + "\n")
                        streams[name].flush()
                except Exception as error:
                    self.unavailable.add("SQL sample failed: " + type(error).__name__)
                if self.stop.wait(self.interval):
                    break
        except Exception as error:
            self.unavailable.add("SQL collector stopped: " + type(error).__name__)
        finally:
            for stream in streams.values():
                stream.close()

    def finish(self):
        self.stop.set()
        self.thread.join(timeout=2)
        if self.thread.is_alive():
            self.unavailable.add("SQL collector shutdown exceeded 2 s; partial evidence retained")
