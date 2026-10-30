"""Content and observed toolchain identity. No secrets or machine names are inputs."""

import hashlib
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import urllib.request


ROOT = Path(__file__).resolve().parents[2]
SUITES = ("dotnet", "browser", "frontend")
SQL_TAG = "mcr.microsoft.com/mssql/server:2022-latest"
CONFIG = ROOT / "scripts/ci/test-reuse.json"


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode()


def digest(value):
    return hashlib.sha256(canonical(value)).hexdigest()


def command(arguments, root=ROOT):
    if os.name == "nt" and arguments[0] == "npm":
        arguments = ["npm.cmd", *arguments[1:]]
    return subprocess.check_output(arguments, cwd=root, text=True, stderr=subprocess.PIPE,
                                   timeout=90).strip()


def configuration():
    config = json.loads(CONFIG.read_text(encoding="utf-8"))
    if (type(config["schema"]) is not int or config["schema"] < 1
            or not 1 <= config["lifetime_days"] <= 14 or not config["environment_epoch"]):
        raise ValueError("Invalid evidence lifetime/environment epoch")
    for rule in config["exclusions"]:
        if not rule["reason"] or not set(rule["suites"]) <= set(SUITES):
            raise ValueError("Unreviewed dependency exclusion")
        if any("*" in p or p.startswith("/") or ".." in p.split("/") for p in rule["paths"]):
            raise ValueError("Exclusions must be exact repository paths")
    return config


def checkout(expected, root=ROOT):
    commit = command(["git", "rev-parse", "HEAD"], root)
    if not re.fullmatch(r"[0-9a-f]{40}", expected) or commit != expected:
        raise ValueError("Checkout does not match the exact workflow SHA")
    if command(["git", "status", "--porcelain", "--untracked-files=no"], root):
        raise ValueError("Tracked checkout contents have changed")
    return commit, command(["git", "rev-parse", "HEAD^{tree}"], root)


def tracked_entries(root=ROOT):
    raw = subprocess.check_output(["git", "ls-tree", "-r", "-z", "HEAD"], cwd=root, timeout=30)
    entries = []
    for record in raw.split(b"\0"):
        if record:
            metadata, name = record.split(b"\t", 1)
            mode, kind, sha = metadata.decode("ascii").split()
            entries.append([name.decode("utf-8"), mode, kind, sha])
    return entries


def source_fingerprint(entries, suite, config):
    # Inclusion is the default. Unknown/new files, docs and shared review code stay included.
    excluded = {p for rule in config["exclusions"] if suite in rule["suites"] for p in rule["paths"]}
    return digest(sorted(entry for entry in entries if entry[0] not in excluded))


def selections(root=ROOT):
    workflow = (root / ".github/workflows/dotnet.yml").read_text(encoding="utf-8")
    browser = (root / "tests/browser/run.cjs").read_text(encoding="utf-8")
    minimum = int(re.search(r"--minimum-expected-tests (\d+)", workflow)[1])
    non_browser = re.search(r"--filter\s*'([^']+)'", workflow)[1]
    browser_filter = "|".join(re.findall(r"'FullyQualifiedName~([^']+)'", browser))
    browser_filter = "|".join("FullyQualifiedName~" + term for term in browser_filter.split("|"))
    browser_minimum = int(re.search(r"'--minimum-expected-tests',\s*'(\d+)'", browser)[1])
    files = sorted(p.name for p in (root / "tests").glob("*.test.js"))
    return {
        "dotnet": {"filter": non_browser, "minimum": minimum},
        "browser": {"filter": browser_filter, "minimum": browser_minimum},
        "frontend": {"filter": "npm test (all root *.test.js files)", "minimum": len(files), "files": files},
    }


def discover(selection, root=ROOT):
    output = command(["dotnet", "test", "--project", "tests/Asap.Tests/Asap.Tests.csproj",
                      "--configuration", "Release", "--no-build", "--no-restore", "--list-tests",
                      "--filter", selection["filter"]], root)
    counts = re.findall(r"^Discovered (\d+) tests\.$", output, re.MULTILINE)
    if len(counts) != 1 or int(counts[0]) < selection["minimum"]:
        raise ValueError("Test discovery did not meet the retained minimum")
    return int(counts[0])


def sql_image():
    # Resolve the mutable tag without starting/downloading SQL. Execute the resolved digest.
    request = urllib.request.Request(
        "https://mcr.microsoft.com/v2/mssql/server/manifests/2022-latest",
        headers={"Accept": "application/vnd.docker.distribution.manifest.v2+json, "
                           "application/vnd.oci.image.index.v1+json, "
                           "application/vnd.docker.distribution.manifest.list.v2+json"})
    with urllib.request.urlopen(request, timeout=15) as response:
        body = response.read(1024 * 1024 + 1)
        identity = response.headers.get("Docker-Content-Digest", "")
    if len(body) > 1024 * 1024 or identity != "sha256:" + hashlib.sha256(body).hexdigest():
        raise ValueError("SQL registry did not provide a verifiable manifest digest")
    manifest = json.loads(body)
    if "manifests" in manifest:
        matches = [m for m in manifest["manifests"]
                   if m.get("platform", {}).get("os") == "linux"
                   and m.get("platform", {}).get("architecture") == "amd64"]
        if len(matches) != 1:
            raise ValueError("SQL Linux x64 manifest is ambiguous")
        identity = matches[0]["digest"]
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", identity):
        raise ValueError("SQL image digest is unavailable")
    return "mcr.microsoft.com/mssql/server@" + identity


