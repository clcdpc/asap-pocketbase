"""Fail-closed Actions test evidence lookup and the required suite verification gate."""

import argparse
import base64
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
import selection as test_selection


DIRECTORY = fp.ROOT / ".artifacts/test-reuse"
STEPS = {
    "dotnet": "Run .NET and real SQL tests (non-browser partition)",
    "browser": "Run browser accessibility tests",
    "frontend": "Run frontend unit tests",
}
LABELS = {"dotnet": ".NET/SQL", "browser": "Browser", "frontend": "Frontend"}
MAX_ARCHIVE = 4 * 1024 * 1024


class EvidenceError(ValueError):
    """Only developer-authored, nonsensitive validation messages may be reported."""


def failure_reason(error):
    return str(error) if isinstance(error, (EvidenceError, test_selection.SelectionError)) else type(error).__name__


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


def artifact_name(config, suite, fingerprint):
    require(suite in fp.SUITES and re.fullmatch(r"[0-9a-f]{64}", fingerprint), "Invalid evidence lookup identity")
    return f"asap-test-evidence-v{config['schema']}-{suite}-{fingerprint}"


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
    if "identities" in selection:
        require(sorted(identities) == selection["identities"],
                "Executed test identities differ from the selected discovered population")
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
        output = "ci-performance" if suite == "dotnet" else "browser-performance"
        path = fp.ROOT / ".artifacts" / output / "test-results.trx"
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
        executions = [o.get("executionId") for o in outcomes]
        require(all(executions) and len(set(executions)) == len(executions), "Native execution identities missing or duplicated")
        require(all(o.get("testId") for o in outcomes), "Native test identities missing")
        results = {"total": counts["total"], "executed": counts["executed"], "passed": counts["passed"],
                   "failed": 0, "skipped": 0,
                   "identities": sorted(fp.digest(o.get("testId")) for o in outcomes)}
    passing_results(results, selection)
    return results


