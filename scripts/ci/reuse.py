"""Fail-closed Actions test evidence lookup and the required suite verification gate."""

import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import io
import json
import os
from pathlib import Path
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

import fingerprints as fp


DIRECTORY = fp.ROOT / ".artifacts/test-reuse"
STEPS = {
    "dotnet": "Run .NET and real SQL tests (non-browser partition)",
    "browser": "Run browser accessibility tests",
    "frontend": "Run legacy/frontend unit tests",
}
LABELS = {"dotnet": ".NET/SQL", "browser": "Browser", "frontend": "Frontend"}
MAX_ARCHIVE = 4 * 1024 * 1024


class EvidenceError(ValueError):
    """Only developer-authored, nonsensitive validation messages may be reported."""


def failure_reason(error):
    return str(error) if isinstance(error, EvidenceError) else type(error).__name__


def utcnow():
    return datetime.now(timezone.utc)


def timestamp(value):
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.utcoffset() != timedelta(0):
        raise ValueError("Evidence timestamp must be UTC")
    return parsed


def iso(value):
    return value.isoformat().replace("+00:00", "Z")


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(fp.canonical(value) + b"\n")


def artifact_name(config, suite, run, attempt):
    return f"asap-test-evidence-v{config['schema']}-{suite}-{run}-{attempt}"


def require(condition, message):
    if not condition:
        raise EvidenceError(message)


def integer(value):
    return type(value) is int and value > 0


class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, newurl):
        require(urllib.parse.urlsplit(newurl).scheme == "https", "Insecure API redirect")
        redirected = super().redirect_request(request, response, code, message, headers, newurl)
        if urllib.parse.urlsplit(request.full_url).netloc != urllib.parse.urlsplit(newurl).netloc:
            # Artifact/log downloads use signed storage URLs. Never forward the GitHub token.
            redirected.remove_header("Authorization")
        return redirected


class GitHub:
    def __init__(self, repository, token):
        self.base = "https://api.github.com/repos/" + repository
        self.token = token
        self.opener = urllib.request.build_opener(SafeRedirect())
        self.deadline = time.monotonic() + 180

    def request(self, path, limit=4 * 1024 * 1024):
        require(path.startswith("/") and not path.startswith("//"), "Invalid API path")
        remaining = self.deadline - time.monotonic()
        require(remaining > 0, "Evidence lookup time budget exhausted")
        request = urllib.request.Request(self.base + path, headers={
            "Authorization": "Bearer " + self.token,
            "Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "asap-ci-test-reuse",
        })
        with self.opener.open(request, timeout=min(15, remaining)) as response:
            body = response.read(limit + 1)
        require(len(body) <= limit, "API response exceeds evidence bounds")
        return body

    def get(self, path):
        return json.loads(self.request(path))

    def pages(self, path, key, pages=3, size=10):
        # Explicit bounds: no Link-following into arbitrary/unlimited history.
        for page in range(1, pages + 1):
            separator = "&" if "?" in path else "?"
            result = self.get(f"{path}{separator}per_page={size}&page={page}")
            values = result[key] if key else result
            require(isinstance(values, list), "Unexpected paginated API response")
            yield from values
            if len(values) < size:
                break


def successful(value):
    return value.get("status") == "completed" and value.get("conclusion") == "success"


def passing_results(results, selection):
    require(all(type(results.get(key)) is int for key in ("total", "executed", "passed", "failed", "skipped")),
            "Test counters must be integers")
    expected = selection.get("expected")
    count = results.get("total")
    require(integer(count) and count >= selection["minimum"], "Required test count was not met")
    if expected is not None:
        require(count == expected, "Executed/discovered test count mismatch")
    require(results.get("executed") == count and results.get("passed") == count
            and results.get("failed") == 0 and results.get("skipped") == 0,
            "Missing, failed or skipped tests")
    identities = results.get("identities", [])
    require(len(identities) == count and len(set(identities)) == count
            and all(isinstance(i, str) and re.fullmatch(r"[0-9a-f]{64}", i) for i in identities),
            "Missing or duplicate test outcomes")
    if "files" in selection:
        require(identities == [fp.digest(name) for name in selection["files"]], "Frontend selection mismatch")


