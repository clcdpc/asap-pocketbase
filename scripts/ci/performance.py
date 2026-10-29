#!/usr/bin/env python3
"""Best-effort Linux CI telemetry around an unchanged command; stdlib only.

Never read process command lines/environments or unrestricted Docker inspect data.
CPU and disk counters are differenced before use. See README.md for scope/units.
"""

import argparse
import datetime
import json
import math
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import subprocess
import sys
import threading
import time


CPU_FIELDS = ("user", "nice", "system", "idle", "iowait", "irq", "softirq", "steal")
DISK_FIELDS = ("reads", "reads_merged", "read_sectors", "read_ms", "writes",
               "writes_merged", "write_sectors", "write_ms", "in_flight", "io_ms", "weighted_ms")
LIMITATIONS = [
    "Measurements describe the Linux guest, not Windows physical disk latency or Hyper-V scheduling contention. Zero steal time does not rule out host contention.",
    "Guest CPU busy excludes idle, I/O wait and steal; /proc/stat I/O wait is approximate and may decrease. Counter decreases/resets omit the affected interval.",
    "Disks are reported separately, never summed across devices/layers. Await is completed-I/O time / completed operations; busy time is approximate on parallel devices.",
    "Process CPU uses surviving PID/start-time pairs; short-lived processes can be missed. RSS sums can count shared pages more than once. Process/container CPU: 100% = one logical CPU.",
    "Docker CPU/memory are sparse CLI observations, not whole-minute averages; peaks can be missed. Memory excludes cache as reported by Docker; block-byte counters are rounded by its CLI.",
    "Only visible cgroup v2 ancestors are inspected. Hidden ancestor limits and cgroup v1 metrics are unavailable. Runner cgroups need not contain the sibling SQL container.",
    "Only normal shutdown or catchable signals permit finalization. SIGKILL/host loss can leave a partial JSONL line; flushed samples and minute health logs are the fallback.",
]


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="milliseconds")


def warn(message):
    print("::warning::CI performance: " + message, file=sys.stderr, flush=True)


def delta(previous, current):
    if previous is None or current is None or current < previous:
        return None
    return current - previous


def numeric(value):
    try:
        result = float(value)
        return result if math.isfinite(result) and result >= 0 else None
    except (ValueError, TypeError):
        return None


def key_values(text):
    result = {}
    for line in text.splitlines():
        parts = line.replace(":", " ").split()
        if len(parts) >= 2 and parts[1].isdigit():
            result[parts[0]] = int(parts[1])
    return result


def cpu_counters(text):
    for line in text.splitlines():
        parts = line.split()
        if parts and parts[0] == "cpu":
            # guest/guest_nice already belong to user/nice; do not count twice.
            return dict(zip(CPU_FIELDS, map(int, parts[1:9])))
    return {}


def cpu_percentages(previous, current):
    changes = [delta(previous.get(key), current.get(key)) for key in CPU_FIELDS]
    result = dict.fromkeys(("busy_percent", "idle_percent", "iowait_percent", "steal_percent"))
    if any(value is None for value in changes) or sum(changes) <= 0:
        return result
    total = sum(changes)
    result.update(idle_percent=100 * changes[3] / total,
                  iowait_percent=100 * changes[4] / total,
                  steal_percent=100 * changes[7] / total,
                  busy_percent=100 * sum(changes[index] for index in (0, 1, 2, 5, 6)) / total)
    return result


def disk_counters(text):
    result = {}
    for line in text.splitlines():
        parts = line.split()
        if len(parts) >= 14:
            result[parts[2]] = dict(zip(DISK_FIELDS, map(int, parts[3:14])))
    return result


def disk_interval(previous, current, seconds):
    result = {"in_flight": current.get("in_flight")}
    if not previous or seconds <= 0:
        return result
    changes = {key: delta(previous.get(key), current.get(key))
               for key in DISK_FIELDS if key != "in_flight"}
    if any(value is None for value in changes.values()):
        return result
    # diskstats sectors always represent 512 bytes, regardless of device sector size.
    for direction, operation, sectors in (("read", "reads", "read_sectors"),
                                           ("write", "writes", "write_sectors")):
        operations = changes[operation]
        result.update({direction + "_operations": operations,
                       direction + "_bytes": changes[sectors] * 512,
                       direction + "_time_ms": changes[direction + "_ms"],
                       direction + "_ops_per_second": operations / seconds,
                       direction + "_bytes_per_second": changes[sectors] * 512 / seconds,
                       direction + "_await_ms": changes[direction + "_ms"] / operations if operations else None})
    result.update(io_time_ms=changes["io_ms"], weighted_io_time_ms=changes["weighted_ms"],
                  busy_percent=100 * changes["io_ms"] / (seconds * 1000),
                  average_queue_depth=changes["weighted_ms"] / (seconds * 1000))
    return result


def pressure_counters(text):
    result = {}
    for line in text.splitlines():
        parts = line.split()
        if parts and parts[0] in ("some", "full"):
            values = dict(part.split("=", 1) for part in parts[1:] if "=" in part)
            result[parts[0]] = {key: numeric(values.get(key)) for key in ("avg10", "avg60", "avg300", "total")}
    return result