class Validator:
    def __init__(self, api, config, current, now=None):
        self.api, self.config, self.current = api, config, current
        self.now = now or utcnow()
        self.contexts = {}
        self.contracts = {}
        self.workflow = api.get("/actions/workflows/dotnet.yml")
        require(self.workflow["path"] == config["workflow"] and integer(self.workflow["id"]),
                "Unexpected workflow identity")

    def context(self, run, attempt, suite):
        config = self.config
        run_id, latest = run["id"], run["run_attempt"]
        require(integer(run_id) and integer(attempt) and integer(latest)
                and attempt <= latest <= attempt + 10, "Invalid or excessive originating attempts")
        job_name = config["executors"][suite]["name"]
        require(run["workflow_id"] == self.workflow["id"] and run["path"] == config["workflow"],
                "Wrong originating workflow")
        key = run_id, attempt, latest, suite
        if key in self.contexts:
            return self.contexts[key]
        for repository in (run["repository"], run["head_repository"]):
            require(repository["id"] == config["repository_id"]
                    and repository["full_name"] == config["repository"], "Wrong repository or external fork")
        require(isinstance(run.get("event"), str) and bool(run["event"]), "Missing workflow event identity")
        # A new attempt may execute long after the run was first created. Age is
        # enforced below against the job-bound execution time, not run creation.
        require(timestamp(run["created_at"]) <= self.now, "Originating run has a future creation time")
        original = self.api.get(f"/actions/runs/{run_id}/attempts/{attempt}")
        require(original["head_sha"] == run["head_sha"]
                and original["run_attempt"] == attempt and original["id"] == run_id,
                "Unsuccessful or mismatched originating attempt")
        jobs = list(self.api.pages(f"/actions/runs/{run_id}/attempts/{attempt}/jobs", "jobs"))
        self.qualification(jobs, run)
        # Deployment-only retries preserve the complete original qualification.
        # Never ignore a later incomplete or failed worker/reporting gate.
        for later in range(attempt + 1, latest + 1):
            later_jobs = list(self.api.pages(f"/actions/runs/{run_id}/attempts/{later}/jobs", "jobs"))
            deployment_only = bool(later_jobs) and all(job["name"] == "Deploy exact artifact to test IIS"
                                                     for job in later_jobs)
            if not deployment_only:
                self.qualification(later_jobs, run)
            require(all(job["run_id"] == run_id and job["head_sha"] == run["head_sha"] for job in later_jobs),
                    "Later attempt job identity changed")
        job = next(job for job in jobs if job["name"] == job_name)
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
            require(commit == run["head_sha"] or (len(source["parents"]) == 2
                    and source["parents"][1]["sha"] == run["head_sha"]),
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
        context = {"run": run, "attempt": attempt, "job": job, "commit": commit, "tree": tree, "entries": entries}
        self.contexts[key] = context
        return context

    def qualification(self, jobs, run):
        # Suite artifacts alone never establish a completed originating qualification.
        for name in [*(owner["name"] for owner in self.config["executors"].values()), "Build, test and package"]:
            matching = [job for job in jobs if job["name"] == name]
            require(len(matching) == 1 and successful(matching[0])
                    and matching[0]["run_id"] == run["id"] and matching[0]["head_sha"] == run["head_sha"],
                    "Required originating qualification job was missing or unsuccessful")
        package = next(job for job in jobs if job["name"] == self.config["executors"]["frontend"]["name"])
        for name in ("Verify publish artifacts", "Create exact test-IIS deployment ZIP",
                     "Validate test-IIS package without host mutation", "Upload exact test-IIS deployment artifact"):
            steps = [step for step in package["steps"] if step["name"] == name]
            require(len(steps) == 1 and successful(steps[0]), "Original package qualification was incomplete")

    def execution_contract(self, context):
        entries = [e for e in context["entries"] if e[0] == self.config["workflow"]
                   and e[1] == "100644" and e[2] == "blob"]
        require(len(entries) == 1, "Original workflow is missing or ambiguous")
        sha = entries[0][3]
        require(re.fullmatch(r"[0-9a-f]{40}", sha), "Invalid workflow blob SHA")
        if sha not in self.contracts:
            blob = self.api.get(f"/git/blobs/{sha}")
            require(blob.get("sha") == sha and blob.get("encoding") == "base64"
                    and type(blob.get("size")) is int and 0 < blob["size"] <= 524288,
                    "Invalid workflow Git blob")
            raw = base64.b64decode("".join(blob["content"].split()), validate=True)
            require(len(raw) == blob["size"] and
                    hashlib.sha1(b"blob " + str(len(raw)).encode() + b"\0" + raw).hexdigest() == sha,
                    "Original workflow blob failed Git identity check")
            self.contracts[sha] = fp.full_ci_execution_contract(raw.decode("utf-8"))
        return self.contracts[sha]

    def validate(self, run, artifact, suite):
        config, current = self.config, self.current["suites"][suite]
        require(not current["unavailable"], "Current environment identity is unavailable")
        require(integer(artifact["id"]) and artifact["name"] == artifact_name(config, suite, current["fingerprint"])
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
        # Reject a different input key before fetching potentially large job logs
        # and Git trees. This is only a filter; every HIT still proves provenance.
        require(manifest["fingerprint"] == current["fingerprint"], "Different effective test inputs")
        context = self.context(run, manifest["run_attempt"], suite)
        require(manifest["repository"] == config["repository"] and manifest["repository_id"] == config["repository_id"]
                and manifest["workflow"] == config["workflow"] and manifest["job"] == config["executors"][suite]["name"]
                and manifest["run_id"] == run["id"] and manifest["run_attempt"] == context["attempt"]
                and manifest["source_commit"] == context["commit"] and manifest["source_tree"] == context["tree"],
                "Forged or incomplete manifest provenance")
        require(manifest["environment_fingerprint"] == current["environment"]
                and fp.environment_fingerprint(manifest["environment_inputs"]) == current["environment"]
                and manifest["selection"] == current["selection"],
                "Environment or selection mismatch")
        origin_contract = self.execution_contract(context)
        source = fp.source_fingerprint(context["entries"], suite, config, origin_contract)
        require(source == current["source"], "Test source or execution contract changed")
        require(manifest.get("source_algorithm") == fp.SOURCE_ALGORITHM
                and manifest["source_fingerprint"] == source
                and manifest["fingerprint"] == current["fingerprint"], "Current source fingerprint mismatch")
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
        return {"run_id": run["id"], "attempt": context["attempt"], "artifact_id": artifact["id"],
                "artifact_digest": artifact["digest"], "url": f"https://github.com/{config['repository']}/actions/runs/{run['id']}",
                "age_seconds": (self.now - executed).total_seconds(), "saved_seconds": seconds}


def validate_executor(env=None):
    env = os.environ if env is None else env
    suites = [suite for suite, owner in fp.EXECUTORS.items() if owner["id"] == env.get("GITHUB_JOB")]
    require(len(suites) == 1, "Only a fixed owning job can qualify its assigned suite")
    return suites[0]


def lookup(api, config, current, forced=False, suites=fp.SUITES):
    decisions = {suite: {"reuse": False, "reason": "Forced full CI" if forced else "No matching successful original evidence"}
                 for suite in suites}
    for suite in suites:
        if current["suites"][suite]["unavailable"]:
            decisions[suite]["reason"] = current["suites"][suite]["unavailable"]
    if forced:
        return decisions
    try:
        validator = Validator(api, config, current)
        runs = {}
        for suite in suites:
            if current["suites"][suite]["unavailable"]:
                continue
            # Content-addressed names let the API search all retained matching
            # originals. Drafts, reuse-only runs and unrelated inputs consume no
            # lookup slots. All returned provenance is still independently checked.
            name = artifact_name(config, suite, current["suites"][suite]["fingerprint"])
            artifacts = list(api.pages("/actions/artifacts?name=" + name, "artifacts", size=100))
            for artifact in artifacts:
                run_id = artifact.get("workflow_run", {}).get("id")
                if not integer(run_id) or str(run_id) == os.environ.get("GITHUB_RUN_ID"):
                    continue
                if sum(a.get("workflow_run", {}).get("id") == run_id for a in artifacts) != 1:
                    continue
                try:
                    if run_id not in runs:
                        runs[run_id] = api.get(f"/actions/runs/{run_id}")
                    source = validator.validate(runs[run_id], artifact, suite)
                    decisions[suite] = {"reuse": True, "reason": "Verified successful original execution", "source": source}
                    break
                except (urllib.error.URLError, TimeoutError, ConnectionError):
                    # A transport outage can affect the gate's later revalidation too.
                    # Restore execution globally instead of preserving a partial HIT.
                    raise
                except Exception as error:
                    decisions[suite]["reason"] = "Unverifiable evidence: " + failure_reason(error)
    except Exception as error:
        for suite in suites:
            decisions[suite] = {"reuse": False, "reason": "GitHub lookup unavailable: " + failure_reason(error)}
    return decisions


def forced_full():
    if os.environ.get("ASAP_FORCE_FULL_CI") == "true":
        return True
    try:
        return test_selection.load()["decision"]["fresh"] is not False
    except Exception:
        return True


def api_client(config):
    require(os.environ.get("GITHUB_REPOSITORY") == config["repository"], "Wrong active repository")
    token = os.environ.get("GH_TOKEN", "")
    require(bool(token), "Read-only Actions token unavailable")
    return GitHub(config["repository"], token)


def plan(directory=DIRECTORY):
    config = fp.configuration()
    owned = validate_executor()
    suites = (owned,)
    (directory / "current.json").unlink(missing_ok=True)
    current = None
    decisions = {s: {"reuse": False, "reason": "Fingerprint unavailable; execute normal tests"} for s in suites}
    try:
        current = fp.calculate(os.environ["GITHUB_SHA"], config)
        write_json(directory / "current.json", current)
        if forced_full():
            decisions = lookup(None, config, current, True, suites)
            if os.environ.get("ASAP_FORCE_FULL_CI") != "true":
                reason = test_selection.load()["decision"]["reason"]
                for decision in decisions.values():
                    decision["reason"] = "Selection requires fresh execution: " + reason
        else:
            decisions = lookup(api_client(config), config, current, suites=suites)
    except Exception as error:
        # Deliberately omit exception messages: subprocess/API errors can contain sensitive data.
        print(f"::notice::Test reuse unavailable ({type(error).__name__}); normal tests required.")
    write_json(directory / "plan.json", {"run_id": os.environ["GITHUB_RUN_ID"],
               "attempt": os.environ["GITHUB_RUN_ATTEMPT"], "job": os.environ["GITHUB_JOB"], "decisions": decisions})
    outputs = {s: str(decisions[s]["reuse"]).lower() for s in suites}
    if current:
        outputs.update({s + "_fingerprint": current["suites"][s]["fingerprint"] for s in suites})
    outputs["schema"] = config["schema"]
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        for key, value in outputs.items():
            output.write(f"{key}={value}\n")
    for suite in suites:
        print(f"{LABELS[suite]}: {'HIT' if decisions[suite]['reuse'] else 'MISS'} — {decisions[suite]['reason']}")


def record(config, current, suite, results, directory=DIRECTORY):
    identity = current["suites"][suite]
    if identity["unavailable"]:
        return
    now = utcnow()
    manifest = {
        "schema": config["schema"], "kind": "executed", "outcome": "passed", "suite": suite,
        "repository": config["repository"], "repository_id": config["repository_id"],
        "workflow": config["workflow"], "job": config["executors"][suite]["name"],
        "run_id": int(os.environ["GITHUB_RUN_ID"]), "run_attempt": int(os.environ["GITHUB_RUN_ATTEMPT"]),
        "source_commit": current["commit"], "source_tree": current["tree"],
        "source_algorithm": fp.SOURCE_ALGORITHM,
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
    for suite in rows:
        row = rows[suite]
        source = row.get("source", {}).get("url")
        lines.append(f"| {LABELS[suite]} | {row['status']} | " + (f"[Original run]({source})" if source else "Current run") + " |")
    lines.append("")
    selection = current["suites"]["dotnet"]["selection"] if current else {}
    if current:
        lines.extend(["| .NET category | Discovered | Coverage |", "| --- | ---: | --- |"])
        for category, suite in (("core", "dotnet"), ("browser", "browser")):
            if suite not in rows:
                continue
            count = current["suites"][suite]["selection"]["expected"]
            status = rows[suite]["status"]
            lines.append(f"| {category} | {count} | {status} |")
        lines.extend(["", f"Selected non-browser population: {selection['expected']}; native test identities must match exactly.", ""])
    for suite in rows:
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
    owned = validate_executor()
    try:
        current = json.loads((directory / "current.json").read_bytes())
    except (OSError, ValueError):
        current = None
    try:
        planned = json.loads((directory / "plan.json").read_bytes())
    except (OSError, ValueError):
        planned = None
    selected = fp.selections(verify=True)
    checkout_valid = False
    try:
        commit, tree = fp.checkout(os.environ["GITHUB_SHA"])
        entries = fp.tracked_entries()
        contract = fp.full_ci_execution_contract((fp.ROOT / config["workflow"]).read_text(encoding="utf-8"))
        if current:
            environments, problems, _ = fp.environment(config, observe=False)
            require(current["commit"] == commit and current["tree"] == tree
                    and current.get("full_ci_execution_contract") == contract,
                    "Current source identity or executor contract changed")
            for suite in fp.SUITES:
                identity = current["suites"][suite]
                inputs = {key: identity[key] for key in ("schema", "suite", "source", "environment", "selection")}
                require(identity["fingerprint"] == fp.digest(inputs)
                        and identity["schema"] == config["schema"] and identity["suite"] == suite
                        and identity["source"] == fp.source_fingerprint(entries, suite, config, contract)
                        and identity["environment"] == fp.environment_fingerprint(identity["environment_inputs"])
                        and identity["environment"] == fp.environment_fingerprint(environments[suite])
                        and (identity["unavailable"] or not problems.get(suite))
                        and identity["selection"] == selected[suite],
                        "Current fingerprint inputs changed")
                # Fresh SQL bootstrap records the selected digest after planning.
                if suite in ("dotnet", "browser") and os.environ.get("ASAP_CI_SQL_IMAGE"):
                    identity["environment_inputs"]["sql_image"] = os.environ["ASAP_CI_SQL_IMAGE"]
        checkout_valid = True
    except Exception:
        pass
    rows, failures = {}, []
    validator = None
    for suite in (owned,):
        decision = (planned or {}).get("decisions", {}).get(suite, {"reuse": False, "reason": "No reuse plan"})
        rows[suite] = {"status": "FAILED", "reason": decision["reason"]}
        try:
            require(checkout_valid, "Current source/fingerprint identity could not be verified")
            outcome = os.environ.get(suite.upper() + "_OUTCOME")
            if decision["reuse"]:
                require(outcome == "skipped" and current is not None and not forced_full(), "Invalid skipped/reused result")
                require(planned["run_id"] == os.environ["GITHUB_RUN_ID"]
                        and planned["attempt"] == os.environ["GITHUB_RUN_ATTEMPT"]
                        and planned["job"] == os.environ["GITHUB_JOB"], "Reuse plan is from another execution")
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
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write(f"verified_{owned}=true\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("checkout", "plan", "verify", "sql-image"))
    args = parser.parse_args()
    if args.operation == "checkout":
        commit, tree = fp.checkout(os.environ["GITHUB_SHA"])
        print(f"Verified exact workflow source {commit}, tree {tree}")
    elif args.operation == "sql-image":
        print(fp.sql_image())
    elif args.operation == "plan":
        validate_executor()
        plan()
    else:
        validate_executor()
        verify()


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print("::error::Required test verification failed: " + failure_reason(error), file=sys.stderr)
        sys.exit(1)
