"""Content and observed toolchain identity. No secrets or machine names are inputs."""

import hashlib
import fnmatch
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import urllib.request

import selection as test_selection


ROOT = Path(__file__).resolve().parents[2]
SUITES = (*test_selection.NATIVE_SUITES, "frontend")
SQL_TAG = "mcr.microsoft.com/mssql/server:2022-latest"
CONFIG = ROOT / "scripts/ci/test-reuse.json"
SOURCE_ALGORITHM = "suite-inputs-v1"
EXECUTORS = {
    "frontend": {"id": "build-test-package", "name": "Frontend, CI checks and package"},
    "dotnet_a": {"id": "dotnet-qualification-a", "name": "Non-browser .NET/SQL qualification A"},
    "dotnet_b": {"id": "dotnet-qualification-b", "name": "Non-browser .NET/SQL qualification B"},
    "browser_a": {"id": "browser-qualification-a", "name": "Browser/SQL qualification A"},
    "browser_b": {"id": "browser-qualification-b", "name": "Browser/SQL qualification B"},
}


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
    if (type(config["schema"]) is not int or config["schema"] != 7
            or not 1 <= config["lifetime_days"] <= 14 or not config["environment_epoch"]):
        raise ValueError("Invalid evidence lifetime/environment epoch")
    if config.get("executors") != EXECUTORS or "job" in config:
        raise ValueError("Every required suite must have its fixed owning execution job")
    if not isinstance(config.get("source_inputs"), list) or not config["source_inputs"]:
        raise ValueError("Missing source input policy")
    for rule in config["source_inputs"]:
        if not rule["reason"] or not rule["paths"] or not set(rule["suites"]) <= set(SUITES):
            raise ValueError("Unreviewed source input rule")
        for path in rule["paths"]:
            parts = path.rstrip("/").split("/")
            if (path.startswith("/") or "\\" in path or "**" in path or "?" in path or "[" in path
                    or any(part in ("", ".", "..") for part in parts)
                    or any("*" in part for part in parts[:-1])):
                raise ValueError("Input rules must be repository directories or single-level file patterns")
        if "extensions" in rule and (not rule["extensions"] or not all(p.endswith("/") for p in rule["paths"])
                or not all(re.fullmatch(r"\.[a-z0-9]+", extension) for extension in rule["extensions"])):
            raise ValueError("File-type rules require an explicit directory scope")
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


def full_ci_execution_contract(workflow):
    """Bind every test executor, reporting gate and deployment authorization edit."""
    workflow = workflow.replace("\r\n", "\n")
    if not all(len(re.findall(r"(?m)^  " + name + r":[ \\t]*$", workflow)) == 1
               for name in (*(owner["id"] for owner in EXECUTORS.values()), "validation")):
        raise ValueError("Missing or ambiguous required execution jobs")
    return digest(workflow)


def source_suites(path, config):
    # First matching ownership rule wins. A directory includes its descendants;
    # filename globs match only that directory level, never across separators.
    for rule in config["source_inputs"]:
        if rule.get("extensions") and Path(path).suffix not in rule["extensions"]:
            continue
        for pattern in rule["paths"]:
            if (path.startswith(pattern) if pattern.endswith("/") else
                    path.count("/") == pattern.count("/") and fnmatch.fnmatchcase(path, pattern)):
                return rule["suites"]
    # New/unclassified roots cannot silently disappear from qualification.
    return SUITES


def source_fingerprint(entries, suite, config, execution_contract=None):
    files = sorted(entry for entry in entries if suite in source_suites(entry[0], config))
    # Optional contract argument is for isolated pure-entry tests only.
    return (digest({"files": files, "execution_contract": execution_contract})
            if execution_contract is not None else digest(files))