def execution_results(suite, selection, directory=DIRECTORY):
    if suite == "frontend":
        raw = json.loads((directory / "frontend-results.json").read_text(encoding="utf-8"))
        require(raw.get("filter") == "" and raw.get("failed") == 0 and raw.get("skipped") == 0,
                "Frontend run was filtered or incomplete")
        require(raw["files"] == selection["files"], "Frontend discovery changed during execution")
        results = {"total": len(raw["files"]), "executed": raw["executed"], "passed": raw["passed"],
                   "failed": raw["failed"], "skipped": raw["skipped"],
                   "identities": [fp.digest(name) for name in raw["files"]]}
    else:
        path = (fp.ROOT / ".artifacts/ci-performance/test-results.trx" if suite == "dotnet"
                else directory / "browser-results.trx")
        root = ET.parse(path).getroot()
        ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
        summary = root.find("t:ResultSummary", ns)
        require(summary is not None and summary.get("outcome") in ("Completed", "Passed"),
                "Native test run did not complete")
        counters = summary.find("t:Counters", ns)
        require(counters is not None, "Native test counters missing")
        counts = {key: int(value) for key, value in counters.attrib.items()}
        for key, value in counts.items():
            if key not in ("total", "executed", "passed", "completed"):
                require(value == 0, "Native test report contains non-passing results")
        outcomes = root.findall("t:Results/t:UnitTestResult", ns)
        require(len(outcomes) == counts["total"] and all(o.get("outcome") == "Passed" for o in outcomes),
                "Native test outcomes do not match counters")
        require(all(o.get("executionId") for o in outcomes), "Native test identities missing")
        results = {"total": counts["total"], "executed": counts["executed"], "passed": counts["passed"],
                   "failed": 0, "skipped": 0,
                   "identities": [fp.digest(o.get("executionId")) for o in outcomes]}
    passing_results(results, selection)
    return results


