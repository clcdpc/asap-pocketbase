"""Opt-in, read-only runner lifecycle evidence. Never export raw journal messages,
process arguments/environments, unrestricted service properties or package logs.
Flushed console records survive when a service stop prevents artifact upload.
"""

import argparse
import datetime
import hashlib
import json
from pathlib import Path
import re
import signal
import threading
import time


UNITS = ("clc-runner.service", "docker.service", "apt-daily.service",
         "apt-daily-upgrade.service", "unattended-upgrades.service",
         "systemd-oomd.service", "apt-daily.timer", "apt-daily-upgrade.timer")
PROCESSES = {"apt", "apt-get", "dpkg", "needrestart", "unattended-upgr",
             "unattended-upgrade", "systemctl", "systemd-oomd", "shutdown", "reboot"}
PROPERTIES = ("Id", "ActiveState", "SubState", "Result", "MainPID", "NRestarts",
              "InvocationID", "ExecMainStartTimestampMonotonic", "ExecMainExitTimestampMonotonic",
              "LastTriggerUSecMonotonic", "NextElapseUSecMonotonic", "RandomizedDelayUSec")
STATES = {"active", "inactive", "activating", "deactivating", "failed", "reloading",
          "running", "waiting", "dead", "exited", "start", "start-pre", "start-post",
          "stop", "stop-sigterm", "stop-sigkill", "stop-post", "auto-restart"}
RESULTS = {"success", "resources", "timeout", "exit-code", "signal", "core-dump",
           "watchdog", "start-limit-hit", "oom-kill", "dependency", "skipped", "done"}


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="milliseconds")


def safe_property(key, value):
    if key not in PROPERTIES:
        return False
    if key == "Id":
        return value in UNITS
    if key in ("ActiveState", "SubState"):
        return value in STATES
    if key == "Result":
        return value in RESULTS
    if key == "InvocationID":
        return re.fullmatch(r"[a-f0-9]{32}", value) is not None
    if key == "RandomizedDelayUSec":
        return re.fullmatch(r"(?:\d+(?:\.\d+)?(?:us|ms|s|min|h|d|w|y)? ?)+", value) is not None
    return value.isdigit()


def journal_event(row):
    """Keep identity/state fields; the original MESSAGE can contain job secrets."""
    if not isinstance(row, dict):
        return None
    unit = row.get("UNIT", row.get("_SYSTEMD_UNIT"))
    message = str(row.get("MESSAGE", ""))
    timestamp = str(row.get("__REALTIME_TIMESTAMP", ""))
    timestamp = timestamp if timestamp.isdigit() else None
    if unit not in UNITS:
        if row.get("_TRANSPORT") == "kernel" and re.search(r"Out of memory:|oom-kill:|Killed process \d+", message):
            return {"event": "kernel_oom", "journal_us": timestamp}
        if row.get("SYSLOG_IDENTIFIER") == "needrestart":
            return {"event": "needrestart_activity", "journal_us": timestamp,
                    "mentions_runner": "clc-runner.service" in message}
        return None
    result = {"event": "service_journal", "unit": unit}
    for prefix, state in (("Stopping ", "stopping"), ("Stopped ", "stopped"),
                          ("Starting ", "starting"), ("Started ", "started")):
        if message.startswith(prefix):
            result["transition"] = state
    for source, target in (("__REALTIME_TIMESTAMP", "journal_us"), ("_PID", "pid"),
                           ("MESSAGE_ID", "message_id"), ("JOB_TYPE", "job_type"),
                           ("JOB_RESULT", "job_result"), ("RESULT", "result")):
        value = row.get(source)
        if not isinstance(value, str):
            continue
        valid = (value.isdigit() if target in ("journal_us", "pid") else
                 bool(re.fullmatch(r"[a-f0-9]{32}", value)) if target == "message_id" else
                 value in ("start", "stop", "restart", "try-restart", "reload") if target == "job_type" else
                 value in RESULTS)
        if valid:
            result[target] = value
    return result


def package_event(line, source):
    if source == "dpkg":
        match = re.fullmatch(r"(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) (upgrade|install|remove) ([\w.+:-]+) ([\w.+:~<>-]+) ([\w.+:~<>-]+)", line)
        if match:
            return dict(zip(("package_time", "action", "package", "old_version", "new_version"), match.groups()), event="package_change")
    elif source == "unattended":
        match = re.match(r"(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)[,.]\d+ (?:INFO|WARNING) (.*)", line)
        if match:
            message = match[2]
            for text, event in (("Starting unattended upgrades script", "unattended_started"),
                                ("All upgrades installed", "unattended_installed"),
                                ("Packages that will be upgraded:", "unattended_packages"),
                                ("Found /var/run/reboot-required", "reboot_required")):
                if message.startswith(text):
                    return {"event": event, "package_time": match[1]}
    elif source == "unattended_dpkg":
        # needrestart's service commands are written to this update log, outside
        # the runner cgroup. Export only known service names, never raw commands.
        if re.match(r"\s*systemctl (?:try-)?restart ", line):
            units = sorted(set(line.split()) & set(UNITS))
            if units:
                return {"event": "update_log_restart_command", "units": units}
    return None


