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
CATEGORIES = {"browser": "Browser"}
PARTITIONS = ("core", "browser")
ALL = ["core"]
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


def ancestor(older, newer, root=ROOT):
    return subprocess.run(["git", "merge-base", "--is-ancestor", older, newer], cwd=root, timeout=30,
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0


def full(reason, fresh=True):
    return {"categories": list(ALL), "fresh": fresh, "reason": reason}


def paths_policy(paths):
    """Select every required suite; fingerprints decide whether inputs were tested."""
    require(isinstance(paths, list) and 0 < len(paths) <= 3000, "Missing or excessive changed paths")
    for path in paths:
        require(isinstance(path, str) and path and not path.startswith(("/", chr(92)))
                and chr(92) not in path and not any(p in ("", ".", "..") for p in path.split("/"))
                and not any(ord(c) < 32 for c in path), "Invalid changed path")
    return full("Current suites required; reuse must independently match all dependencies", fresh=False)


def sha(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{40}", value) and value != "0" * 40


def changed_paths(event_name, event, env, root=ROOT, current_pr=None):
    """Diff the exact tested integration tree, including base changes since fork."""
    tested = env.get("GITHUB_SHA")
    require(sha(tested) and git("rev-parse", "HEAD", root=root) == tested, "Tested commit identity is unavailable")
    ref = env.get("GITHUB_REF", "")
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
        head = pr["head"]["sha"]
        require(sha(head) and current["head"]["sha"] == head, "PR head changed after the event")
        parents = git("rev-list", "--parents", "-n", "1", tested, root=root).split()
        require(len(parents) == 3 and parents[0] == tested and parents[2] == head,
                "PR synthetic merge does not match the tested head")
        # GitHub's advertised base SHA can lag the base used to create the merge.
        # Trust the actual first parent only after binding it to fetched main.
        base = parents[1]
        require(ancestor(base, "refs/remotes/origin/main", root),
                "PR merge parent is outside trusted main history")
        for candidate in (pr, current):
            require(sha(candidate["base"]["sha"]), "Invalid advertised PR base SHA")
        merge_bases = git("merge-base", "--all", base, head, root=root).splitlines()
        require(len(merge_bases) == 1 and sha(merge_bases[0]), "PR merge base is ambiguous")
        baseline = merge_bases[0]
    elif env.get("GITHUB_REF_TYPE") == "tag" or ref.startswith("refs/tags/"):
        require(ref.startswith("refs/tags/") and env.get("GITHUB_REF_TYPE") in (None, "tag"), "Invalid tag ref metadata")
        git("check-ref-format", ref, root=root)
        require(git("rev-parse", "--verify", ref + "^{commit}", root=root) == tested,
                "Tag ref does not identify the tested commit")
        if event_name == "push":
            require(event.get("ref") == ref and event.get("deleted") is False
                    and type(event.get("forced")) is bool and sha(event.get("after"))
                    and git("rev-parse", "--verify", event["after"] + "^{commit}", root=root) == tested,
                    "Tag push metadata does not identify the tested commit")
        if event_name == "release":
            require(ref == "refs/tags/" + event["release"]["tag_name"], "Release tag metadata changed")
        if event_name in ("deployment", "deployment_status"):
            require(event["deployment"]["sha"] == tested, "Deployment source metadata changed")
        return []
    elif event_name == "push":
        require(ref.startswith("refs/heads/") and event.get("ref") == ref and event.get("after") == tested
                and event.get("deleted") is False and type(event.get("forced")) is bool,
                "Ambiguous push metadata")
        git("check-ref-format", ref, root=root)
        if event.get("before") == "0" * 40:
            require(event.get("created") is True, "Missing new-branch metadata")
            return []
        require(sha(event.get("before")), "Missing previous push source")
        baseline = event["before"]
    else:
        require(isinstance(event_name, str) and bool(event_name), "Missing workflow event identity")
        require(event_name != "release", "Release is missing its tag ref")
        if event_name in ("deployment", "deployment_status"):
            require(event["deployment"]["sha"] == tested, "Deployment source metadata changed")
        # All suites are mandatory. Other event types need no dependency diff;
        # the exact checkout and complete fingerprint still authorize reuse.
        return []
    # Disable rename detection: both deleted and added dependency paths matter.
    raw = subprocess.check_output(["git", "diff", "--no-renames", "--name-only", "-z", baseline, tested],
                                  cwd=root, timeout=30, stderr=subprocess.PIPE)
    require(len(raw) <= 1024 * 1024 and (not raw or raw.endswith(b"\0")), "Incomplete Git diff")
    return raw[:-1].decode("utf-8").split("\0") if raw else []


def decision(env=None, root=ROOT):
    env = os.environ if env is None else env
    event_name = env.get("GITHUB_EVENT_NAME")
    if env.get("ASAP_FORCE_FULL_CI") == "true":
        return full("Explicit force-full")
    if (event_name == "workflow_dispatch" and env.get("GITHUB_REF_TYPE") != "tag"
            and not env.get("GITHUB_REF", "").startswith("refs/tags/")):
        return full("Manual full qualification", fresh=False)
    try:
        require(isinstance(event_name, str) and bool(event_name), "Missing workflow event identity")
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
        selected = paths_policy(paths) if paths else full("Complete current population; inputs independently fingerprinted", fresh=False)
        return {**selected, "base_paths": paths}
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
    expression = FILTERS["core"]
    require(expression == FILTERS["core"], "Unexpected execution filter")
    suites = {}
    for suite, tests, expression in (("dotnet", selected_uids, expression),
                                      ("browser", sorted(partitions["browser"]), FILTERS["browser"])):
        # The full disjoint union is validated above on every invocation. Its
        # unrelated partition must not become an input to this suite's reuse key.
        suites[suite] = {"filter": expression, "minimum": len(tests), "expected": len(tests),
                         "identities": sorted(digest(uid) for uid in tests),
                         "population": digest({uid: catalog[uid] for uid in tests})}
    files = frontend_files(root)
    require(files, "Frontend discovery is empty")
    suites["frontend"] = {"filter": "npm test (root *.test.js and declared portable acceptance files)", "minimum": len(files),
                           "expected": len(files), "files": files}
    members = {key: [{"uid": uid, **value} for uid, value in sorted(tests.items())]
               for key, tests in partitions.items()}
    return suites, round(time.monotonic() - started, 3), members


def frontend_files(root=ROOT):
    declared = json.loads((root / "tests/frontend-tests.json").read_bytes())
    portable = declared["portable"]
    require(isinstance(portable, list) and portable and all(
        isinstance(name, str) and re.fullmatch(r"acceptance/[a-z_]+\.test\.cjs", name) for name in portable),
        "Invalid portable frontend declaration")
    files = sorted([p.name for p in (root / "tests").glob("*.test.js")] + portable)
    require(len(files) == len(set(files)) and all((root / "tests" / name).is_file() for name in files),
            "Missing or duplicate frontend file")
    return files


def prepare(root=ROOT, directory=DIRECTORY):
    selected = decision(root=root)
    suites, seconds, members = population(selected, root)
    result = {"schema": 2, "kind": "qualification", "commit": git("rev-parse", "HEAD", root=root),
              "decision": selected, "suites": suites, "discovery_seconds": seconds}
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "selection.json").write_bytes(canonical(result) + b"\n")
    # Human-readable case inventory; authorization still re-discovers the population.
    (directory / "catalog.json").write_bytes(canonical({"commit": result["commit"], "partitions": members}) + b"\n")
    print(json.dumps({"decision": selected, "counts": {key: len(value) for key, value in members.items()},
                      "selected": suites["dotnet"]["expected"], "discovery_seconds": seconds}, indent=2))


def load(root=ROOT, verify=False, directory=None):
    path = (directory or root / ".artifacts/test-selection") / "selection.json"
    value = json.loads(path.read_bytes())
    require(value.get("schema") == 2 and value.get("kind") == "qualification"
            and value["commit"] == git("rev-parse", "HEAD", root=root), "Missing current-commit qualification population")
    if verify:
        selected = decision(root=root)
        require(selected == value["decision"], "Required selection changed after planning; rerun current base/head")
        suites, _, _ = population(selected, root)
        require(suites == value["suites"], "Discovered test population changed after planning")
    return value


def execute(selected, root=ROOT):
    args = ["dotnet", "test", "--project", "tests/Asap.Tests/Asap.Tests.csproj", "--configuration", "Release",
            "--no-build", "--no-restore", "--minimum-expected-tests", str(selected["expected"]),
            "--report-trx", "--report-trx-filename", "test-results.trx", "--results-directory", ".artifacts/ci-performance",
            "--filter", selected["filter"]]
    return subprocess.call(args, cwd=root)


def draft(root=ROOT):
    import reuse
    expression = ("FullyQualifiedName~Asap.Tests.Unit.|FullyQualifiedName~Asap.Tests.PolicyContractTests.|"
                  "FullyQualifiedName~Asap.Tests.AnalyticsContractTests.")
    tests = discover(expression, root)
    selected = {"filter": expression, "minimum": len(tests), "expected": len(tests),
                "identities": sorted(digest(uid) for uid in tests)}
    result = execute(selected, root)
    require(result == 0, "Draft tests failed")
    reuse.execution_results("dotnet", selected)
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("prepare", "run", "run-draft"))
    args = parser.parse_args()
    if args.operation == "prepare":
        prepare()
        return 0
    if args.operation == "run-draft":
        return draft()
    return execute(load()["suites"]["dotnet"])


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        message = str(error) if isinstance(error, SelectionError) else type(error).__name__
        print("::error::Test selection failed: " + message, file=sys.stderr)
        sys.exit(1)