class Validator:
    def __init__(self, api, config, current, now=None):
        self.api, self.config, self.current = api, config, current
        self.now = now or utcnow()
        self.contexts = {}
        self.workflow = api.get("/actions/workflows/dotnet.yml")
        require(self.workflow["path"] == config["workflow"] and integer(self.workflow["id"]),
                "Unexpected workflow identity")

    def context(self, run):
        config = self.config
        run_id, attempt = run["id"], run["run_attempt"]
        require(integer(run_id) and integer(attempt), "Invalid run/attempt")
        key = run_id, attempt
        if key in self.contexts:
            return self.contexts[key]
        require(successful(run) and run["workflow_id"] == self.workflow["id"]
                and run["path"] == config["workflow"], "Run/workflow did not complete successfully")
        for repository in (run["repository"], run["head_repository"]):
            require(repository["id"] == config["repository_id"]
                    and repository["full_name"] == config["repository"], "Wrong repository or external fork")
        require(run["event"] in ("push", "pull_request", "workflow_dispatch"), "Untrusted workflow event")
        require(self.now - timedelta(days=config["lifetime_days"]) <= timestamp(run["created_at"]) <= self.now,
                "Originating run is outside the evidence lifetime")
        original = self.api.get(f"/actions/runs/{run_id}/attempts/{attempt}")
        require(successful(original) and original["head_sha"] == run["head_sha"]
                and original["run_attempt"] == attempt and original["id"] == run_id,
                "Unsuccessful or mismatched originating attempt")
        jobs = list(self.api.pages(f"/actions/runs/{run_id}/attempts/{attempt}/jobs", "jobs"))
        matching = [job for job in jobs if job["name"] == config["job"]]
        require(len(matching) == 1 and successful(matching[0]), "Required job was not successful")
        job = matching[0]
        require(job["run_id"] == run_id and job["head_sha"] == run["head_sha"]
                and job["runner_group_name"] == config["runner_group"]
                and sorted(job["labels"]) == sorted(config["runner_labels"]), "Wrong job/runner identity")
        logs = self.api.request(f"/actions/jobs/{job['id']}/logs", limit=64 * 1024 * 1024).decode("utf-8-sig")
        # This is actions/checkout's own Git output, before any repository test command.
        checkout = re.findall(r"\[command\][^\n]*[/\\]git log -1 --format=%H\r?\n[^\n]*?Z ([0-9a-f]{40})\r?\n", logs)
        require(len(checkout) == 1, "Exact source checkout cannot be independently verified")
        commit = checkout[0]
        source = self.api.get(f"/git/commits/{commit}")
        require(source["sha"] == commit, "Wrong Git source commit")
        if run["event"] == "pull_request":
            require(len(source["parents"]) == 2 and source["parents"][1]["sha"] == run["head_sha"],
                    "Synthetic merge does not match the tested PR head")
            prs = list(self.api.pages(f"/commits/{run['head_sha']}/pulls", None))
            # The commit-specific endpoint verifies historical association too: a PR
            # may advance after execution without invalidating its old evidence.
            trusted = [pr for pr in prs if pr["head"]["repo"] and pr["head"]["repo"]["id"] == config["repository_id"]
                       and pr["base"]["repo"]["id"] == config["repository_id"]
                       and pr["base"]["ref"] == "main"
                       and pr["author_association"] in ("OWNER", "MEMBER", "COLLABORATOR")]
            require(bool(trusted), "PR execution was not authored in the trusted repository")
        else:
            require(commit == run["head_sha"], "Run did not check out its exact source SHA")
        tree = source["tree"]["sha"]
        remote = self.api.get(f"/git/trees/{tree}?recursive=1")
        require(remote["sha"] == tree and remote.get("truncated") is False, "Incomplete source tree")
        entries = [[entry["path"], entry["mode"], entry["type"], entry["sha"]]
                   for entry in remote["tree"] if entry["type"] != "tree"]
        context = {"run": run, "job": job, "commit": commit, "tree": tree, "entries": entries}
        self.contexts[key] = context
        return context

    def validate(self, run, artifact, suite):
        config, current = self.config, self.current["suites"][suite]
        require(not current["unavailable"], "Current environment identity is unavailable")
        context = self.context(run)
        require(integer(artifact["id"]) and artifact["name"] == artifact_name(config, suite, run["id"], run["run_attempt"])
                and artifact["expired"] is False and timestamp(artifact["expires_at"]) > self.now,
                "Artifact is missing, expired or from another attempt")
        origin = artifact["workflow_run"]
        require(origin["id"] == run["id"] and origin["repository_id"] == config["repository_id"]
                and origin["head_repository_id"] == config["repository_id"] and origin["head_sha"] == run["head_sha"],
                "Artifact origin does not match the run")
        require(0 < artifact["size_in_bytes"] <= MAX_ARCHIVE
                and re.fullmatch(r"sha256:[0-9a-f]{64}", artifact["digest"]), "Missing artifact integrity metadata")
        archive = self.api.request(f"/actions/artifacts/{artifact['id']}/zip", limit=MAX_ARCHIVE)
        require(len(archive) == artifact["size_in_bytes"]
                and "sha256:" + hashlib.sha256(archive).hexdigest() == artifact["digest"], "Artifact checksum mismatch")
        with zipfile.ZipFile(io.BytesIO(archive)) as zipped:
            require(sorted(zipped.namelist()) == ["manifest.json", "results.json"]
                    and sum(item.file_size for item in zipped.infolist()) <= MAX_ARCHIVE,
                    "Unexpected evidence archive contents")
            manifest = json.loads(zipped.read("manifest.json"))
            results = json.loads(zipped.read("results.json"))
        require(all(integer(manifest[key]) for key in ("schema", "repository_id", "run_id", "run_attempt", "lifetime_days")),
                "Invalid manifest numeric identities")
        require(manifest["schema"] == config["schema"] and manifest["kind"] == "executed"
                and manifest["outcome"] == "passed" and manifest["suite"] == suite,
                "Wrong schema/suite or reused-only evidence")
        require(manifest["repository"] == config["repository"] and manifest["repository_id"] == config["repository_id"]
                and manifest["workflow"] == config["workflow"] and manifest["job"] == config["job"]
                and manifest["run_id"] == run["id"] and manifest["run_attempt"] == run["run_attempt"]
                and manifest["source_commit"] == context["commit"] and manifest["source_tree"] == context["tree"],
                "Forged or incomplete manifest provenance")
        require(manifest["fingerprint"] == current["fingerprint"]
                and manifest["environment_fingerprint"] == current["environment"]
                and fp.digest(manifest["environment_inputs"]) == current["environment"]
                and manifest["selection"] == current["selection"]
                and manifest["source_fingerprint"] == current["source"]
                and fp.source_fingerprint(context["entries"], suite, config) == current["source"],
                "Source, environment or selection fingerprint mismatch")
        executed = timestamp(manifest["executed_at"])
        require(manifest["lifetime_days"] == config["lifetime_days"]
                and self.now - timedelta(days=config["lifetime_days"]) <= executed <= self.now
                and timestamp(context["job"]["started_at"]) <= executed <= timestamp(context["job"]["completed_at"])
                and timestamp(manifest["expires_at"]) == executed + timedelta(days=config["lifetime_days"]),
                "Execution evidence expired or has invalid timestamps")
        passing_results(results, current["selection"])
        require(all(type(value) is int for value in manifest["counts"].values())
                and manifest["results_sha256"] == fp.digest(results) and manifest["counts"] == {
            key: results[key] for key in ("total", "executed", "passed", "failed", "skipped")}, "Invalid test outcomes")
        steps = context["job"]["steps"]
        for name in (STEPS[suite], "Verify required test suites", f"Upload original {suite} execution evidence"):
            matching = [step for step in steps if step["name"] == name]
            require(len(matching) == 1 and successful(matching[0]), "Required original execution step was skipped or failed")
        step = next(step for step in steps if step["name"] == STEPS[suite])
        seconds = (timestamp(step["completed_at"]) - timestamp(step["started_at"])).total_seconds()
        require(seconds >= 0, "Invalid original test timing")
        return {"run_id": run["id"], "attempt": run["run_attempt"], "artifact_id": artifact["id"],
                "artifact_digest": artifact["digest"], "url": f"https://github.com/{config['repository']}/actions/runs/{run['id']}",
                "age_seconds": (self.now - executed).total_seconds(), "saved_seconds": seconds}