def selections(root=ROOT, verify=False):
    return test_selection.load(root, verify=verify)["suites"]


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
    paths = ["src/Asap.Web", "tests/Asap.Tests",
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


def environment(config, root=ROOT, observe=True):
    # Only policy and resolved dependency contents authorize reuse. Observations
    # of an ephemeral VM are useful provenance, never a prerequisite for a HIT.
    def observed(arguments):
        if not observe:
            return None
        try:
            return command(arguments, root)
        except Exception:
            return None

    common = {
        "epoch": config["environment_epoch"],
        "os": platform.system(), "arch": platform.machine(), "kernel": platform.release(),
        "runner_os": os.environ.get("RUNNER_OS", ""), "runner_arch": os.environ.get("RUNNER_ARCH", ""),
        "python": platform.python_version(),
        "dotnet": observed(["dotnet", "--version"]),
        "runtimes": [" ".join(line.split()[:2]) for line in (observed(["dotnet", "--list-runtimes"]) or "").splitlines()],
        "nuget": nuget_identity(root),
        "node": observed(["node", "--version"]), "npm": observed(["npm", "--version"]),
        "pwsh": observed(["pwsh", "-NoLogo", "-NoProfile", "-Command", "$PSVersionTable.PSVersion.ToString()"]),
        "locale": {k: os.environ.get(k, "") for k in ("TZ", "LANG", "LC_ALL", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT")},
    }
    if platform.system() == "Linux":
        packages = (observed(["dpkg-query", "-W", "-f=${binary:Package}=${Version}\\n"]) or "").splitlines()
        common["native_packages"] = digest(sorted(packages)) if packages else None
    if Path("/etc/os-release").exists():
        common["distribution"] = {k: v.strip('"') for line in Path("/etc/os-release").read_text().splitlines()
                                  if "=" in line for k, v in [line.split("=", 1)] if k in ("ID", "VERSION_ID")}
    result = {suite: dict(common) for suite in SUITES}
    problems = {}
    # The fresh-only SQL bootstrap resolves and exports the immutable image.
    # Neither a registry request nor a running Docker daemon is needed for reuse.
    image = os.environ.get("ASAP_CI_SQL_IMAGE", SQL_TAG)
    docker = observed(["docker", "version", "--format", "{{.Server.Version}}"])
    for suite in test_selection.NATIVE_SUITES:
        result[suite].update(sql_image=image, docker=docker)
    try:
        browsers = {
            "version": json.loads((root / "node_modules/playwright/package.json").read_text(encoding="utf-8"))["version"],
            "browsers": [b for b in json.loads((root / "node_modules/playwright-core/browsers.json").read_text(encoding="utf-8"))["browsers"]
                         if b["name"] in ("chromium", "chromium-headless-shell")],
        }
        if not browsers["version"] or not browsers["browsers"]:
            raise ValueError("Browser identity missing")
        for suite in ("browser_a", "browser_b"):
            result[suite]["playwright"] = browsers
    except Exception:
        problems.update({s: "Playwright/browser identity unavailable" for s in ("browser_a", "browser_b")})
    if any(os.environ.get(k) for k in ("ASAP_TEST_CHROMIUM_EXECUTABLE_PATH", "PLAYWRIGHT_BROWSERS_PATH")):
        problems.update({s: "Custom browser installation is not fingerprinted" for s in ("browser_a", "browser_b")})
    if os.environ.get("NODE_OPTIONS"):
        problems.update({s: "Ambient Node options are not fingerprinted" for s in SUITES})
    if any(os.environ.get(k) for k in ("DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE")):
        # Frontend contracts also launch PowerShell, which runs on .NET.
        problems.update({s: "Ambient .NET extensions are not fingerprinted" for s in SUITES})
    if common["runner_os"] != "Linux" or common["runner_arch"] != "X64":
        problems.update({suite: "Unrecognized runner environment" for suite in SUITES})
    return result, problems, image


def environment_fingerprint(inputs):
    # Repository policy and resolved test dependencies remain effective inputs.
    # VM/kernel/native packages, installed tools and the mutable SQL image are
    # throwaway execution context, not a reason to repeat matching test evidence.
    if not inputs.get("epoch") or not inputs.get("nuget"):
        raise ValueError("Missing test dependency identity")
    return digest({key: inputs[key] for key in ("epoch", "nuget", "playwright") if key in inputs})


def calculate(expected, config, root=ROOT):
    commit, tree = checkout(expected, root)
    entries = tracked_entries(root)
    selected = selections(root)
    environments, problems, image = environment(config, root)
    contract = full_ci_execution_contract((root / config["workflow"]).read_text(encoding="utf-8"))
    suites = {}
    for suite in SUITES:
        selection = selected[suite]
        inputs = {"schema": config["schema"], "suite": suite,
                  "source": source_fingerprint(entries, suite, config, contract),
                  "environment": environment_fingerprint(environments[suite]), "selection": selection}
        suites[suite] = {**inputs, "fingerprint": digest(inputs), "environment_inputs": environments[suite],
                         "unavailable": problems.get(suite)}
    return {"commit": commit, "tree": tree, "sql_image": image,
            "full_ci_execution_contract": contract, "suites": suites}