def nuget_identity(root=ROOT):
    paths = ["src/Asap.Web", "src/Asap.Migration", "tests/Asap.Tests",
             "database/Asap.Database", "dev/review/src/Asap.ReviewProviders"]
    resolved = {}
    for path in paths:
        assets = json.loads((root / path / "obj/project.assets.json").read_text(encoding="utf-8-sig"))
        libraries = {}
        for name, library in assets["libraries"].items():
            if library["type"] == "package":
                if not library.get("sha512"):
                    raise ValueError("Missing NuGet package content identity")
                libraries[name] = library["sha512"]
        project = next((root / path).glob("*.*proj"))
        sdk = re.search(r'<Project Sdk="([^"/]+)/([^"/]+)"', project.read_text(encoding="utf-8"))
        if sdk:
            name, version = sdk.groups()
            hashes = set()
            for folder in assets["packageFolders"]:
                package = Path(folder) / name.lower() / version.lower() / f"{name.lower()}.{version.lower()}.nupkg.sha512"
                if package.exists():
                    hashes.add(package.read_text(encoding="utf-8").strip())
            if len(hashes) != 1 or not all(re.fullmatch(r"[A-Za-z0-9+/=]{40,}", h) for h in hashes):
                raise ValueError("Missing or ambiguous NuGet build SDK content identity")
            libraries[f"build-sdk:{name}/{version}"] = hashes.pop()
        if not libraries:
            raise ValueError("Missing resolved NuGet/build SDK dependencies")
        resolved[path] = libraries
    return digest(resolved)


def environment(config, root=ROOT):
    # Epoch covers VM image, native libraries, runner configuration and test policy.
    # Observe actual versions too: setup-dotnet rollForward/setup-node major can drift.
    common = {
        "epoch": config["environment_epoch"],
        "os": platform.system(), "arch": platform.machine(), "kernel": platform.release(),
        "runner_os": os.environ.get("RUNNER_OS", ""), "runner_arch": os.environ.get("RUNNER_ARCH", ""),
        "python": platform.python_version(),
        "dotnet": command(["dotnet", "--version"], root),
        "runtimes": [" ".join(line.split()[:2]) for line in command(["dotnet", "--list-runtimes"], root).splitlines()],
        "nuget": nuget_identity(root),
        "node": command(["node", "--version"], root), "npm": command(["npm", "--version"], root),
        "pwsh": command(["pwsh", "-NoLogo", "-NoProfile", "-Command", "$PSVersionTable.PSVersion.ToString()"], root),
        "locale": {k: os.environ.get(k, "") for k in ("TZ", "LANG", "LC_ALL", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT")},
    }
    if Path("/etc/os-release").exists():
        common["distribution"] = {k: v.strip('"') for line in Path("/etc/os-release").read_text().splitlines()
                                  if "=" in line for k, v in [line.split("=", 1)] if k in ("ID", "VERSION_ID")}
    result = {suite: dict(common) for suite in SUITES}
    problems = {}
    image = SQL_TAG
    try:
        image = sql_image()
        docker = command(["docker", "version", "--format", "{{.Server.Version}}"], root)
        if not docker:
            raise ValueError("Docker server version is missing")
        for suite in ("dotnet", "browser"):
            result[suite].update(sql_image=image, docker=docker)
    except Exception:
        for suite in ("dotnet", "browser"):
            problems[suite] = "SQL image/toolchain identity unavailable"
    try:
        browsers = {
            "version": json.loads((root / "node_modules/playwright/package.json").read_text(encoding="utf-8"))["version"],
            "browsers": [b for b in json.loads((root / "node_modules/playwright-core/browsers.json").read_text(encoding="utf-8"))["browsers"]
                         if b["name"] in ("chromium", "chromium-headless-shell")],
        }
        if not browsers["version"] or not browsers["browsers"]:
            raise ValueError("Browser identity missing")
        result["browser"]["playwright"] = browsers
    except Exception:
        problems["browser"] = "Playwright/browser identity unavailable"
    if any(os.environ.get(k) for k in ("ASAP_TEST_CHROMIUM_EXECUTABLE_PATH", "PLAYWRIGHT_BROWSERS_PATH")):
        problems["browser"] = "Custom browser installation is not fingerprinted"
    if os.environ.get("NODE_OPTIONS"):
        problems.update({s: "Ambient Node options are not fingerprinted" for s in SUITES})
    if any(os.environ.get(k) for k in ("DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE")):
        # Frontend contracts also launch PowerShell, which runs on .NET.
        problems.update({s: "Ambient .NET extensions are not fingerprinted" for s in SUITES})
    if common["runner_os"] != "Linux" or common["runner_arch"] != "X64":
        problems.update({suite: "Unrecognized runner environment" for suite in SUITES})
    return result, problems, image


def calculate(expected, config, root=ROOT):
    commit, tree = checkout(expected, root)
    entries = tracked_entries(root)
    selected = selections(root)
    environments, problems, image = environment(config, root)
    suites = {}
    for suite in SUITES:
        selection = selected[suite]
        selection["expected"] = len(selection["files"]) if suite == "frontend" else discover(selection, root)
        inputs = {"schema": config["schema"], "suite": suite,
                  "source": source_fingerprint(entries, suite, config),
                  "environment": digest(environments[suite]), "selection": selection}
        suites[suite] = {**inputs, "fingerprint": digest(inputs), "environment_inputs": environments[suite],
                         "unavailable": problems.get(suite)}
    return {"commit": commit, "tree": tree, "sql_image": image, "suites": suites}