def lookup(api, config, current, forced=False):
    decisions = {suite: {"reuse": False, "reason": "Forced full CI" if forced else "No matching successful original evidence"}
                 for suite in fp.SUITES}
    for suite in fp.SUITES:
        if current["suites"][suite]["unavailable"]:
            decisions[suite]["reason"] = current["suites"][suite]["unavailable"]
    if forced:
        return decisions
    try:
        validator = Validator(api, config, current)
        runs = api.pages("/actions/workflows/dotnet.yml/runs?status=success", "workflow_runs")
        for candidate in runs:
            pending = [s for s in fp.SUITES if not decisions[s]["reuse"] and not current["suites"][s]["unavailable"]]
            if not pending:
                break
            # Never consider the active run or a run whose latest attempt failed.
            if str(candidate.get("id")) == os.environ.get("GITHUB_RUN_ID") or not successful(candidate):
                continue
            artifacts = list(api.pages(f"/actions/runs/{candidate['id']}/artifacts", "artifacts"))
            matches = {s: [a for a in artifacts if a.get("name") == artifact_name(config, s, candidate["id"], candidate["run_attempt"])]
                       for s in pending}
            if not any(matches.values()):
                continue
            run = api.get(f"/actions/runs/{candidate['id']}")
            for suite in pending:
                if len(matches[suite]) != 1:
                    continue
                try:
                    source = validator.validate(run, matches[suite][0], suite)
                    decisions[suite] = {"reuse": True, "reason": "Verified successful original execution", "source": source}
                except (urllib.error.URLError, TimeoutError, ConnectionError):
                    # A transport outage can affect the gate's later revalidation too.
                    # Restore execution globally instead of preserving a partial HIT.
                    raise
                except Exception as error:
                    decisions[suite]["reason"] = "Unverifiable evidence: " + failure_reason(error)
    except Exception as error:
        for suite in fp.SUITES:
            decisions[suite] = {"reuse": False, "reason": "GitHub lookup unavailable: " + failure_reason(error)}
    return decisions