def pressure_interval(previous, current, seconds):
    result = {}
    for scope, values in current.items():
        stalled = delta(previous.get(scope, {}).get("total"), values.get("total"))
        result[scope] = {key + "_percent": values.get(key) for key in ("avg10", "avg60", "avg300")}
        result[scope]["stall_percent"] = 100 * stalled / (seconds * 1e6) if stalled is not None and seconds > 0 else None
    return result


def process_stat(text):
    # comm may contain spaces/parentheses; all later fields are numeric or state.
    left, right = text.index("("), text.rindex(")")
    name = text[left + 1:right].lower()
    group = ("dotnet" if name == "dotnet" else "sqlservr" if name == "sqlservr"
             else "testhost" if name.startswith(("testhost", "asap.tests")) else None)
    if group is None:
        return None
    fields = text[right + 2:].split()
    return ((int(text[:left].strip()), int(fields[19])),
            {"group": group, "ticks": int(fields[11]) + int(fields[12]), "rss_pages": int(fields[21])})


def process_interval(previous, current, seconds, ticks_per_second, page_size):
    result = {name: {"count": 0, "rss_bytes": 0, "cpu_percent": None}
              for name in ("dotnet", "sqlservr", "testhost")}
    if current is None:
        return {name: dict.fromkeys(values) for name, values in result.items()}
    previous = previous or {}
    for identity, value in current.items():
        group = result[value["group"]]
        group["count"] += 1
        group["rss_bytes"] += max(0, value["rss_pages"]) * page_size
        old = previous.get(identity)
        change = delta(old["ticks"], value["ticks"]) if old else None
        if change is not None and seconds > 0:
            group["cpu_percent"] = (group["cpu_percent"] or 0) + 100 * change / ticks_per_second / seconds
    return result


def mounts(text):
    def unescape(value):
        return re.sub(r"\\([0-7]{3})", lambda match: chr(int(match[1], 8)), value)
    result = []
    for line in text.splitlines():
        before, separator, after = line.partition(" - ")
        first, last = before.split(), after.split()
        if separator and len(first) >= 5 and len(last) >= 2:
            result.append({"device": first[2], "root": unescape(first[3]),
                           "mount": unescape(first[4]), "type": last[0]})
    return result


def cgroup_interval(previous, current, seconds):
    result = {key: value for key, value in current.items() if key not in ("cpu", "events")}
    cpu, old = current.get("cpu", {}), previous.get("cpu", {})
    for key in ("usage_usec", "nr_periods", "nr_throttled", "throttled_usec"):
        result[key + "_delta"] = delta(old.get(key), cpu.get(key))
    periods, throttled = result["nr_periods_delta"], result["nr_throttled_delta"]
    result["throttled_periods_percent"] = 100 * throttled / periods if periods and throttled is not None else None
    result["cpu_percent"] = 100 * result["usage_usec_delta"] / (seconds * 1e6) if result["usage_usec_delta"] is not None and seconds > 0 else None
    result["memory_events_delta"] = {key: delta(previous.get("events", {}).get(key), value)
                                      for key, value in current.get("events", {}).items()}
    return result


