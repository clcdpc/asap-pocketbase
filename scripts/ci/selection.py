"""Authoritative, fail-closed test selection and complete native MTP discovery."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time


ROOT = Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / ".artifacts/test-selection"
CATEGORIES = {"migration": "HistoricalMigration", "upgrade": "HistoricalUpgrade", "browser": "Browser"}
PARTITIONS = ("core", "migration", "upgrade", "browser")
ALL = ["core", "migration", "upgrade"]
FILTERS = {key: f"TestCategory={value}" for key, value in CATEGORIES.items()}
FILTERS["core"] = "&".join(f"TestCategory!={value}" for value in CATEGORIES.values())
APPLICATION = "tests/Asap.Tests/bin/Release/net10.0/Asap.Tests.dll"


class SelectionError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise SelectionError(message)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode()


def digest(value):
    return hashlib.sha256(canonical(value)).hexdigest()


def git(*args, root=ROOT):
    return subprocess.check_output(["git", *args], cwd=root, timeout=30, stderr=subprocess.PIPE).decode("utf-8").strip()


def full(reason, fresh=True):
    return {"categories": list(ALL), "fresh": fresh, "reason": reason}


def paths_policy(paths):
    """Only audited unrelated paths can omit history; unknown dependencies run all."""
    require(isinstance(paths, list) and 0 < len(paths) <= 3000, "Missing or excessive changed paths")
    selected, reasons = {"core"}, []
    for path in paths:
        require(isinstance(path, str) and path and not path.startswith(("/", "\\"))
                and "\\" not in path and not any(p in ("", ".", "..") for p in path.split("/"))
                and not any(ord(c) < 32 for c in path), "Invalid changed path")
        if path.startswith((".github/", "scripts/ci/", "scripts/build/", "scripts/deployment/")) or path in (
                "tests/ci_workflow_contract.test.js", "tests/test_iis_deployment_contract.test.js",
                "tests/run_all.js", "tests/browser/run.cjs", "tests/Asap.Tests/TestCategories.cs") or path.endswith(
                    (".csproj", ".sqlproj", ".props", ".targets")):
            return full("Selection, workflow, build, deployment or safety infrastructure changed: " + path)
        if path.startswith(("database/", "src/Asap.Migration/", "tests/Asap.Tests/Migration/",
                            "tests/fixtures/migration/", "tests/Asap.Tests/Sql/")):
            return full("Historical migration/schema implementation, contract or fixture changed: " + path)
        if path.startswith(("src/Shared/", "src/Asap.Web/Infrastructure/")):
            selected.update(ALL)
            reasons.append("Shared SQL/configuration/deployment dependency: " + path)
        elif path.startswith(("src/Asap.Web/Features/Patron/PatronConfigurationService",
                              "src/Asap.Web/Features/Patron/PatronMessageDefaults",
                              "src/Asap.Web/Features/Staff/TitleRequestEndpoints")):
            selected.add("migration")
            reasons.append("Runtime configuration consumed by import oracles: " + path)
        elif path.startswith("src/Asap.Web/Features/Patron/PatronSessionService"):
            selected.add("upgrade")
            reasons.append("Session identity consumed by upgrade oracle: " + path)
        elif path.startswith(("src/Asap.Web/Frontend/", "src/Asap.Web/Features/",
                              "tests/Asap.Tests/Integration/", "tests/Asap.Tests/Unit/", "tests/browser/")):
            pass
        elif re.fullmatch(r"tests/[^/]+\.test\.js", path):
            pass
        else:
            return full("Unreviewed dependency path: " + path)
    return {"categories": [key for key in ALL if key in selected], "fresh": False,
            "reason": "; ".join(reasons) if reasons else "Only reviewed unrelated application/frontend paths changed"}


def sha(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{40}", value) and value != "0" * 40


def changed_paths(event_name, event, env, root=ROOT, current_pr=None):
    """Diff the exact tested integration tree, including base changes since fork."""
    tested = env.get("GITHUB_SHA")
    require(sha(tested) and git("rev-parse", "HEAD", root=root) == tested, "Tested commit identity is unavailable")
    if event_name == "pull_request":
        pr = event["pull_request"]
        current = current_pr or pr
        repository = env.get("GITHUB_REPOSITORY")
        for candidate in (pr, current):
            require(candidate["state"] == "open" and candidate["draft"] is False
                    and candidate["base"]["ref"] == "main"
                    and candidate["head"]["repo"]["full_name"] == repository
                    and candidate["base"]["repo"]["full_name"] == repository
                    and candidate["author_association"] in ("OWNER", "MEMBER", "COLLABORATOR"),
                    "PR change metadata is not trusted ready validation")
        base, head = pr["base"]["sha"], pr["head"]["sha"]
        require(sha(base) and sha(head) and current["base"]["sha"] == base and current["head"]["sha"] == head,
                "PR base/head changed after the event")
        parents = git("rev-list", "--parents", "-n", "1", tested, root=root).split()
        require(parents == [tested, base, head], "PR synthetic merge does not match event base/head")
        merge_bases = git("merge-base", "--all", base, head, root=root).splitlines()
        require(len(merge_bases) == 1 and sha(merge_bases[0]), "PR merge base is ambiguous")
        baseline = merge_bases[0]
    else:
        require(event_name == "push" and env.get("GITHUB_REF") == "refs/heads/main"
                and event.get("ref") == "refs/heads/main" and event.get("after") == tested
                and event.get("deleted") is False and event.get("forced") is False and sha(event.get("before")),
                "Unsupported or ambiguous push metadata")
        baseline = event["before"]
        git("merge-base", "--is-ancestor", baseline, tested, root=root)
    # Disable rename detection: both deleted and added dependency paths matter.
    raw = subprocess.check_output(["git", "diff", "--no-renames", "--name-only", "-z", baseline, tested],
                                  cwd=root, timeout=30, stderr=subprocess.PIPE)
    require(len(raw) <= 1024 * 1024 and raw.endswith(b"\0"), "Empty or incomplete Git diff")
    return raw[:-1].decode("utf-8").split("\0")


def decision(env=None, root=ROOT):
    env = os.environ if env is None else env
    event_name = env.get("GITHUB_EVENT_NAME")
    if (env.get("ASAP_FORCE_FULL_CI") == "true" or env.get("ASAP_DEPLOY_TEST_IIS") == "true"
            or env.get("GITHUB_REF_TYPE") == "tag" or event_name == "release"):
        return full("Explicit force-full, release tag or test-IIS qualification")
    if event_name == "workflow_dispatch":
        return full("Manual full qualification", fresh=False)
    try:
        require(event_name in ("pull_request", "push"), "Unsupported event")
        event_path = Path(env["GITHUB_EVENT_PATH"])
        require(event_path.stat().st_size <= 4 * 1024 * 1024, "Oversized event metadata")
        event = json.loads(event_path.read_bytes())
        current_pr = None
        if event_name == "pull_request":
            # Reuse the existing read-only, bounded client. Hosted trust preflight still runs first.
            import reuse
            require(bool(env.get("GH_TOKEN")), "Current PR change metadata is unavailable")
            number = event["pull_request"]["number"]
            require(type(number) is int and number > 0, "Invalid PR number")
            current_pr = reuse.GitHub(env["GITHUB_REPOSITORY"], env["GH_TOKEN"]).get(f"/pulls/{number}")
        paths = changed_paths(event_name, event, env, root, current_pr)
        return {**paths_policy(paths), "base_paths": paths}
    except Exception as error:
        reason = str(error) if isinstance(error, SelectionError) else type(error).__name__
        return full("Change metadata fallback: " + reason)


def discover(filter_expression=None, root=ROOT):
    # dotnet test --list-tests consumes the json argument in SDK 10.0.303.
    # Invoke the built MTP 2.4 application to obtain supported structured discovery.
    args = ["dotnet", APPLICATION, "--list-tests", "json", "--no-ansi"]
    if filter_expression:
        args.extend(["--filter", filter_expression])
    output = subprocess.check_output(args, cwd=root, text=True, timeout=90, stderr=subprocess.PIPE)
    raw = json.loads(output)
    require(raw.get("schemaVersion") == 1 and isinstance(raw.get("tests"), list), "Unsupported native discovery schema")
    tests = {}
    for test in raw["tests"]:
        uid, details = test["uid"], test["type"]
        require(isinstance(uid, str) and re.fullmatch(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", uid) and uid not in tests,
                "Missing or duplicate native test UID")
        method = ".".join(details[key] for key in ("namespace", "typeName", "methodName"))
        require(method.startswith("Asap.Tests.") and test.get("displayName"), "Native test method identity is missing")
        tests[uid] = {"method": method, "name": test["displayName"]}
    require(tests, "Native discovery returned no tests")
    return tests


def validate_partitions(catalog, partitions, reviewed):
    require(set(partitions) == set(PARTITIONS) and all(partitions.values()), "A required test partition is empty or missing")
    require(set(reviewed) == set(CATEGORIES.values()), "Reviewed category definitions changed")
    owned = set()
    for key in PARTITIONS:
        tests = partitions[key]
        require(not owned.intersection(tests), "Test category partitions overlap: " + key)
        require(all(uid in catalog and catalog[uid] == value for uid, value in tests.items()), "Partition discovery identity mismatch")
        owned.update(tests)
        if key != "core":
            methods = reviewed[CATEGORIES[key]]
            require(isinstance(methods, list) and methods and len(set(methods)) == len(methods), "Invalid reviewed category inventory")
            expected = {uid for uid, value in catalog.items() if value["method"] in methods}
            require({value["method"] for value in tests.values()} == set(methods) and set(tests) == expected,
                    "Category differs from reviewed method inventory: " + key + "; review method attributes and reviewed-test-categories.json")
    require(owned == set(catalog), "Partition union omits discovered tests")
    conditional_methods = {method for methods in reviewed.values() for method in methods}
    expected_core = {uid for uid, value in catalog.items() if value["method"] not in conditional_methods}
    require(set(partitions["core"]) == expected_core, "Unclassified/core tests were unexpectedly excluded")


def population(selected, root=ROOT):
    require(selected["categories"] and selected["categories"][0] == "core"
            and selected["categories"] == [key for key in ALL if key in selected["categories"]], "Invalid category selection")
    started = time.monotonic()
    catalog = discover(root=root)
    partitions = {key: discover(FILTERS[key], root) for key in PARTITIONS}
    reviewed = json.loads((root / "scripts/ci/reviewed-test-categories.json").read_bytes())
    validate_partitions(catalog, partitions, reviewed)
    selected_uids = sorted(uid for key in selected["categories"] for uid in partitions[key])
    omitted = [key for key in ALL if key not in selected["categories"]]
    expression = "&".join(["TestCategory!=Browser", *(f"TestCategory!={CATEGORIES[key]}" for key in omitted)])
    actual = discover(expression, root)
    require(set(actual) == set(selected_uids), "Effective filter unexpectedly narrows the selected population")
    catalog_identity = digest({"catalog": catalog, "partitions": partitions, "reviewed": reviewed})
    suites = {}
    for suite, tests, expression in (("dotnet", selected_uids, expression),
                                      ("browser", sorted(partitions["browser"]), FILTERS["browser"])):
        suites[suite] = {"filter": expression, "minimum": len(tests), "expected": len(tests),
                         "identities": sorted(digest(uid) for uid in tests), "population": catalog_identity}
    suites["dotnet"].update(categories=selected["categories"], omitted=omitted,
                             partition_counts={key: len(partitions[key]) for key in PARTITIONS})
    files = sorted(p.name for p in (root / "tests").glob("*.test.js"))
    require(files, "Frontend discovery is empty")
    suites["frontend"] = {"filter": "npm test (all root *.test.js files)", "minimum": len(files),
                           "expected": len(files), "files": files}
    members = {key: [{"uid": uid, **value} for uid, value in sorted(tests.items())]
               for key, tests in partitions.items()}
    return suites, round(time.monotonic() - started, 3), members


def prepare(root=ROOT, directory=DIRECTORY, benchmark=False):
    selected = benchmark_decision() if benchmark else decision(root=root)
    suites, seconds, members = population(selected, root)
    result = {"schema": 1, "kind": "benchmark" if benchmark else "qualification", "commit": git("rev-parse", "HEAD", root=root),
              "decision": selected, "suites": suites, "discovery_seconds": seconds}
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "selection.json").write_bytes(canonical(result) + b"\n")
    # Human-readable case inventory; authorization still re-discovers the population.
    (directory / "catalog.json").write_bytes(canonical({"commit": result["commit"], "partitions": members}) + b"\n")
    print(json.dumps({"decision": selected, "counts": suites["dotnet"]["partition_counts"],
                      "selected": suites["dotnet"]["expected"], "discovery_seconds": seconds}, indent=2))


def load(root=ROOT, verify=False, directory=None):
    path = (directory or root / ".artifacts/test-selection") / "selection.json"
    value = json.loads(path.read_bytes())
    require(value.get("schema") == 1 and value.get("kind") == "qualification"
            and value["commit"] == git("rev-parse", "HEAD", root=root), "Missing current-commit qualification population")
    if verify:
        selected = decision(root=root)
        require(selected == value["decision"], "Required selection changed after planning; rerun current base/head")
        suites, _, _ = population(selected, root)
        require(suites == value["suites"], "Discovered test population changed after planning")
    return value


def benchmark_decision():
    # Exercise the same policy on a controlled unrelated change; never a qualification result.
    return {**paths_policy(["src/Asap.Web/Frontend/app.js"]), "fresh": True,
            "reason": "Controlled unrelated-change benchmark"}


def benchmark_population(root=ROOT):
    value = json.loads((root / ".artifacts/test-selection/selection.json").read_bytes())
    require(value.get("kind") == "benchmark" and value.get("schema") == 1
            and value["commit"] == git("rev-parse", "HEAD", root=root)
            and value["decision"] == benchmark_decision(), "Invalid controlled benchmark population")
    import fingerprints
    try:
        fingerprints.checkout(value["commit"], root)
    except ValueError as error:
        raise SelectionError("Benchmark checkout must remain clean at its recorded commit") from error
    return value


def run(root=ROOT, benchmark=False):
    value = benchmark_population(root) if benchmark else load(root)
    selected = value["suites"]["dotnet"]
    args = ["dotnet", "test", "--project", "tests/Asap.Tests/Asap.Tests.csproj", "--configuration", "Release",
            "--no-build", "--no-restore", "--minimum-expected-tests", str(selected["expected"]),
            "--report-trx", "--report-trx-filename", "test-results.trx", "--results-directory", ".artifacts/ci-performance",
            "--filter", selected["filter"]]
    return subprocess.call(args, cwd=root)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("prepare", "run", "prepare-benchmark", "run-benchmark", "verify-benchmark"))
    args = parser.parse_args()
    if args.operation == "prepare":
        prepare()
        return 0
    if args.operation == "prepare-benchmark":
        prepare(benchmark=True)
        return 0
    if args.operation == "verify-benchmark":
        import reuse
        value = benchmark_population()
        suites, _, _ = population(benchmark_decision())
        require(suites == value["suites"], "Benchmark population changed")
        results = reuse.execution_results("dotnet", suites["dotnet"])
        report = {"kind": "benchmark-only", "commit": value["commit"], "results": results,
                  "omitted": suites["dotnet"]["omitted"], "discovery_seconds": value["discovery_seconds"]}
        (DIRECTORY / "benchmark-results.json").write_bytes(canonical(report) + b"\n")
        print(f"Controlled benchmark only: {results['passed']} passed, {results['failed']} failed, {results['skipped']} skipped; no qualification or reusable evidence.")
        return 0
    return run(benchmark=args.operation == "run-benchmark")


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        message = str(error) if isinstance(error, SelectionError) else type(error).__name__
        print("::error::Test selection failed: " + message, file=sys.stderr)
        sys.exit(1)