def forced_full():
    return (os.environ.get("ASAP_FORCE_FULL_CI") == "true"
            or os.environ.get("ASAP_DEPLOY_TEST_IIS") == "true"
            or os.environ.get("GITHUB_REF_TYPE") == "tag"
            or os.environ.get("GITHUB_EVENT_NAME") == "release")


def api_client(config):
    require(os.environ.get("GITHUB_REPOSITORY") == config["repository"], "Wrong active repository")
    token = os.environ.get("GH_TOKEN", "")
    require(bool(token), "Read-only Actions token unavailable")
    return GitHub(config["repository"], token)


def plan(directory=DIRECTORY):
    config = fp.configuration()
    (directory / "current.json").unlink(missing_ok=True)
    current = None
    decisions = {s: {"reuse": False, "reason": "Fingerprint unavailable; execute normal tests"} for s in fp.SUITES}
    try:
        current = fp.calculate(os.environ["GITHUB_SHA"], config)
        write_json(directory / "current.json", current)
        decisions = lookup(None, config, current, True) if forced_full() else lookup(api_client(config), config, current)
    except Exception as error:
        # Deliberately omit exception messages: subprocess/API errors can contain sensitive data.
        print(f"::notice::Test reuse unavailable ({type(error).__name__}); normal tests required.")
    write_json(directory / "plan.json", {"run_id": os.environ["GITHUB_RUN_ID"],
               "attempt": os.environ["GITHUB_RUN_ATTEMPT"], "decisions": decisions})
    outputs = {s: str(decisions[s]["reuse"]).lower() for s in fp.SUITES}
    outputs["schema"] = config["schema"]
    outputs["sql_image"] = current["sql_image"] if current else fp.SQL_TAG
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        for key, value in outputs.items():
            output.write(f"{key}={value}\n")
    for suite in fp.SUITES:
        print(f"{LABELS[suite]}: {'HIT' if decisions[suite]['reuse'] else 'MISS'} — {decisions[suite]['reason']}")


def record(config, current, suite, results, directory=DIRECTORY):
    identity = current["suites"][suite]
    if identity["unavailable"]:
        return
    now = utcnow()
    manifest = {
        "schema": config["schema"], "kind": "executed", "outcome": "passed", "suite": suite,
        "repository": config["repository"], "repository_id": config["repository_id"],
        "workflow": config["workflow"], "job": config["job"],
        "run_id": int(os.environ["GITHUB_RUN_ID"]), "run_attempt": int(os.environ["GITHUB_RUN_ATTEMPT"]),
        "source_commit": current["commit"], "source_tree": current["tree"],
        "source_fingerprint": identity["source"], "fingerprint": identity["fingerprint"],
        "environment_fingerprint": identity["environment"], "environment_inputs": identity["environment_inputs"],
        "selection": identity["selection"],
        "counts": {key: results[key] for key in ("total", "executed", "passed", "failed", "skipped")},
        "results_sha256": fp.digest(results), "executed_at": iso(now),
        "lifetime_days": config["lifetime_days"], "expires_at": iso(now + timedelta(days=config["lifetime_days"])),
    }
    write_json(directory / "evidence" / suite / "manifest.json", manifest)
    write_json(directory / "evidence" / suite / "results.json", results)


def summary(rows, current):
    lines = ["## Required test verification", "", "| Suite | Current status | Original source |", "| --- | --- | --- |"]
    for suite in fp.SUITES:
        row = rows[suite]
        source = row.get("source", {}).get("url")
        lines.append(f"| {LABELS[suite]} | {row['status']} | " + (f"[Original run]({source})" if source else "Current run") + " |")
    lines.append("")
    for suite in fp.SUITES:
        row = rows[suite]
        identity = current["suites"][suite]["fingerprint"] if current else "unavailable"
        detail = f"- {LABELS[suite]}: `{identity}`; {row['reason']}."
        if row.get("source"):
            source = row["source"]
            detail += (f" Evidence age {source['age_seconds'] / 3600:.2f} h; "
                       f"approximately {source['saved_seconds'] / 60:.2f} min saved (original test step wall time); "
                       f"artifact `{source['artifact_id']}` / `{source['artifact_digest']}`.")
        lines.append(detail)
    lines.extend(["", "Build, publish, deployment ZIP identity and SHA-256 validation run for the current commit.", ""])
    return "\n".join(lines)