class LinuxMetrics:
    def __init__(self, workspace, unavailable, proc=Path("/proc"), sysfs=Path("/sys")):
        self.proc, self.sysfs, self.workspace = Path(proc), Path(sysfs), Path(workspace).resolve()
        self.unavailable = unavailable
        self.page_size = os.sysconf("SC_PAGE_SIZE") if hasattr(os, "sysconf") else 4096
        self.ticks = os.sysconf("SC_CLK_TCK") if hasattr(os, "sysconf") else 100
        self.mounts = mounts(self.read(self.proc / "self/mountinfo", "filesystem mappings"))
        self.cgroups = self.find_cgroups()
        self.devices = self.find_devices()

    def read(self, path, label=None):
        try:
            return path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            if label:
                self.unavailable.add(label + " unavailable")
            return ""

    def find_cgroups(self):
        membership = self.read(self.proc / "self/cgroup", "cgroup membership")
        entry = next((line[3:] for line in membership.splitlines() if line.startswith("0::")), None)
        mount = next((value for value in self.mounts if value["type"] == "cgroup2"), None)
        if entry is None or mount is None:
            self.unavailable.add("cgroup v2 limits/events unavailable (v1 is not sampled)")
            return []
        root = Path(mount["mount"]).resolve()
        relative = os.path.relpath(entry, mount["root"]) if entry != "/" else "."
        leaf = (root / relative).resolve()
        if not leaf.is_relative_to(root):
            self.unavailable.add("cgroup namespace mapping unavailable")
            return []
        return [leaf] + list(leaf.parents)[:len(leaf.parts) - len(root.parts)]

    def filesystem(self, path):
        candidates = [entry for entry in self.mounts if path.is_relative_to(Path(entry["mount"]))]
        entry = max(candidates, key=lambda item: len(item["mount"])) if candidates else {}
        result = {"type": entry.get("type"), "device_major_minor": entry.get("device")}
        result["backing_device"] = self.block_device(entry.get("device"))
        try:
            usage = shutil.disk_usage(path)
            result.update(total_bytes=usage.total, free_bytes=usage.free)
        except OSError:
            self.unavailable.add("filesystem capacity unavailable")
        return result

    def block_device(self, device):
        link = self.sysfs / "dev/block" / str(device)
        if not link.exists():
            return None
        block = link.resolve()
        return block.parent.name if (block / "partition").exists() else block.name

    def find_devices(self):
        counters = disk_counters(self.read(self.proc / "diskstats", "disk counters"))
        workspace_device = self.filesystem(self.workspace).get("backing_device")
        # Keep other independent disks too: Docker storage may differ from the workspace.
        candidates = {name for name in counters if not name.startswith(("loop", "ram", "zram"))
                      and (self.sysfs / "class/block" / name).exists()
                      and not (self.sysfs / "class/block" / name / "partition").exists()}
        for name in (workspace_device, self.filesystem(Path("/")).get("backing_device")):
            if name in counters:
                candidates.add(name)
        underlying = set()
        for name in candidates:
            try:
                for item in (self.sysfs / "class/block" / name / "slaves").iterdir():
                    block = item.resolve()
                    underlying.add(block.parent.name if (block / "partition").exists() else block.name)
            except OSError:
                pass
        if workspace_device is None:
            self.unavailable.add("workspace block mapping unresolved; candidate guest disks cannot establish workspace/host physical I/O")
        if workspace_device in underlying:
            self.unavailable.add("workspace shares a lower disk layer; only top-level device counters are sampled to avoid duplicate I/O")
        return {"selection": "independent top-level guest devices, no totals", "workspace_device": workspace_device,
                "names": sorted(candidates - underlying)}

    def read_cgroups(self):
        result = []
        for level, path in enumerate(self.cgroups):
            maximum = self.read(path / "cpu.max").split()
            quota = numeric(maximum[0]) if maximum else None
            period = numeric(maximum[1]) if len(maximum) == 2 else None
            memory = self.read(path / "memory.max").strip()
            memory_high = self.read(path / "memory.high").strip()
            swap_max = self.read(path / "memory.swap.max").strip()
            cpuset = self.read(path / "cpuset.cpus.effective").strip()
            count = 0
            for part in cpuset.split(",") if cpuset else []:
                bounds = [int(value) for value in part.split("-")]
                count += bounds[-1] - bounds[0] + 1
            cpu = key_values(self.read(path / "cpu.stat"))
            events = key_values(self.read(path / "memory.events"))
            result.append({"level": level, "cpu_limit_cores": quota / period if quota is not None and period else None,
                           "cpu_limit_status": "unlimited" if maximum and maximum[0] == "max" else "limited" if quota is not None and period else "unavailable",
                           "cpuset_cpu_count": count or None, "memory_limit_bytes": numeric(memory),
                           "memory_limit_status": "unlimited" if memory == "max" else "limited" if numeric(memory) is not None else "unavailable",
                           "memory_high_bytes": numeric(memory_high),
                           "memory_high_status": "unlimited" if memory_high == "max" else "limited" if numeric(memory_high) is not None else "unavailable",
                           "memory_current_bytes": numeric(self.read(path / "memory.current").strip()),
                           "swap_limit_bytes": numeric(swap_max),
                           "swap_limit_status": "unlimited" if swap_max == "max" else "limited" if numeric(swap_max) is not None else "unavailable",
                           "swap_current_bytes": numeric(self.read(path / "memory.swap.current").strip()),
                           "cpu": {key: cpu[key] for key in ("usage_usec", "nr_periods", "nr_throttled", "throttled_usec") if key in cpu},
                           "events": {key: events[key] for key in ("low", "high", "max", "oom", "oom_kill", "oom_group_kill") if key in events}})
        if result and not result[0]["cpu"]:
            self.unavailable.add("leaf cgroup CPU counters unavailable")
        if result and "nr_throttled" not in result[0]["cpu"]:
            self.unavailable.add("leaf cgroup CPU throttling counters unavailable")
        if result and result[0]["memory_current_bytes"] is None:
            self.unavailable.add("leaf cgroup memory controller unavailable")
        if result and not result[0]["events"]:
            self.unavailable.add("leaf cgroup memory events unavailable")
        return result

    def environment(self):
        info = self.read(self.proc / "cpuinfo", "CPU model")
        model = next((line.split(":", 1)[1].strip() for line in info.splitlines()
                      if line.startswith(("model name", "Hardware")) and ":" in line), None)
        memory = key_values(self.read(self.proc / "meminfo", "memory counters"))
        dmi = " ".join(self.read(self.sysfs / "class/dmi/id" / name).lower() for name in ("sys_vendor", "product_name"))
        indicators = [label for match, label in (("microsoft", "Hyper-V"), ("vmware", "VMware"),
                      ("virtualbox", "VirtualBox"), ("kvm", "KVM"), ("qemu", "QEMU"), ("xen", "Xen")) if match in dmi]
        if "wsl" in platform.release().lower():
            indicators.append("WSL")
        github = {}
        for key, pattern in (("GITHUB_RUN_ID", r"\d{1,24}"), ("GITHUB_RUN_ATTEMPT", r"\d{1,6}"), ("GITHUB_SHA", r"[0-9a-fA-F]{40,64}")):
            value = os.environ.get(key, "")
            github[key.lower()] = value if re.fullmatch(pattern, value) else None
        return {"schema_version": 1, "started_utc": utc_now(), **github,
                "runner_type": os.environ.get("RUNNER_ENVIRONMENT") if os.environ.get("RUNNER_ENVIRONMENT") in ("github-hosted", "self-hosted") else "unknown",
                "operating_system": platform.system(), "kernel": platform.release(), "architecture": platform.machine(),
                "cpu_model": model[:200] if model else None, "logical_cpus": os.cpu_count(),
                "affinity_cpus": len(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else None,
                "memory_total_bytes": memory.get("MemTotal", 0) * 1024 or None,
                "virtualization": {"hypervisor_cpu_flag": "hypervisor" in info.split(), "indicators": indicators},
                "filesystems": {"root": self.filesystem(Path("/")), "workspace": self.filesystem(self.workspace)},
                "disks": self.devices, "cgroups": self.read_cgroups(), "limitations": LIMITATIONS}

    def snapshot(self):
        now = time.monotonic()
        processes = {}
        denied = False
        try:
            for path in self.proc.iterdir():
                if path.name.isdigit():
                    try:
                        parsed = process_stat((path / "stat").read_text(encoding="utf-8"))
                        if parsed:
                            processes[parsed[0]] = parsed[1]
                    except PermissionError:
                        denied = True
                    except (OSError, ValueError, IndexError):
                        pass  # An exiting process is an ordinary sampling race.
        except OSError:
            self.unavailable.add("process counters unavailable")
            processes = None
        if denied:
            self.unavailable.add("some process counters inaccessible without privileges")
        result = {"monotonic": now, "timestamp_utc": utc_now(),
                "cpu": cpu_counters(self.read(self.proc / "stat", "CPU counters")),
                "load": self.read(self.proc / "loadavg", "load averages").split()[:3],
                "memory": key_values(self.read(self.proc / "meminfo", "memory counters")),
                "vm": key_values(self.read(self.proc / "vmstat", "VM counters")),
                "pressure": {name: pressure_counters(self.read(self.proc / "pressure" / name, name + " pressure")) for name in ("cpu", "memory", "io")},
                "disks": disk_counters(self.read(self.proc / "diskstats", "disk counters")),
                "cgroups": self.read_cgroups(), "processes": processes}
        if any(key not in result["cpu"] for key in CPU_FIELDS):
            self.unavailable.add("some CPU counter fields unavailable")
        if "MemAvailable" not in result["memory"]:
            self.unavailable.add("MemAvailable unavailable; used memory is not estimated")
        for key in ("pswpin", "pswpout", "pgmajfault"):
            if key not in result["vm"]:
                self.unavailable.add(key + " VM counter unavailable")
        return result

    def interval(self, previous, current):
        previous = previous or {}
        seconds = current["monotonic"] - previous.get("monotonic", current["monotonic"])
        memory = {key + "_bytes": current["memory"].get(source, 0) * 1024 if source in current["memory"] else None
                  for key, source in (("total", "MemTotal"), ("available", "MemAvailable"), ("swap_total", "SwapTotal"), ("swap_free", "SwapFree"))}
        memory["used_bytes"] = memory["total_bytes"] - memory["available_bytes"] if memory["total_bytes"] is not None and memory["available_bytes"] is not None else None
        memory["swap_used_bytes"] = memory["swap_total_bytes"] - memory["swap_free_bytes"] if memory["swap_total_bytes"] is not None and memory["swap_free_bytes"] is not None else None
        for source, name, multiplier in (("pswpin", "swap_in_bytes", self.page_size), ("pswpout", "swap_out_bytes", self.page_size), ("pgmajfault", "major_faults", 1)):
            change = delta(previous.get("vm", {}).get(source), current["vm"].get(source))
            memory[name] = change * multiplier if change is not None else None
            memory[name + "_per_second"] = change * multiplier / seconds if change is not None and seconds > 0 else None
        return {"kind": "host", "timestamp_utc": current["timestamp_utc"], "interval_seconds": seconds,
                "cpu": {**cpu_percentages(previous.get("cpu", {}), current["cpu"]),
                        "load_1_5_15": [numeric(value) for value in current["load"]]},
                "memory": memory,
                "pressure": {name: pressure_interval(previous.get("pressure", {}).get(name, {}), values, seconds) for name, values in current["pressure"].items()},
                "disks": {name: disk_interval(previous.get("disks", {}).get(name, {}), current["disks"][name], seconds) for name in self.devices["names"] if name in current["disks"]},
                "cgroups": [cgroup_interval(next((old for old in previous.get("cgroups", []) if old["level"] == value["level"]), {}), value, seconds) for value in current["cgroups"]],
                "processes": process_interval(previous.get("processes", {}), current["processes"], seconds, self.ticks, self.page_size)}


def kill_group(process, signum):
    try:
        if os.name == "posix":
            os.killpg(process.pid, signum)
        else:
            process.send_signal(signum)
    except ProcessLookupError:
        pass


def optional_command(arguments, stop, timeout=4):
    """Bound and reap each optional CLI, including a hung Docker daemon/client."""
    process, completed = None, False
    try:
        if stop.is_set():
            return None
        process = subprocess.Popen(arguments, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                   stderr=subprocess.DEVNULL, text=True, start_new_session=os.name == "posix")
        deadline = time.monotonic() + timeout
        while not stop.is_set() and time.monotonic() < deadline:
            try:
                output, _ = process.communicate(timeout=0.1)
                completed = True
                return output.strip() if process.returncode == 0 else None
            except subprocess.TimeoutExpired:
                pass
        return None
    except OSError:
        return None
    finally:
        if process is not None and not completed:
            kill_group(process, signal.SIGKILL if os.name == "posix" else signal.SIGTERM)
            try:
                process.communicate(timeout=1)
            except subprocess.TimeoutExpired:
                process.stdout.close()
                warn("optional CLI did not finish shutdown; sample omitted.")


def byte_quantity(text):
    match = re.fullmatch(r"\s*([0-9]+(?:\.[0-9]+)?)\s*([kKMGTPE]?i?B)\s*", text)
    if not match:
        return None
    unit = match[2]
    exponent = "KMGTPE".find(unit[0].upper()) + 1 if unit != "B" else 0
    return float(match[1]) * (1024 if "i" in unit else 1000) ** exponent


def docker_stats(text):
    values = json.loads(text)
    if not isinstance(values, dict):
        raise ValueError("unexpected stats shape")
    memory = str(values.get("memory", "")).split("/")
    block = str(values.get("block_io", "")).split("/")
    return {"cpu_percent": numeric(str(values.get("cpu", "")).removesuffix("%")),
            "memory_usage_bytes": byte_quantity(memory[0]) if len(memory) == 2 else None,
            "memory_limit_bytes": byte_quantity(memory[1]) if len(memory) == 2 else None,
            "block_read_bytes": byte_quantity(block[0]) if len(block) == 2 else None,
            "block_write_bytes": byte_quantity(block[1]) if len(block) == 2 else None}


class DockerMetrics:
    INSPECT = ('{"state":{{json .State.Status}},"started":{{json .State.StartedAt}},'
               '"memory_bytes":{{.HostConfig.Memory}},"memory_swap_bytes":{{.HostConfig.MemorySwap}},'
               '"nano_cpus":{{.HostConfig.NanoCpus}},"cpu_quota_usec":{{.HostConfig.CpuQuota}},'
               '"cpu_period_usec":{{.HostConfig.CpuPeriod}},"cpuset_cpus":{{json .HostConfig.CpusetCpus}},'
               '"blkio_weight":{{.HostConfig.BlkioWeight}}}')
    STATS = '{"cpu":{{json .CPUPerc}},"memory":{{json .MemUsage}},"block_io":{{json .BlockIO}}}'

    def __init__(self, stop, unavailable):
        self.stop, self.unavailable, self.previous = stop, unavailable, None

    def inspect(self):
        text = optional_command(["docker", "inspect", "--format", self.INSPECT, "asap-sql"], self.stop)
        if text is None:
            self.unavailable.add("SQL container state/resource limits unavailable (Docker missing, denied, absent or timed out)")
            return {}
        source = json.loads(text)
        if not isinstance(source, dict):
            raise ValueError("unexpected inspect shape")
        # Allow-list all exported fields even when the supplied JSON has extra data.
        result = {key: source.get(key) if isinstance(source.get(key), int) else None
                  for key in ("memory_bytes", "memory_swap_bytes", "nano_cpus", "cpu_quota_usec", "cpu_period_usec", "blkio_weight")}
        cpuset = str(source.get("cpuset_cpus", ""))
        result["cpuset_cpus"] = cpuset if re.fullmatch(r"[0-9,-]*", cpuset) else None
        result["state"] = source.get("state") if source.get("state") in ("created", "running", "paused", "restarting", "removing", "exited", "dead") else "unknown"
        started = str(source.get("started", ""))
        result["started"] = started if re.fullmatch(r"[0-9TZ:.+-]+", started) else None
        return result

    def sample(self):
        inspected = self.inspect()
        result = {"kind": "sql_container", "timestamp_utc": utc_now(), "state": inspected.get("state", "unknown")}
        if inspected.get("state") != "running":
            self.previous = None
            self.unavailable.add("SQL Docker stats skipped: container was not observed running")
            return result
        text = optional_command(["docker", "stats", "--no-stream", "--format", self.STATS, "asap-sql"], self.stop)
        if text is None:
            self.unavailable.add("SQL Docker stats unavailable (missing, denied or timed out)")
            return result
        values = docker_stats(text)
        now = time.monotonic()
        result.update(values)
        seconds = now - self.previous["time"] if self.previous else None
        result["interval_seconds"] = seconds
        for key in ("block_read_bytes", "block_write_bytes"):
            change = delta(self.previous.get(key), values.get(key)) if self.previous and inspected.get("started") and self.previous["started"] == inspected["started"] else None
            result[key + "_delta"] = change
            result[key + "_per_second"] = change / seconds if change is not None and seconds else None
        self.previous = {**values, "time": now, "started": inspected.get("started")}
        return result


def read_samples(path):
    rows = []
    try:
        with path.open(encoding="utf-8") as source:
            for line in source:
                try:
                    row = json.loads(line)
                    if isinstance(row, dict):
                        rows.append(row)
                except (ValueError, TypeError):
                    pass  # Partial final line after interruption is expected.
    except OSError:
        pass
    return rows


def at(row, *keys):
    value = row
    for key in keys:
        value = value.get(key) if isinstance(value, dict) else None
    return value


def average_peak(rows, keys, scale=1):
    values = [(at(row, *keys), row.get("interval_seconds", 0)) for row in rows]
    valid = [(value, weight) for value, weight in values if isinstance(value, (float, int)) and math.isfinite(value)
             and isinstance(weight, (float, int)) and math.isfinite(weight) and weight > 0]
    if not valid:
        return "unavailable"
    peak = max(value for value, _ in values if isinstance(value, (float, int)) and math.isfinite(value))
    return f"{sum(value * weight for value, weight in valid) / sum(weight for _, weight in valid) / scale:.2f} / {peak / scale:.2f}"


def total(rows, *keys):
    values = [at(row, *keys) for row in rows]
    valid = [value for value in values if isinstance(value, (float, int)) and math.isfinite(value)]
    return sum(valid) if valid else None


def fmt(value):
    return f"{value:.2f}" if value is not None else "unavailable"


def summary(rows, duration, exit_code, unavailable, interrupted=None):
    host = [row for row in rows if row.get("kind") == "host"]
    sql = [row for row in rows if row.get("kind") == "sql_container"]
    coverage = sum(row.get("interval_seconds", 0) or 0 for row in host)
    lines = ["## .NET/SQL CI performance", "", f"Test duration: {duration:.2f} s; command exit: {exit_code if exit_code is not None else 'still running/unknown'}; host intervals: {coverage:.2f} s ({len(host)} snapshots).",
             f"Interrupted by signal: {interrupted}." if interrupted else "", "",
             "Time-weighted average / sampled peak (missing/reset intervals omitted):", "",
             "| Metric | Average / peak |", "| --- | ---: |"]
    for label, keys, scale in (("Guest CPU busy (%)", ("cpu", "busy_percent"), 1),
                                ("Guest CPU idle (%)", ("cpu", "idle_percent"), 1),
                                ("Guest CPU I/O wait (%)", ("cpu", "iowait_percent"), 1),
                                ("Guest CPU steal (%)", ("cpu", "steal_percent"), 1),
                                ("Guest memory used: total - available (MiB)", ("memory", "used_bytes"), 1024**2),
                                ("Guest swap used (MiB)", ("memory", "swap_used_bytes"), 1024**2)):
        lines.append(f"| {label} | {average_peak(host, keys, scale)} |")
    swap_in, swap_out, major = (total(host, "memory", key) for key in ("swap_in_bytes", "swap_out_bytes", "major_faults"))
    lines += ["", f"Swap in/out: {fmt(swap_in / 1024**2 if swap_in is not None else None)} / {fmt(swap_out / 1024**2 if swap_out is not None else None)} MiB; major faults: {fmt(major)}.", ""]
    for device in sorted({key for row in host for key in row.get("disks", {})}):
        disk_rows = [{"interval_seconds": row.get("interval_seconds", 0), **row["disks"][device]} for row in host if device in row.get("disks", {})]
        lines.append(f"Disk `{device}` (separate guest device):")
        for direction in ("read", "write"):
            count, elapsed_ms = total(disk_rows, direction + "_operations"), total(disk_rows, direction + "_time_ms")
            await_ms = elapsed_ms / count if count and elapsed_ms is not None else None
            peaks = [row.get(direction + "_await_ms") for row in disk_rows if row.get(direction + "_await_ms") is not None]
            lines.append(f"- {direction}: {average_peak(disk_rows, (direction + '_bytes_per_second',), 1024**2)} MiB/s average/peak; {average_peak(disk_rows, (direction + '_ops_per_second',))} ops/s average/peak; await {fmt(await_ms)} ms (operation-weighted), {fmt(max(peaks) if peaks else None)} ms peak interval.")
        lines.append(f"- busy: {average_peak(disk_rows, ('busy_percent',))} % average/peak; queue depth: {average_peak(disk_rows, ('average_queue_depth',))} average/peak.")
        lines.append("")
    for resource in ("cpu", "memory", "io"):
        for scope in ("some", "full"):
            if any(at(row, "pressure", resource, scope) for row in host):
                lines.append(f"PSI {resource}/{scope} stall: {average_peak(host, ('pressure', resource, scope, 'stall_percent'))} % average/peak.")
    lines.append("")
    for level in sorted({group["level"] for row in host for group in row.get("cgroups", [])}):
        groups = [{"interval_seconds": row.get("interval_seconds", 0), **group} for row in host for group in row.get("cgroups", []) if group["level"] == level]
        periods, throttled, usec = (total(groups, key) for key in ("nr_periods_delta", "nr_throttled_delta", "throttled_usec_delta"))
        events = {key: total(groups, "memory_events_delta", key) for key in ("high", "max", "oom", "oom_kill")}
        ratio = 100 * throttled / periods if periods and throttled is not None else None
        latest = groups[-1]
        quota = fmt(latest["cpu_limit_cores"]) + " cores" if latest.get("cpu_limit_cores") is not None else latest.get("cpu_limit_status", "unavailable")
        lines.append(f"Cgroup level {level} (0 = runner leaf): CPU quota {quota}; CPU {average_peak(groups, ('cpu_percent',))}% average/peak; throttled periods {fmt(throttled)} / {fmt(periods)} ({fmt(ratio)}%); throttled time {fmt(usec / 1e6 if usec is not None else None)} s; memory current {average_peak(groups, ('memory_current_bytes',), 1024**2)} MiB average/peak; memory event deltas {json.dumps(events, separators=(',', ':'))}.")
    lines.append("")
    for name in ("dotnet", "sqlservr", "testhost"):
        lines.append(f"Process group {name}: CPU {average_peak(host, ('processes', name, 'cpu_percent'))} % average/peak; RSS {average_peak(host, ('processes', name, 'rss_bytes'), 1024**2)} MiB average/peak.")
    cpus = [at(row, "cpu_percent") for row in sql if at(row, "cpu_percent") is not None]
    memories = [at(row, "memory_usage_bytes") for row in sql if at(row, "memory_usage_bytes") is not None]
    lines += ["", f"SQL container: {len(sql)} observations; CPU sampled mean/peak {fmt(sum(cpus) / len(cpus) if cpus else None)} / {fmt(max(cpus) if cpus else None)} %; memory peak {fmt(max(memories) / 1024**2 if memories else None)} MiB.",
              f"SQL block I/O during observed intervals: read {fmt(total(sql, 'block_read_bytes_delta'))} bytes; write {fmt(total(sql, 'block_write_bytes_delta'))} bytes; observed states: {', '.join(sorted({row.get('state', 'unknown') for row in sql})) or 'unavailable'}.",
              "", "Unavailable metrics:"]
    lines += ["- " + item for item in sorted(unavailable)] or ["- None reported."]
    lines += ["", "Interpretation limits:"] + ["- " + item for item in LIMITATIONS]
    return "\n".join(lines) + "\n"


class Monitor:
    def __init__(self, output, interval, docker_interval, cancelled):
        self.output, self.interval, self.docker_interval = Path(output), interval, docker_interval
        self.stop = threading.Event()
        self.cancelled = cancelled
        self.unavailable = set()
        self.metrics = LinuxMetrics(Path.cwd(), self.unavailable)
        self.environment = self.metrics.environment()
        self.docker = DockerMetrics(cancelled, self.unavailable)
        self.output.mkdir(parents=True, exist_ok=True)
        self.started = time.monotonic()
        self.previous = None
        self.last_host, self.last_sql = {}, {}
        self.stream = (self.output / "samples.jsonl").open("w", encoding="utf-8", buffering=1)
        self.write_environment()
        self.take_host()
        self.write_summary(None, None, False)
        self.thread = threading.Thread(target=self.collect, name="ci-performance", daemon=True)
        self.thread.start()

    def write_environment(self):
        self.environment["unavailable_metrics"] = sorted(self.unavailable)
        path = self.output / "environment.json"
        temporary = path.with_suffix(".tmp")
        temporary.write_text(json.dumps(self.environment, indent=2) + "\n", encoding="utf-8")
        temporary.replace(path)

    def write(self, row):
        row["elapsed_seconds"] = time.monotonic() - self.started
        self.stream.write(json.dumps(row, separators=(",", ":"), allow_nan=False) + "\n")
        self.stream.flush()

    def take_host(self):
        current = self.metrics.snapshot()
        self.last_host = self.metrics.interval(self.previous, current)
        self.previous = current
        self.write(self.last_host)

    def collect(self):
        next_host = self.started + self.interval
        next_docker = self.started
        next_health = self.started + 60
        try:
            version = optional_command(["docker", "version", "--format", "{{.Client.Version}}|{{if .Server}}{{.Server.Version}}{{end}}"], self.cancelled)
            self.environment["docker_version"] = version if version and re.fullmatch(r"[0-9a-zA-Z.+|_-]{1,100}", version) else None
            if self.environment["docker_version"] is None:
                self.unavailable.add("Docker client/server version unavailable")
            try:
                self.environment["sql_container_limits"] = self.docker.inspect()
            except (OSError, ValueError, TypeError):
                self.unavailable.add("SQL container limits unavailable or malformed")
            self.write_environment()
            while not self.stop.is_set():
                now = time.monotonic()
                if now >= next_host:
                    self.take_host()
                    next_host = time.monotonic() + self.interval
                if now >= next_docker and not self.cancelled.is_set():
                    try:
                        self.last_sql = self.docker.sample()
                        self.write(self.last_sql)
                    except (OSError, ValueError, TypeError):
                        self.unavailable.add("SQL container sample unavailable or malformed")
                    next_docker = time.monotonic() + self.docker_interval
                if now >= next_health:
                    cpu = at(self.last_host, "cpu", "busy_percent")
                    wait = at(self.last_host, "cpu", "iowait_percent")
                    steal = at(self.last_host, "cpu", "steal_percent")
                    memory = at(self.last_host, "memory", "used_bytes")
                    pressure = at(self.last_host, "pressure", "io", "some", "stall_percent")
                    sql_memory = self.last_sql.get("memory_usage_bytes")
                    print(f"[ci-performance] {time.monotonic() - self.started:.0f}s CPU={fmt(cpu)}% iowait={fmt(wait)}% steal={fmt(steal)}% memory={fmt(memory / 1024**2 if memory is not None else None)}MiB IO_PSI={fmt(pressure)}% SQL_CPU={fmt(self.last_sql.get('cpu_percent'))}% SQL_memory={fmt(sql_memory / 1024**2 if sql_memory is not None else None)}MiB", flush=True)
                    self.write_summary(None, None, False)
                    next_health = time.monotonic() + 60
                self.stop.wait(min(0.5, max(0.01, next_host - time.monotonic())))
            self.take_host()
        except Exception as error:
            self.unavailable.add("collector stopped: " + type(error).__name__)
            warn("collector stopped (" + type(error).__name__ + "); command continues.")
        finally:
            self.stream.close()

    def write_summary(self, exit_code, interrupted, publish, duration=None):
        text = summary(read_samples(self.output / "samples.jsonl"), duration if duration is not None else time.monotonic() - self.started,
                       exit_code, self.unavailable, interrupted)
        (self.output / "summary.md").write_text(text, encoding="utf-8")
        if publish:
            # The artifact/step summary contains the full report; keep the log short.
            compact = [line for line in text.splitlines() if line.startswith(("Test duration:", "| Guest CPU", "| Guest memory", "Swap in/out:", "SQL container:"))]
            print("[ci-performance] Final summary (full report: .artifacts/ci-performance/summary.md)\n" + "\n".join(compact), flush=True)
            destination = os.environ.get("GITHUB_STEP_SUMMARY")
            if destination:
                with open(destination, "a", encoding="utf-8") as stream:
                    stream.write(text)

    def finish(self, exit_code, interrupted, duration):
        self.stop.set()
        self.cancelled.set()  # Interrupt an optional Docker call promptly.
        self.thread.join(timeout=5)
        if self.thread.is_alive():
            warn("collector shutdown exceeded 5 s; partial evidence retained.")
            return
        self.write_environment()
        self.write_summary(exit_code, interrupted, True, duration)


def run(command, output, interval=15, docker_interval=60):
    child, monitor = None, None
    cancelled = threading.Event()
    received = []

    def forward(signum, _frame):
        received.append((signum, time.monotonic()))
        cancelled.set()
        if child is not None:
            kill_group(child, signum)

    handlers = {signum: signal.signal(signum, forward) for signum in (signal.SIGINT, signal.SIGTERM)}
    code, duration, command_started = 1, 0, None
    try:
        try:
            monitor = Monitor(output, interval, docker_interval, cancelled)
        except Exception as error:
            warn("collector unavailable (" + type(error).__name__ + "); command will still run.")
        if received:
            code = 128 + received[0][0]
        else:
            try:
                # Streams and stdin are inherited directly; no tee, buffering or shell reparsing.
                command_started = time.monotonic()
                child = subprocess.Popen(command, start_new_session=os.name == "posix")
            except OSError as error:
                print("Command could not start (" + type(error).__name__ + ").", file=sys.stderr, flush=True)
                code = 127 if isinstance(error, FileNotFoundError) else 126
            if child is not None:
                if received:
                    kill_group(child, received[0][0])
                while True:
                    try:
                        code = child.wait(timeout=0.2)
                        break
                    except subprocess.TimeoutExpired:
                        if received and (len(received) > 1 or time.monotonic() - received[0][1] >= 5):
                            kill_group(child, signal.SIGKILL if os.name == "posix" else signal.SIGTERM)
                code = 128 - code if code < 0 else code
    finally:
        if command_started is not None:
            duration = time.monotonic() - command_started
        if child is not None and received and os.name == "posix":
            kill_group(child, signal.SIGKILL)
        if monitor is not None:
            try:
                monitor.finish(code if code or not received else 128 + received[0][0], received[0][0] if received else None, duration)
            except Exception as error:
                warn("finalization unavailable (" + type(error).__name__ + "); command exit is preserved.")
        for signum, handler in handlers.items():
            signal.signal(signum, handler)
    return code if code or not received else 128 + received[0][0]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", default=".artifacts/ci-performance")
    parser.add_argument("--interval", type=float, default=15)
    parser.add_argument("--docker-interval", type=float, default=60)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        parser.error("a command after -- is required")
    if not math.isfinite(args.interval) or args.interval <= 0 or not math.isfinite(args.docker_interval) or args.docker_interval <= 0:
        parser.error("sample intervals must be positive finite seconds")
    return run(command, args.output, args.interval, args.docker_interval)


if __name__ == "__main__":
    sys.exit(main())