class Collector:
    def __init__(self, output, probe, proc=Path("/proc")):
        self.output, self.probe, self.proc = Path(output), probe, Path(proc)
        self.stop = threading.Event()
        self.cursor = None
        self.seen_packages = set()
        self.last_processes = None
        self.last_state = None
        self.next_state = 0
        self.output.parent.mkdir(parents=True, exist_ok=True)
        self.stream = self.output.open("a", encoding="utf-8", buffering=1)
        self.thread = None

    def emit(self, row):
        text = json.dumps({"observed_utc": utc_now(), **row}, separators=(",", ":"), ensure_ascii=True)
        self.stream.write(text + "\n")
        self.stream.flush()
        print("[runner-lifecycle] " + text, flush=True)

    def command(self, arguments):
        return self.probe(arguments, self.stop, timeout=1)

    def processes(self):
        result = []
        for path in self.proc.glob("[0-9]*"):
            try:
                name = (path / "comm").read_text().strip()
                if name not in PROCESSES:
                    continue
                # No cmdline/environ reads. Parent PID/name identifies ancestry without arguments.
                status = (path / "status").read_text()
                parent = int(re.search(r"(?m)^PPid:\s*(\d+)", status)[1])
                parent_name = (self.proc / str(parent) / "comm").read_text().strip()
                result.append({"pid": int(path.name), "name": name, "parent_pid": parent,
                               "parent_name": parent_name if parent_name in PROCESSES else "other"})
            except (OSError, ValueError, TypeError):
                continue
        return sorted(result, key=lambda row: row["pid"])

    def sample(self):
        journal = ["sudo", "-n", "journalctl", "--no-pager", "--output=json", "--lines=200"]
        journal += ["--after-cursor=" + self.cursor] if self.cursor else ["--boot"]
        journal += ["_PID=1"]
        for match in ("_TRANSPORT=kernel", "SYSLOG_IDENTIFIER=needrestart",
                      "_SYSTEMD_UNIT=apt-daily.service", "_SYSTEMD_UNIT=apt-daily-upgrade.service",
                      "_SYSTEMD_UNIT=unattended-upgrades.service"):
            journal += ["+", match]
        raw = self.command(journal)
        journal_available = raw is not None
        if raw is not None:
            for line in raw.splitlines():
                try:
                    row = json.loads(line)
                except ValueError:
                    continue
                if not isinstance(row, dict):
                    continue
                cursor = row.get("__CURSOR")
                if isinstance(cursor, str) and re.fullmatch(r"[A-Za-z0-9;=]+", cursor):
                    self.cursor = cursor
                event = journal_event(row)
                if event:
                    self.emit(event)

        processes = self.processes()
        if processes != self.last_processes:
            self.emit({"event": "update_processes", "processes": processes})
            self.last_processes = processes

        package_available = {}
        for source, path in (("dpkg", "/var/log/dpkg.log"),
                             ("unattended", "/var/log/unattended-upgrades/unattended-upgrades.log"),
                             ("unattended_dpkg", "/var/log/unattended-upgrades/unattended-upgrades-dpkg.log")):
            raw = self.command(["sudo", "-n", "tail", "-n", "80", path])
            package_available[source] = raw is not None
            for line in (raw or "").splitlines():
                event = package_event(line, source)
                identity = hashlib.sha256(line.encode()).hexdigest()
                if event and identity not in self.seen_packages:
                    self.seen_packages.add(identity)
                    self.emit(event)

        if time.monotonic() >= self.next_state:
            raw = self.command(["systemctl", "show", "--no-pager", "--property=" + ",".join(PROPERTIES), *UNITS])
            state = []
            for block in (raw or "").split("\n\n"):
                fields = dict(line.split("=", 1) for line in block.splitlines() if "=" in line)
                if fields.get("Id") in UNITS:
                    state.append({key: value for key, value in fields.items()
                                  if safe_property(key, value)})
            if state != self.last_state:
                self.emit({"event": "service_state", "units": state, "journal_available": journal_available,
                           "package_logs_available": package_available, "systemctl_available": raw is not None})
                self.last_state = state
            memory = {}
            try:
                for line in (self.proc / "meminfo").read_text().splitlines():
                    parts = line.split()
                    if parts[0] in ("MemTotal:", "MemAvailable:", "SwapTotal:", "SwapFree:") and parts[1].isdigit():
                        memory[parts[0].rstrip(":") + "_kib"] = int(parts[1])
                oom = re.search(r"(?m)^oom_kill (\d+)$", (self.proc / "vmstat").read_text())
                memory["kernel_oom_kill_count"] = int(oom[1]) if oom else None
            except OSError:
                pass
            self.emit({"event": "resource_state", **memory})
            self.next_state = time.monotonic() + 30

    def collect(self):
        self.emit({"event": "collector_started", "limits": "2s polling; bounded history; process-name observations cannot prove the restart caller"})
        try:
            while not self.stop.is_set():
                self.sample()
                self.stop.wait(2)
        except Exception as error:
            # Only the exception class is safe to export; errors may contain raw CLI output.
            self.emit({"event": "collector_unavailable", "error_type": type(error).__name__})
        finally:
            self.emit({"event": "collector_stopped"})
            self.stream.close()

    def start(self):
        self.thread = threading.Thread(target=self.collect, name="runner-lifecycle", daemon=True)
        self.thread.start()
        return self

    def finish(self):
        self.stop.set()
        if self.thread is not None:
            self.thread.join(timeout=2)


def main():
    from performance import optional_command
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", default=".artifacts/runner-lifecycle/events.jsonl")
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()
    collector = Collector(args.output, optional_command)
    if args.once:
        try:
            collector.sample()
        finally:
            collector.stream.close()
        return
    # Also usable over a read-only SSH connection outside the runner service's cgroup.
    for signum in (signal.SIGINT, signal.SIGTERM):
        signal.signal(signum, lambda *_: collector.stop.set())
    collector.collect()


if __name__ == "__main__":
    main()
