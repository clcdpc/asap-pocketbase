"""Content and observed toolchain identity. No secrets or machine names are inputs."""

import hashlib
import fnmatch
import json
import os
from pathlib import Path
import platform
import re
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request

import selection as test_selection


ROOT = Path(__file__).resolve().parents[2]
SUITES = ("dotnet", "browser", "frontend")
SQL_TAG = "mcr.microsoft.com/mssql/server:2022-latest"
SQL_MANIFEST_ACCEPT = ", ".join((
    "application/vnd.oci.image.manifest.v1+json",
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
))
MAX_MANIFEST_BYTES = 1024 * 1024
SQL_REGISTRY_MAX_ATTEMPTS = 3
SQL_REGISTRY_RETRY_BACKOFF_SECONDS = (0.25, 0.5)
CONFIG = ROOT / "scripts/ci/test-reuse.json"
SOURCE_ALGORITHM = "suite-inputs-v1"
EXECUTORS = {
    "frontend": {"id": "build-test-package", "name": "Frontend, CI checks and package"},
    "dotnet": {"id": "dotnet-qualification", "name": "Non-browser .NET/SQL qualification"},
    "browser": {"id": "browser-qualification", "name": "Browser/SQL qualification"},
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
    if (type(config["schema"]) is not int or config["schema"] != 6
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
    repository, tag, manifests_url = sql_image_source()
    manifest, identity = sql_manifest(tag, manifests_url)
    if "manifests" in manifest:
        descriptors = manifest["manifests"]
        if not isinstance(descriptors, list):
            raise ValueError("SQL image index manifests are malformed")
        matches = [descriptor for descriptor in descriptors
                   if isinstance(descriptor, dict)
                   and isinstance(descriptor.get("platform"), dict)
                   and descriptor["platform"].get("os") == "linux"
                   and descriptor["platform"].get("architecture") == "amd64"]
        if len(matches) != 1:
            raise ValueError("SQL Linux x64 manifest is ambiguous")
        identity = matches[0].get("digest", "")
        if not isinstance(identity, str) or not re.fullmatch(r"sha256:[0-9a-f]{64}", identity):
            raise ValueError("SQL image digest is unavailable")
    if not isinstance(identity, str) or not re.fullmatch(r"sha256:[0-9a-f]{64}", identity):
        raise ValueError("SQL image digest is unavailable")
    manifest, identity = sql_manifest(identity, manifests_url, expected_digest=identity)
    if "manifests" in manifest:
        raise ValueError("SQL Linux x64 image descriptor did not resolve to a single manifest")
    return repository + "@" + identity


def sql_image_source():
    repository, separator, tag = SQL_TAG.rpartition(":")
    if not separator or not tag or "@" in tag:
        raise ValueError("Configured SQL image must include a repository and tag")
    registry, separator, image = repository.partition("/")
    if not separator or not registry or not image:
        raise ValueError("Configured SQL image must include a registry and repository")
    return repository, tag, f"https://{registry}/v2/{image}/manifests/"


def sql_registry_failure_is_transient(error):
    if isinstance(error, urllib.error.HTTPError):
        return error.code in (408, 429) or 500 <= error.code <= 599
    reason = error.reason if isinstance(error, urllib.error.URLError) else error
    return isinstance(reason, (TimeoutError, socket.timeout, ConnectionError))


def sql_registry_failure_detail(error):
    if isinstance(error, urllib.error.HTTPError):
        return f"HTTP {error.code} {error.reason}"
    reason = error.reason if isinstance(error, urllib.error.URLError) else error
    return str(reason) or "connection timeout"


def sql_registry_response(request):
    for attempt in range(SQL_REGISTRY_MAX_ATTEMPTS):
        try:
            with urllib.request.urlopen(request, timeout=15) as response:
                return response.read(MAX_MANIFEST_BYTES + 1), response.headers.get("Docker-Content-Digest", "")
        except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError, socket.timeout, ConnectionError) as error:
            if not sql_registry_failure_is_transient(error):
                raise
            if attempt + 1 == SQL_REGISTRY_MAX_ATTEMPTS:
                detail = sql_registry_failure_detail(error)
                raise RuntimeError(
                    f"SQL registry request failed after {SQL_REGISTRY_MAX_ATTEMPTS} attempts: {detail}"
                ) from error
            time.sleep(SQL_REGISTRY_RETRY_BACKOFF_SECONDS[attempt])


def sql_manifest(reference, manifests_url, expected_digest=None):
    request = urllib.request.Request(manifests_url + reference,
                                     headers={"Accept": SQL_MANIFEST_ACCEPT})
    body, identity = sql_registry_response(request)
    actual_digest = "sha256:" + hashlib.sha256(body).hexdigest()
    if (len(body) > MAX_MANIFEST_BYTES or not isinstance(identity, str)
            or not re.fullmatch(r"sha256:[0-9a-f]{64}", identity)
            or identity != actual_digest or (expected_digest is not None and identity != expected_digest)):
        raise ValueError("SQL registry did not provide a verifiable manifest digest")
    manifest = json.loads(body)
    if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 2:
        raise ValueError("SQL registry returned a malformed image manifest")
    return manifest, identity


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
    for suite in ("dotnet", "browser"):
        result[suite].update(sql_image=image, docker=docker)
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