def verify(directory=DIRECTORY):
    config = fp.configuration()
    try:
        current = json.loads((directory / "current.json").read_bytes())
    except (OSError, ValueError):
        current = None
    try:
        planned = json.loads((directory / "plan.json").read_bytes())
    except (OSError, ValueError):
        planned = None
    selected = fp.selections()
    checkout_valid = False
    try:
        commit, tree = fp.checkout(os.environ["GITHUB_SHA"])
        entries = fp.tracked_entries()
        if current:
            require(current["commit"] == commit and current["tree"] == tree, "Current source identity changed")
            for suite in fp.SUITES:
                identity = current["suites"][suite]
                inputs = {key: identity[key] for key in ("schema", "suite", "source", "environment", "selection")}
                require(identity["fingerprint"] == fp.digest(inputs)
                        and identity["schema"] == config["schema"] and identity["suite"] == suite
                        and identity["source"] == fp.source_fingerprint(entries, suite, config)
                        and identity["environment"] == fp.digest(identity["environment_inputs"])
                        and {k: v for k, v in identity["selection"].items() if k != "expected"} == selected[suite],
                        "Current fingerprint inputs changed")
        checkout_valid = True
    except Exception:
        pass
    rows, failures = {}, []
    validator = None
    for suite in fp.SUITES:
        decision = (planned or {}).get("decisions", {}).get(suite, {"reuse": False, "reason": "No reuse plan"})
        rows[suite] = {"status": "FAILED", "reason": decision["reason"]}
        try:
            require(checkout_valid, "Current source/fingerprint identity could not be verified")
            outcome = os.environ.get(suite.upper() + "_OUTCOME")
            if decision["reuse"]:
                require(outcome == "skipped" and current is not None and not forced_full(), "Invalid skipped/reused result")
                require(planned["run_id"] == os.environ["GITHUB_RUN_ID"]
                        and planned["attempt"] == os.environ["GITHUB_RUN_ATTEMPT"], "Reuse plan is from another execution")
                # Re-fetch immutable evidence at the gate; a plan's JSON claim cannot authorize success.
                if validator is None:
                    validator = Validator(api_client(config), config, current)
                source = decision["source"]
                run = validator.api.get(f"/actions/runs/{source['run_id']}")
                artifact = validator.api.get(f"/actions/artifacts/{source['artifact_id']}")
                verified = validator.validate(run, artifact, suite)
                require(verified["artifact_digest"] == source["artifact_digest"], "Evidence identity changed after lookup")
                rows[suite] = {"status": "REUSED", "reason": decision["reason"], "source": verified}
            else:
                require(outcome == "success", "Suite did not execute successfully and has no validated evidence")
                selection = current["suites"][suite]["selection"] if current else selected[suite]
                results = execution_results(suite, selection, directory)
                rows[suite] = {"status": "EXECUTED", "reason": decision["reason"]}
                if current:
                    record(config, current, suite, results, directory)
        except Exception as error:
            rows[suite]["reason"] = "Required verification failed: " + failure_reason(error)
            failures.append(suite)
    report = summary(rows, current)
    print(report)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as output:
            output.write(report)
    write_json(directory / "verification.json", rows)
    require(not failures, "Required suites lack verified success: " + ", ".join(failures))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("checkout", "plan", "verify"))
    args = parser.parse_args()
    if args.operation == "checkout":
        commit, tree = fp.checkout(os.environ["GITHUB_SHA"])
        print(f"Verified exact workflow source {commit}, tree {tree}")
    elif args.operation == "plan":
        plan()
    else:
        verify()


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"::error::Required test verification failed ({type(error).__name__}).", file=sys.stderr)
        sys.exit(1)
