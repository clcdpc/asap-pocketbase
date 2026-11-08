"""Adversarial reuse contracts: temporary Git trees, native reports, and mocked Actions API."""

import base64
import copy
from datetime import datetime, timedelta, timezone
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

import fingerprints as fp
import probe_reuse
import reuse as reuse
import selection as test_selection


def success(**fields):
    return {"status": "completed", "conclusion": "success", **fields}


def results(selection):
    count = selection["expected"]
    return {"total": count, "executed": count, "passed": count, "failed": 0, "skipped": 0,
            "identities": selection.get("identities", [fp.digest(name) for name in selection.get("files", [str(i) for i in range(count)])])}


class Fixture:
    def __init__(self):
        self.now = datetime(2026, 10, 29, 12, tzinfo=timezone.utc)
        self.config = fp.configuration()
        self.workflow_bytes = (fp.ROOT / self.config["workflow"]).read_bytes()
        self.workflow_sha = hashlib.sha1(b"blob " + str(len(self.workflow_bytes)).encode()
                                         + b"\0" + self.workflow_bytes).hexdigest()
        self.entries = [["src/production.cs", "100644", "blob", "c" * 40],
                        [self.config["workflow"], "100644", "blob", self.workflow_sha]]
        self.contract = fp.full_ci_execution_contract(self.workflow_bytes.decode("utf-8"))
        self.current = {"commit": "a" * 40, "tree": "b" * 40,
                        "full_ci_execution_contract": self.contract, "suites": {}}
        self.selections = {}
        for suite in fp.SUITES:
            selection = {"filter": "actual " + suite + " selection", "minimum": 2, "expected": 2}
            if suite == "frontend":
                selection["files"] = ["a.test.js", "b.test.js"]
            else:
                selection["identities"] = sorted(fp.digest(str(i)) for i in range(2))
            self.selections[suite] = selection
            environment = {"epoch": "image-v1", "nuget": "resolved-content", "runtime": "actual-version", "suite": suite}
            inputs = {"schema": self.config["schema"], "suite": suite,
                      "source": fp.source_fingerprint(self.entries, suite, self.config, self.contract),
                      "environment": fp.environment_fingerprint(environment), "selection": selection}
            self.current["suites"][suite] = {**inputs, "fingerprint": fp.digest(inputs),
                                              "environment_inputs": environment, "unavailable": None}
        repository = {"id": self.config["repository_id"], "full_name": self.config["repository"]}
        self.run = success(id=7, run_attempt=2, head_sha=self.current["commit"], workflow_id=8,
                           path=self.config["workflow"], repository=copy.deepcopy(repository),
                           head_repository=copy.deepcopy(repository), event="push", head_branch="main",
                           created_at=reuse.iso(self.now - timedelta(hours=3)))
        self.job = success(id=9, run_id=7, head_sha=self.run["head_sha"], name=self.config["executors"]["dotnet"]["name"],
                           labels=self.config["runner_labels"], runner_group_name=self.config["runner_group"],
                           runner_group_id=self.config["runner_group_id"],
                           started_at=reuse.iso(self.now - timedelta(hours=2)),
                           completed_at=reuse.iso(self.now - timedelta(hours=1)), steps=[])
        self.gate = success(id=90, run_id=7, head_sha=self.run["head_sha"], name="Build, test and package")
        self.jobs = {}
        for index, suite in enumerate(fp.SUITES):
            job = self.job if suite == "dotnet" else copy.deepcopy(self.job)
            job.update(id=9 + index, name=self.config["executors"][suite]["name"], steps=[])
            for name in (reuse.STEPS[suite], "Verify required test suites", f"Upload original {suite} execution evidence"):
                job["steps"].append(success(name=name, started_at=reuse.iso(self.now - timedelta(minutes=120)),
                                             completed_at=reuse.iso(self.now - timedelta(minutes=95))))
            self.jobs[suite] = job
        for name in ("Verify publish artifacts", "Create exact test-IIS deployment ZIP",
                     "Validate test-IIS package without host mutation", "Upload exact test-IIS deployment artifact"):
            self.jobs["frontend"]["steps"].append(success(name=name))
        self.source = {"sha": self.current["commit"], "tree": {"sha": self.current["tree"]}, "parents": []}
        self.logs = (f"2026-10-29T10:00:00.000Z [command]/usr/bin/git log -1 --format=%H\n"
                     f"2026-10-29T10:00:00.001Z {self.current['commit']}\n").encode()
        self.prs = []
        self.attempts, self.attempt_jobs = {}, {}
        self.artifacts, self.manifests, self.reports, self.archives = {}, {}, {}, {}
        self.calls = []
        self.failure = None
        for index, suite in enumerate(fp.SUITES, start=10):
            identity = self.current["suites"][suite]
            executed = self.now - timedelta(minutes=90)
            report = results(self.selections[suite])
            self.reports[suite] = report
            self.manifests[suite] = {
                "schema": self.config["schema"], "kind": "executed", "outcome": "passed", "suite": suite,
                "repository": self.config["repository"], "repository_id": self.config["repository_id"],
                "workflow": self.config["workflow"], "job": self.config["executors"][suite]["name"], "run_id": 7, "run_attempt": 2,
                "source_commit": self.current["commit"], "source_tree": self.current["tree"],
                "source_algorithm": fp.SOURCE_ALGORITHM,
                "source_fingerprint": identity["source"], "fingerprint": identity["fingerprint"],
                "environment_fingerprint": identity["environment"], "environment_inputs": identity["environment_inputs"],
                "selection": identity["selection"], "counts": {k: report[k] for k in ("total", "executed", "passed", "failed", "skipped")},
                "results_sha256": fp.digest(report), "executed_at": reuse.iso(executed),
                "expires_at": reuse.iso(executed + timedelta(days=self.config["lifetime_days"])),
                "lifetime_days": self.config["lifetime_days"],
            }
            self.artifacts[suite] = {"id": index, "name": reuse.artifact_name(self.config, suite, identity["fingerprint"]),
                                     "expired": False, "expires_at": reuse.iso(self.now + timedelta(days=13)),
                                     "workflow_run": {"id": 7, "repository_id": repository["id"],
                                                      "head_repository_id": repository["id"], "head_sha": self.run["head_sha"]}}
            self.archive(suite)

    def archive(self, suite, extra=None):
        data = io.BytesIO()
        with zipfile.ZipFile(data, "w", zipfile.ZIP_DEFLATED) as zipped:
            zipped.writestr("manifest.json", fp.canonical(self.manifests[suite]))
            zipped.writestr("results.json", fp.canonical(self.reports[suite]))
            if extra:
                zipped.writestr(extra, "never execute or extract this")
        body = data.getvalue()
        self.archives[self.artifacts[suite]["id"]] = body
        self.artifacts[suite].update(size_in_bytes=len(body), digest="sha256:" + hashlib.sha256(body).hexdigest())

    def get(self, path):
        self.calls.append(path)
        if self.failure:
            raise self.failure
        if path == "/actions/workflows/dotnet.yml":
            return {"id": 8, "path": self.config["workflow"]}
        if path in ("/actions/runs/7", "/actions/runs/7/attempts/2"):
            return copy.deepcopy(self.attempts.get(2, self.run) if path.endswith("/attempts/2") else self.run)
        if path.startswith("/actions/runs/7/attempts/"):
            return copy.deepcopy(self.attempts[int(path.rsplit("/", 1)[1])])
        if path == f"/git/commits/{self.source['sha']}":
            return copy.deepcopy(self.source)
        if path == f"/git/blobs/{self.workflow_sha}":
            return {"sha": self.workflow_sha, "encoding": "base64", "size": len(self.workflow_bytes),
                    "content": base64.b64encode(self.workflow_bytes).decode("ascii")}
        if path == f"/git/trees/{self.source['tree']['sha']}?recursive=1":
            return {"sha": self.source["tree"]["sha"], "truncated": False,
                    "tree": [dict(zip(("path", "mode", "type", "sha"), e)) for e in self.entries]}
        if path.startswith("/actions/artifacts/"):
            return copy.deepcopy(next(a for a in self.artifacts.values() if a["id"] == int(path.split("/")[-1])))
        raise AssertionError("Unexpected API endpoint: " + path)

    def request(self, path, limit=None):
        self.calls.append(path)
        if self.failure:
            raise self.failure
        if path in (f"/actions/jobs/{job['id']}/logs" for job in self.jobs.values()):
            return self.logs
        if path.startswith("/actions/artifacts/") and path.endswith("/zip"):
            return self.archives[int(path.split("/")[-2])]
        raise AssertionError("Unexpected API download: " + path)

    def pages(self, path, key, **kwargs):
        self.calls.append(path)
        if self.failure:
            raise self.failure
        if path.startswith("/actions/workflows/dotnet.yml/runs?"):
            return iter([copy.deepcopy(self.run)])
        if path == "/actions/runs/7/attempts/2/jobs":
            return iter(copy.deepcopy([*self.jobs.values(), self.gate]))
        if path.startswith("/actions/runs/7/attempts/") and path.endswith("/jobs"):
            return iter(copy.deepcopy(self.attempt_jobs[int(path.split("/")[-2])]))
        if path == "/actions/artifacts":
            return iter(copy.deepcopy(list(self.artifacts.values())))
        if path.startswith("/actions/artifacts?name="):
            return iter(copy.deepcopy([a for a in self.artifacts.values() if a["name"] == path.partition("name=")[2]]))
        if path == "/actions/runs/7/artifacts":
            return iter(copy.deepcopy(list(self.artifacts.values())))
        if path.startswith("/commits/") and path.endswith("/pulls"):
            return iter(copy.deepcopy(self.prs))
        raise AssertionError("Unexpected paginated API endpoint: " + path)

    def lookup(self, forced=False):
        with mock.patch.object(reuse, "utcnow", return_value=self.now):
            return reuse.lookup(self, self.config, self.current, forced)

    def validate(self, suite="dotnet"):
        validator = reuse.Validator(self, self.config, self.current, now=self.now)
        return validator.validate(self.run, self.artifacts[suite], suite)


class GitIdentityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.email", "ci-fixture@example.org")
        self.git("config", "user.name", "CI fixture")
        self.git("config", "core.autocrlf", "false")
        (self.root / "code.txt").write_text("base\n")
        self.commit()

    def git(self, *arguments):
        return fp.command(["git", *arguments], self.root)

    def commit(self):
        self.git("add", ".")
        self.git("commit", "--allow-empty", "-qm", "deterministic fixture")
        return self.git("rev-parse", "HEAD")

    def test_different_commits_with_identical_trees_share_source_identity(self):
        first = self.git("rev-parse", "HEAD")
        tree = self.git("rev-parse", "HEAD^{tree}")
        entries = fp.tracked_entries(self.root)
        second = self.commit()
        self.assertNotEqual(first, second)
        self.assertEqual(fp.checkout(second, self.root)[1], tree)
        self.assertEqual(entries, fp.tracked_entries(self.root))

    def test_changed_file_contents_change_identity(self):
        before = fp.tracked_entries(self.root)
        (self.root / "code.txt").write_text("changed\n")
        self.commit()
        self.assertNotEqual(before, fp.tracked_entries(self.root))

    def test_checkout_must_be_exact_event_sha_and_clean(self):
        sha = self.git("rev-parse", "HEAD")
        with self.assertRaises(ValueError):
            fp.checkout("f" * 40, self.root)
        (self.root / "code.txt").write_text("uncommitted")
        with self.assertRaises(ValueError):
            fp.checkout(sha, self.root)

    def test_pr_merge_uses_effective_content_and_base_change_invalidates(self):
        self.git("checkout", "-qb", "pr")
        (self.root / "pr.txt").write_text("PR contents")
        head = self.commit()
        self.git("checkout", "-q", "main")
        (self.root / "base.txt").write_text("integration contents")
        self.commit()
        self.git("merge", "--no-ff", "-qm", "synthetic merge", head)
        first_merge = self.git("rev-parse", "HEAD")
        first_tree = fp.checkout(first_merge, self.root)[1]
        with self.assertRaises(ValueError):
            fp.checkout(head, self.root)
        self.git("checkout", "-qb", "new-base", "HEAD^1")
        (self.root / "base.txt").write_text("new integration contents")
        self.commit()
        self.git("merge", "--no-ff", "-qm", "new synthetic merge", head)
        second_merge = self.git("rev-parse", "HEAD")
        self.assertNotEqual(first_tree, fp.checkout(second_merge, self.root)[1])
        self.assertEqual(head, self.git("rev-parse", "HEAD^2"))

    def test_manual_full_baseline_reused_by_pr_with_ci_and_docs_through_final_verification(self):
        fixture = Fixture()
        self.git("checkout", "-qb", "pr")
        files = {fixture.config["workflow"]: fixture.workflow_bytes,
                 "scripts/ci/policy.py": b"# CI input\n", "docs/ci.md": b"Qualification documentation\n",
                 "tests/a.test.js": b"", "tests/b.test.js": b""}
        partitions = {key: {str(i): {"method": "Asap.Tests." + key + ".Case" + str(i), "name": str(i)}
                            for i in values} for key, values in (("core", (1, 2)), ("browser", (3, 4)))}
        reviewed = {"Browser": [value["method"] for value in partitions["browser"].values()]}
        files["scripts/ci/reviewed-test-categories.json"] = fp.canonical(reviewed)
        for path, contents in files.items():
            target = self.root / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(contents)
        head = self.commit()
        fixture.entries = fp.tracked_entries(self.root)
        fixture.source = {"sha": head, "tree": {"sha": self.git("rev-parse", "HEAD^{tree}")}, "parents": []}
        fixture.run.update(event="workflow_dispatch", head_sha=head, head_branch="pr")
        for job in fixture.jobs.values():
            job["head_sha"] = head
        fixture.gate["head_sha"] = head
        for artifact in fixture.artifacts.values():
            artifact["workflow_run"]["head_sha"] = head
        fixture.logs = ("2026-10-29T10:00:00.000Z [command]/usr/bin/git log -1 --format=%H\n"
                        f"2026-10-29T10:00:00.001Z {head}\n").encode()
        environments = {s: copy.deepcopy(fixture.current["suites"][s]["environment_inputs"]) for s in fp.SUITES}
        directory = self.root / ".artifacts/test-reuse"
        selection_directory = self.root / ".artifacts/test-selection"
        env = {**{key: os.environ[key] for key in ("PATH", "SystemRoot", "TEMP", "TMP") if key in os.environ},
               "GITHUB_EVENT_NAME": "workflow_dispatch", "GITHUB_SHA": head, "GITHUB_RUN_ID": "7", "GITHUB_JOB": "dotnet-qualification",
               "GITHUB_RUN_ATTEMPT": "2", "GITHUB_OUTPUT": str(self.root / "outputs"),
               "GITHUB_REPOSITORY": fixture.config["repository"], "GH_TOKEN": "fixture-token",
               "ASAP_FORCE_FULL_CI": "true"}
        load, calculate, checkout, entries = test_selection.load, fp.calculate, fp.checkout, fp.tracked_entries
        live_pr = {}

        def discover(expression=None, root=None):
            if expression is None:
                return {uid: value for tests in partitions.values() for uid, value in tests.items()}
            return copy.deepcopy(partitions[next(key for key, value in test_selection.FILTERS.items() if value == expression)])

        with mock.patch.dict(os.environ, env, clear=True), \
             mock.patch.object(fp, "ROOT", self.root), \
             mock.patch.object(fp, "calculate", side_effect=lambda expected, config: calculate(expected, config, self.root)), \
             mock.patch.object(fp, "checkout", side_effect=lambda expected, root=None: checkout(expected, self.root)), \
             mock.patch.object(fp, "tracked_entries", side_effect=lambda root=None: entries(self.root)), \
             mock.patch.object(fp, "environment", side_effect=lambda *args, **kwargs: (copy.deepcopy(environments), {}, fp.SQL_TAG)), \
             mock.patch.object(test_selection, "load", side_effect=lambda *args, verify=False: load(
                 self.root, verify=verify, directory=selection_directory)), \
             mock.patch.object(test_selection, "discover", side_effect=discover), \
             mock.patch.object(reuse, "GitHub") as github, \
             mock.patch.object(reuse, "api_client", return_value=fixture), \
             mock.patch.object(reuse, "utcnow", return_value=fixture.now), \
             mock.patch("sys.stdout", new_callable=io.StringIO):
            # The only substituted boundaries are native discovery, installed
            # tools and GitHub. Selection, Git identity, planning, recording and
            # final provenance verification use their real implementations.
            test_selection.prepare(self.root, selection_directory)
            baseline = fp.calculate(head, fixture.config)
            for suite in fp.SUITES:
                with mock.patch.object(reuse, "utcnow", return_value=fixture.now - timedelta(minutes=90)):
                    reuse.record(fixture.config, baseline, suite, results(baseline["suites"][suite]["selection"]),
                                 self.root / "origin")
                fixture.manifests[suite] = json.loads((self.root / "origin/evidence" / suite / "manifest.json").read_bytes())
                fixture.reports[suite] = json.loads((self.root / "origin/evidence" / suite / "results.json").read_bytes())
                fixture.artifacts[suite]["name"] = reuse.artifact_name(fixture.config, suite, baseline["suites"][suite]["fingerprint"])
                fixture.archive(suite)

            self.git("checkout", "-q", "main")
            base = self.git("rev-parse", "HEAD")
            self.git("update-ref", "refs/remotes/origin/main", base)
            self.git("merge", "--no-ff", "-qm", "synthetic merge", head)
            tested = self.git("rev-parse", "HEAD")
            self.assertNotEqual(tested, head)
            self.assertEqual(self.git("rev-parse", "HEAD^{tree}"), baseline["tree"])
            repo = {"full_name": fixture.config["repository"]}
            pr = {"number": 1, "state": "open", "draft": False, "author_association": "COLLABORATOR",
                  "head": {"sha": head, "repo": repo}, "base": {"sha": base, "ref": "main", "repo": repo}}
            live_pr = copy.deepcopy(pr)
            github.return_value.get.side_effect = lambda path: copy.deepcopy(live_pr)
            event_path = self.root / "event.json"
            event_path.write_bytes(fp.canonical({"pull_request": pr}))
            os.environ.update(GITHUB_EVENT_NAME="pull_request", GITHUB_SHA=tested, GITHUB_RUN_ID="99",
                              GITHUB_RUN_ATTEMPT="1", GITHUB_EVENT_PATH=str(event_path), ASAP_FORCE_FULL_CI="false",
                              GITHUB_REF="refs/pull/1/merge", GITHUB_REF_TYPE="branch",
                              **{s.upper() + "_OUTCOME": "skipped" for s in fp.SUITES})
            test_selection.prepare(self.root, selection_directory)
            selected = load(self.root, verify=True, directory=selection_directory)
            self.assertFalse(selected["decision"]["fresh"])
            self.assertIn("docs/ci.md", selected["decision"]["base_paths"])
            self.assertIn(fixture.config["workflow"], selected["decision"]["base_paths"])
            self.assertIn("scripts/ci/policy.py", selected["decision"]["base_paths"])
            # A later throwaway VM can differ while effective inputs match.
            for value in environments.values():
                value.update(runtime="another-installed-version", kernel="another-kernel", native_packages="new-packages",
                             sql_image="another-sql-image", docker="another-docker")
            reuse.plan(directory)
            matching_plan = json.loads((directory / "plan.json").read_bytes())
            self.assertTrue(all(d["reuse"] for d in matching_plan["decisions"].values()), matching_plan)
            fixture.calls.clear()
            reuse.verify(directory)
            rows = json.loads((directory / "verification.json").read_bytes())
            self.assertTrue(all(row["status"] == "REUSED" for row in rows.values()))
            self.assertFalse((directory / "evidence").exists())
            self.assertIn("/actions/artifacts/10/zip", fixture.calls)

            # Keep the literal tested commit fixed while changing the trigger.
            tag = "v1.0.0-test.1"
            self.git("tag", "-a", "-m", "same tested commit", tag, tested)
            tag_ref = "refs/tags/" + tag
            for event_name, ref, event in (
                    ("workflow_dispatch", "refs/heads/pr", {}),
                    ("workflow_dispatch", tag_ref, {}),
                    ("push", "refs/heads/integration", {"ref": "refs/heads/integration", "before": head,
                                                        "after": tested, "deleted": False, "forced": False}),
                    ("push", tag_ref, {"ref": tag_ref, "after": self.git("rev-parse", tag_ref),
                                       "deleted": False, "forced": False}),
                    ("release", tag_ref, {"release": {"tag_name": tag}}),
                    ("deployment", "", {"deployment": {"sha": tested}}),
                    ("deployment_status", tag_ref, {"deployment": {"sha": tested}})):
                with self.subTest(trigger=event_name, ref=ref):
                    os.environ.update(GITHUB_EVENT_NAME=event_name, GITHUB_REF=ref, ASAP_DEPLOY_TEST_IIS="true",
                                      GITHUB_REF_TYPE="tag" if ref.startswith("refs/tags/") else "branch")
                    event_path.write_bytes(fp.canonical(event))
                    test_selection.prepare(self.root, selection_directory)
                    self.assertFalse(load(self.root, directory=selection_directory)["decision"]["fresh"])
                    reuse.plan(directory)
                    self.assertTrue(all(d["reuse"] for d in json.loads((directory / "plan.json").read_bytes())["decisions"].values()))
                    reuse.verify(directory)
                    self.assertTrue(all(row["status"] == "REUSED" for row in json.loads((directory / "verification.json").read_bytes()).values()))
            os.environ.update(GITHUB_EVENT_NAME="pull_request", GITHUB_REF="refs/pull/1/merge",
                              GITHUB_REF_TYPE="branch", ASAP_DEPLOY_TEST_IIS="false")
            event_path.write_bytes(fp.canonical({"pull_request": pr}))

            original_environments, original_partitions = copy.deepcopy(environments), copy.deepcopy(partitions)
            original_archives = copy.deepcopy(fixture.archives)
            for change in ("force", "invalid-metadata", "changed-dependency", "incomplete-population", "corrupt-evidence", "changed-ci-input"):
                with self.subTest(change=change):
                    environments, partitions = copy.deepcopy(original_environments), copy.deepcopy(original_partitions)
                    fixture.archives = copy.deepcopy(original_archives)
                    live_pr = copy.deepcopy(pr)
                    os.environ["ASAP_FORCE_FULL_CI"] = "false"
                    if change == "force":
                        os.environ["ASAP_FORCE_FULL_CI"] = "true"
                    elif change == "invalid-metadata":
                        live_pr["head"]["sha"] = "f" * 40
                    elif change == "changed-dependency":
                        environments["dotnet"]["nuget"] = "different-resolved-package-content"
                    elif change == "incomplete-population":
                        partitions["core"]["5"] = {"method": "Asap.Tests.core.NewCase", "name": "NewCase"}
                    elif change == "corrupt-evidence":
                        fixture.archives[10] = b"corrupt archive"
                    else:
                        self.git("checkout", "-qb", "changed-input", head)
                        (self.root / "scripts/ci/policy.py").write_text("# Changed CI input\n")
                        self.git("add", "scripts/ci/policy.py")
                        self.git("commit", "-qm", "changed CI input")
                        pr["head"]["sha"] = self.git("rev-parse", "HEAD")
                        self.git("checkout", "-q", "--detach", base)
                        self.git("merge", "--no-ff", "-qm", "changed synthetic merge", pr["head"]["sha"])
                        os.environ["GITHUB_SHA"] = self.git("rev-parse", "HEAD")
                        live_pr = copy.deepcopy(pr)
                        event_path.write_bytes(fp.canonical({"pull_request": pr}))
                    test_selection.prepare(self.root, selection_directory)
                    fixture.calls.clear()
                    reuse.plan(directory)
                    decisions = json.loads((directory / "plan.json").read_bytes())["decisions"]
                    self.assertFalse(decisions["dotnet"]["reuse"])
                    if change in ("force", "invalid-metadata"):
                        self.assertEqual(fixture.calls, [])
                    # A previously valid plan must not mask the later failure.
                    reuse.write_json(directory / "plan.json", matching_plan)
                    with self.assertRaises(reuse.EvidenceError):
                        reuse.verify(directory)


class DependencyTests(unittest.TestCase):
    def test_frontend_only_contract_edits_preserve_native_sources_but_still_run_frontend(self):
        config = fp.configuration()
        for path in (".github/workflows/issue-triage.yml", "tests/issue_triage_workflow.test.js",
                     "tests/settings_domain_editor.test.js", "scripts/ci/reviewed-test-categories.json"):
            for suite in fp.SUITES:
                before = fp.source_fingerprint([[path, "100644", "blob", "a"]], suite, config)
                after = fp.source_fingerprint([[path, "100644", "blob", "b"]], suite, config)
                self.assertEqual(before != after, suite == "frontend", (path, suite))

    def test_native_exclusions_have_no_cross_suite_consumers(self):
        config = fp.configuration()
        names = {path.name for path in (fp.ROOT / "tests").glob("*.test.js")
                 if not {"dotnet", "browser"}.intersection(fp.source_suites("tests/" + path.name, config))}
        names.add("run_all.js")
        consumers = [*(fp.ROOT / "tests/Asap.Tests").rglob("*.cs"),
                     *(fp.ROOT / "tests/browser").rglob("*.cjs"),
                     *fp.ROOT.rglob("*.csproj"), *fp.ROOT.rglob("*.targets"), *fp.ROOT.glob("*.props")]
        for path in consumers:
            if any(part in ("node_modules", "bin", "obj") for part in path.parts):
                continue
            contents = path.read_text(encoding="utf-8-sig")
            self.assertFalse(any(name in contents for name in names), "Cross-suite consumer: " + str(path))
        for path in ("tests/new-shared-helper.js", "tests/run_all.js",
                     "tests/ci_workflow_contract.test.js", "tests/test_iis_deployment_contract.test.js"):
            for suite in fp.SUITES:
                self.assertNotEqual(fp.source_fingerprint([], suite, config),
                                    fp.source_fingerprint([[path, "100644", "blob", "a"]], suite, config))

    def test_authored_frontend_assets_allow_backend_reuse_without_file_inventory(self):
        config = fp.configuration()
        for path in ("src/Asap.Web/Frontend/staff/app.js", "src/Asap.Web/Frontend/staff/styles.css",
                     "src/Asap.Web/Frontend/new-module.js"):
            before, after = [[path, "100644", "blob", "a"]], [[path, "100644", "blob", "b"]]
            self.assertEqual(fp.source_fingerprint(before, "dotnet", config), fp.source_fingerprint(after, "dotnet", config))
            for suite in ("browser", "frontend"):
                self.assertNotEqual(fp.source_fingerprint(before, suite, config), fp.source_fingerprint(after, suite, config))
        for path in ("src/Asap.Web/Frontend/vendor/changed.js", "src/Asap.Web/Frontend/staff/index.html",
                     "src/Asap.Web/Build/FrontendAssets.targets", "src/Asap.Web/Features/Patron/PatronSessionService.cs",
                     "src/Asap.Web/Infrastructure/Data/AsapDbContext.cs"):
            self.assertNotEqual(fp.source_fingerprint([[path,"100644","blob","a"]], "dotnet", config),
                                fp.source_fingerprint([[path,"100644","blob","b"]], "dotnet", config))

    def test_unknown_and_shared_dependencies_invalidate_every_suite(self):
        config = fp.configuration()
        for path in ("unknown-new-file", "src/Asap.Web/Frontend/staff/index.html", "src/Shared/SecurityContract.cs",
                     "database/Asap.Database/Scripts/PostDeployment.sql", "tests/Asap.Tests/Sql/DatabaseBaselineTests.cs",
                     "dev/review/src/Asap.ReviewInit/LocalReviewer.sql", "dev/review/src/Asap.ReviewProviders/ReviewProviders.cs",
                     "global.json", "package-lock.json",
                     "Directory.Packages.props", "new-root/runtime-input.md", "tests/new-fixtures/expected.txt"):
            for suite in fp.SUITES:
                with self.subTest(path=path, suite=suite):
                    before = [[path, "100644", "blob", "a" * 40]]
                    after = [[path, "100644", "blob", "b" * 40]]
                    self.assertNotEqual(fp.source_fingerprint(before, suite, config), fp.source_fingerprint(after, suite, config))

    def test_ci_policy_and_selection_changes_invalidate_evidence(self):
        config = fp.configuration()
        for path in (".github/workflows/dotnet.yml", "scripts/ci/selection.py",
                     "scripts/ci/reuse.py", "scripts/ci/fingerprints.py", "tests/Asap.Tests/TestCategories.cs"):
            for suite in fp.SUITES:
                with self.subTest(path=path, suite=suite):
                    self.assertNotEqual(fp.source_fingerprint([[path, "100644", "blob", "a"]], suite, config),
                                        fp.source_fingerprint([[path, "100644", "blob", "b"]], suite, config))
        self.assertNotEqual(fp.source_fingerprint([], "dotnet", config),
                            fp.source_fingerprint([["scripts/ci/new-policy.py", "100644", "blob", "b"]], "dotnet", config))

    def test_every_workflow_executor_and_gate_edit_invalidates_evidence(self):
        workflow = (fp.ROOT / ".github/workflows/dotnet.yml").read_text()
        for before, after in (("timeout-minutes: 120", "timeout-minutes: 121"),
                              ("name: Run browser accessibility tests", "name: Changed browser execution"),
                              ("CI_FULL_RESULT", "CHANGED_FULL_RESULT")):
            self.assertNotEqual(fp.full_ci_execution_contract(workflow),
                                fp.full_ci_execution_contract(workflow.replace(before, after)))


    def test_exact_acceptance_screenshot_files_do_not_invalidate_sql(self):
        config = fp.configuration()
        for path in ("tests/acceptance/run.cjs", "tests/acceptance/helpers/reporting.cjs", "tests/acceptance/helpers/accessibility.cjs"):
            self.assertEqual(fp.source_fingerprint([[path, "100644", "blob", "a"]], "dotnet", config),
                             fp.source_fingerprint([[path, "100644", "blob", "b"]], "dotnet", config))
        path = "tests/fixtures/new-shared-fixture.cjs"
        self.assertNotEqual(fp.source_fingerprint([], "dotnet", config),
                            fp.source_fingerprint([[path, "100644", "blob", "b"]], "dotnet", config))

    def test_documentation_is_scoped_and_runtime_or_fixture_markdown_stays_included(self):
        config = fp.configuration()
        for path in ("README.md", "scripts/ci/README.md", ".agents/skills/new/SKILL.md",
                     "docs/implementation/test-iis-activation.md"):
            for suite in fp.SUITES:
                self.assertEqual(fp.source_fingerprint([], suite, config),
                                 fp.source_fingerprint([[path, "100644", "blob", "b"]], suite, config))
        for path in ("src/Asap.Web/Frontend/vendor/font-awesome/4.7.0/README.md",
                     "tests/fixtures/expected-email.md", "tests/fixtures/expected-email.txt",
                     "src/Asap.Web/Features/Templates/example.md", "scripts/ci/fixtures/README.md"):
            for suite in fp.SUITES:
                self.assertNotEqual(fp.source_fingerprint([], suite, config),
                                    fp.source_fingerprint([[path, "100644", "blob", "b"]], suite, config))
        for path in ("tests/new.test.js", "tests/new-fixture.ps1"):
            self.assertEqual(set(fp.source_suites(path, config)), {"frontend"})

    def test_directory_rules_cover_new_files_and_do_not_match_sibling_names(self):
        config = fp.configuration()
        for path in ("new-top-level/runtime.json", "src/new-feature/code.cs", "database/new-schema.sql",
                     "tests/new-shared-data/input.txt", "Directory.Build.targets", "tests/nested/example.test.js"):
            self.assertEqual(set(fp.source_suites(path, config)), set(fp.SUITES), path)
        self.assertEqual(set(fp.source_suites("tests/browser/new-journey.cjs", config)), {"browser"})
        self.assertEqual(set(fp.source_suites("tests/browserish/shared.cs", config)), set(fp.SUITES))

    def test_audited_browser_script_only_invalidates_browser(self):
        config = fp.configuration()
        path = "tests/browser/staff-presentation.cjs"
        for suite in fp.SUITES:
            changed = (fp.source_fingerprint([[path, "100644", "blob", "a"]], suite, config)
                       != fp.source_fingerprint([[path, "100644", "blob", "b"]], suite, config))
            self.assertEqual(changed, suite == "browser")

    def test_file_modes_and_deleted_dependencies_invalidate(self):
        config = fp.configuration()
        for suite in fp.SUITES:
            original = [["build.sh", "100644", "blob", "a"]]
            self.assertNotEqual(fp.source_fingerprint(original, suite, config), fp.source_fingerprint([], suite, config))
            self.assertNotEqual(fp.source_fingerprint(original, suite, config),
                                fp.source_fingerprint([["build.sh", "100755", "blob", "a"]], suite, config))

    def test_environment_count_filter_and_schema_changes_invalidate(self):
        fixture = Fixture()
        original = fixture.current["suites"]["dotnet"]
        for key, value in (("schema", 99), ("environment", "new-runtime-or-epoch"),
                           ("selection", {**original["selection"], "expected": 3}),
                           ("selection", {**original["selection"], "minimum": 3}),
                           ("selection", {**original["selection"], "filter": "new-filter"})):
            changed = {k: original[k] for k in ("schema", "suite", "source", "environment", "selection")}
            changed[key] = value
            self.assertNotEqual(fp.digest(changed), original["fingerprint"])

    def test_selection_must_use_verified_authoritative_population(self):
        fixture = Fixture()
        with mock.patch.object(test_selection, "load", return_value={"suites": fixture.selections}) as load:
            self.assertEqual(fp.selections(verify=True), fixture.selections)
            load.assert_called_once_with(fp.ROOT, verify=True)

    def test_discovery_count_changes_require_new_evidence(self):
        fixture = Fixture()
        fixture.current["suites"]["dotnet"]["selection"]["minimum"] = 3
        fixture.current["suites"]["dotnet"]["selection"]["expected"] = 3
        self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

class ProvenanceTests(unittest.TestCase):
    def test_literal_commit_from_trusted_pr_is_reusable_without_synthetic_parent_assumptions(self):
        fixture = Fixture()
        fixture.run["event"] = "pull_request"
        fixture.prs = [{"head": {"repo": fixture.run["head_repository"]},
                        "base": {"repo": fixture.run["repository"], "ref": "main"},
                        "author_association": "MEMBER"}]
        self.assertTrue(all(d["reuse"] for d in fixture.lookup().values()))
        fixture.prs[0]["author_association"] = "CONTRIBUTOR"
        self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))

    def test_originating_event_alone_never_rejects_matching_same_commit_evidence(self):
        for event in ("push", "workflow_dispatch", "release", "deployment", "deployment_status", "schedule"):
            fixture = Fixture()
            fixture.run["event"] = event
            with self.subTest(event=event):
                self.assertTrue(all(d["reuse"] for d in fixture.lookup().values()))
                fixture.run["head_repository"]["id"] = 42
                self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))

    def test_different_category_selections_cannot_share_execution_evidence(self):
        for current_categories, evidence_categories in ((["core"], ["unrecognized"]),
                                                        (["unrecognized"], ["core"])):
            fixture = Fixture()
            fixture.current["suites"]["dotnet"]["selection"]["categories"] = current_categories
            manifest = fixture.manifests["dotnet"]
            manifest["selection"] = {**manifest["selection"], "categories": evidence_categories}
            fixture.archive("dotnet")
            self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

    def test_same_count_wrong_native_test_identity_is_not_execution_evidence(self):
        fixture = Fixture()
        fixture.reports["dotnet"]["identities"] = sorted([fp.digest("substituted"), fp.digest("1")])
        fixture.manifests["dotnet"]["results_sha256"] = fp.digest(fixture.reports["dotnet"])
        fixture.archive("dotnet")
        self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

    def test_legacy_schema_or_algorithm_cannot_authorize_new_selection(self):
        fixture = Fixture()
        for suite in fp.SUITES:
            manifest = fixture.manifests[suite]
            manifest.pop("source_algorithm")
            manifest["schema"] = 1
            fixture.archive(suite)
        self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))

    def test_modified_origin_workflow_blob_is_rejected(self):
        fixture = Fixture()
        fixture.workflow_bytes = b"forged content"
        self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

    def test_identical_successful_original_run_hits_all_suites(self):
        fixture = Fixture()
        decisions = fixture.lookup()
        self.assertTrue(all(d["reuse"] for d in decisions.values()))
        self.assertEqual(decisions["dotnet"]["source"]["saved_seconds"], 1500)
        self.assertEqual(fixture.calls.count("/actions/jobs/9/logs"), 1)

    def test_each_suite_requires_its_own_original_success_in_its_owning_job(self):
        for suite in fp.SUITES:
            for name in (reuse.STEPS[suite], f"Upload original {suite} execution evidence"):
                for outcome in ("failure", "cancelled", "skipped", "missing"):
                    fixture = Fixture()
                    job = fixture.jobs[suite]
                    step = next(s for s in job["steps"] if s["name"] == name)
                    if outcome == "missing":
                        job["steps"].remove(step)
                    else:
                        step["conclusion"] = outcome
                    decisions = fixture.lookup()
                    with self.subTest(suite=suite, name=name, outcome=outcome):
                        self.assertEqual({s for s, d in decisions.items() if d["reuse"]}, set(fp.SUITES) - {suite})

    def test_two_job_schema_and_stale_browser_job_provenance_are_incompatible(self):
        fixture = Fixture()
        for suite in fp.SUITES:
            fixture.manifests[suite]["schema"] = 5
            fixture.archive(suite)
        self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))
        fixture = Fixture()
        fixture.manifests["browser"]["job"] = "Browser qualification"
        fixture.archive("browser")
        decisions = fixture.lookup()
        self.assertFalse(decisions["browser"]["reuse"])
        self.assertTrue(decisions["dotnet"]["reuse"] and decisions["frontend"]["reuse"])

    def test_every_required_owner_must_be_present_unique_and_successful(self):
        for suite in fp.SUITES:
            for failure in ("failure", "cancelled", "skipped", "missing", "duplicate", "wrong-head"):
                fixture = Fixture()
                jobs = [*fixture.jobs.values(), fixture.gate]
                target = fixture.jobs[suite]
                if failure == "missing":
                    jobs.remove(target)
                elif failure == "duplicate":
                    jobs.append(copy.deepcopy(target))
                elif failure == "wrong-head":
                    target["head_sha"] = "f" * 40
                else:
                    target["conclusion"] = failure
                pages = fixture.pages
                with self.subTest(suite=suite, failure=failure), mock.patch.object(fixture, "pages", side_effect=lambda path, key, **kwargs:
                        iter(copy.deepcopy(jobs)) if path == "/actions/runs/7/attempts/2/jobs" else pages(path, key, **kwargs)):
                    self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))

    def test_each_package_step_is_required_even_when_all_jobs_claim_success(self):
        for name in ("Verify publish artifacts", "Create exact test-IIS deployment ZIP",
                     "Validate test-IIS package without host mutation", "Upload exact test-IIS deployment artifact"):
            for outcome in ("failure", "cancelled", "skipped", "missing"):
                fixture = Fixture()
                steps = fixture.jobs["frontend"]["steps"]
                step = next(step for step in steps if step["name"] == name)
                if outcome == "missing":
                    steps.remove(step)
                else:
                    step["conclusion"] = outcome
                with self.subTest(name=name, outcome=outcome):
                    self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))

    def test_another_owners_manifest_or_checkout_cannot_authorize_a_suite(self):
        for suite in fp.SUITES:
            fixture = Fixture()
            other = next(s for s in fp.SUITES if s != suite)
            fixture.manifests[suite]["job"] = fixture.config["executors"][other]["name"]
            fixture.archive(suite)
            with self.subTest(suite=suite):
                self.assertFalse(fixture.lookup()[suite]["reuse"])
            fixture = Fixture()
            request = fixture.request
            job_path = f"/actions/jobs/{fixture.jobs[suite]['id']}/logs"
            with mock.patch.object(fixture, "request", side_effect=lambda path, **kwargs:
                    fixture.logs.replace(b"a" * 40, b"f" * 40) if path == job_path else request(path, **kwargs)):
                self.assertFalse(fixture.lookup()[suite]["reuse"])

    def test_shared_job_cannot_substitute_another_suites_archive(self):
        for suite in fp.SUITES:
            fixture = Fixture()
            other = next(s for s in fp.SUITES if s != suite)
            artifact = fixture.artifacts[suite]
            body = fixture.archives[fixture.artifacts[other]["id"]]
            fixture.archives[artifact["id"]] = body
            artifact.update(size_in_bytes=len(body), digest="sha256:" + hashlib.sha256(body).hexdigest())
            decisions = fixture.lookup()
            with self.subTest(suite=suite):
                self.assertFalse(decisions[suite]["reuse"])

    def test_incomplete_qualification_is_rejected_even_when_suites_have_passing_artifacts(self):
        for name in ("job", "gate"):
            for status in ("queued", "in_progress", "pending"):
                fixture = Fixture()
                getattr(fixture, name).update(status=status, conclusion=None)
                fixture.run.update(status="in_progress", conclusion=None)
                with self.subTest(job=name, status=status):
                    self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))

    def test_active_downstream_work_does_not_invalidate_completed_qualification(self):
        for status in ("queued", "in_progress", "waiting", "completed"):
            fixture = Fixture()
            fixture.run.update(status=status, conclusion=None)
            with self.subTest(status=status):
                self.assertTrue(all(d["reuse"] for d in fixture.lookup().values()))

    def test_prior_passing_tests_with_later_failed_packaging_are_rejected(self):
        fixture = Fixture()
        fixture.run["conclusion"] = "failure"
        fixture.job["conclusion"] = "failure"
        self.assertTrue(all(successful["conclusion"] == "success" for successful in fixture.job["steps"]))
        self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))

    def test_downstream_deployment_failure_preserves_successful_qualification(self):
        for conclusion in ("failure", "timed_out", "cancelled"):
            fixture = Fixture()
            fixture.run["conclusion"] = conclusion
            self.assertTrue(all(d["reuse"] for d in fixture.lookup().values()))
        for outcome in ("failure", "cancelled", "skipped"):
            fixture = Fixture()
            fixture.run["conclusion"] = "failure"
            fixture.gate["conclusion"] = outcome
            self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))

    def test_deployment_only_retry_preserves_original_attempt_but_failed_qualification_retry_does_not(self):
        fixture = Fixture()
        original = copy.deepcopy(fixture.run)
        original["conclusion"] = "failure"
        fixture.run["run_attempt"] = 3
        fixture.attempts[2] = original
        fixture.attempt_jobs[3] = [success(name="Deploy exact artifact to test IIS", run_id=7,
                                          head_sha=fixture.run["head_sha"])]
        # The historical endpoint returns attempt 2, not the latest run object.
        original_get = fixture.get
        with mock.patch.object(fixture, "get", side_effect=lambda path: copy.deepcopy(original)
                               if path == "/actions/runs/7/attempts/2" else original_get(path)):
            decisions = fixture.lookup()
            self.assertTrue(all(d["reuse"] and d["source"]["attempt"] == 2 for d in decisions.values()))
            for name in (*(owner["name"] for owner in fp.EXECUTORS.values()), "Build, test and package"):
                for outcome in ("failure", "cancelled", "skipped"):
                    fixture.attempt_jobs[3] = [success(name=name, run_id=7, head_sha=fixture.run["head_sha"], conclusion=outcome)]
                    self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()), (name, outcome))
            # A later deployment-only attempt cannot hide an intervening failed CI.
            fixture.run["run_attempt"] = 4
            fixture.attempt_jobs[4] = []
            self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))

    def test_active_deployment_only_retry_is_distinguished_from_unscheduled_or_active_qualification(self):
        fixture = Fixture()
        fixture.attempts[2] = copy.deepcopy(fixture.run)
        fixture.run.update(run_attempt=3, status="in_progress", conclusion=None)
        deployment = success(name="Deploy exact artifact to test IIS", run_id=7,
                             head_sha=fixture.run["head_sha"], status="in_progress", conclusion=None)
        fixture.attempt_jobs[3] = [deployment]
        self.assertTrue(all(d["reuse"] for d in fixture.lookup().values()))
        for jobs in ([], [success(name="Resolve validation policy", run_id=7, head_sha=fixture.run["head_sha"])],
                     [success(name=fixture.config["executors"]["dotnet"]["name"], run_id=7, head_sha=fixture.run["head_sha"],
                              status="in_progress", conclusion=None)],
                     [copy.deepcopy(fixture.job)],
                     [copy.deepcopy(fixture.job), {**fixture.gate, "status": "queued", "conclusion": None}]):
            fixture.attempt_jobs[3] = jobs
            self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()), jobs)
        fixture.attempt_jobs[3] = copy.deepcopy([*fixture.jobs.values(), fixture.gate, deployment])
        self.assertTrue(all(d["reuse"] for d in fixture.lookup().values()))

    def test_wrong_repository_workflow_job_or_runner_is_rejected(self):
        for target, key, value in (("repository", "id", 42), ("head_repository", "id", 42),
                                   ("run", "workflow_id", 42), ("run", "path", ".github/workflows/other.yml"),
                                   ("job", "name", "Other job"), ("job", "runner_group_name", "other"),
                                   ("job", "runner_group_id", 1), ("job", "labels", ["clc-linux"]),
                                   ("job", "labels", ["ubuntu-latest"]), ("job", "head_sha", "f" * 40)):
            fixture = Fixture()
            record = fixture.run[target] if target in ("repository", "head_repository") else getattr(fixture, target)
            record[key] = value
            with self.subTest(target=target, key=key):
                self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

    def test_draft_validation_and_reporting_gate_cannot_supply_full_suite_evidence(self):
        for job_name in ("Draft validation", "Draft CI result", "Build, test and package", "Browser qualification"):
            fixture = Fixture()
            # Even a forged complete artifact and successful workflow cannot
            # substitute a draft/reporting job for the original full executor.
            fixture.job["name"] = job_name
            with self.subTest(job_name=job_name):
                self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))

    def test_skipped_failed_or_missing_original_step_is_rejected(self):
        for name in (reuse.STEPS["dotnet"], "Verify required test suites", "Upload original dotnet execution evidence"):
            for conclusion in ("skipped", "failure", None):
                fixture = Fixture()
                next(s for s in fixture.job["steps"] if s["name"] == name)["conclusion"] = conclusion
                with self.subTest(name=name, conclusion=conclusion):
                    self.assertFalse(fixture.lookup()["dotnet"]["reuse"])
        fixture = Fixture()
        fixture.job["steps"] = []
        self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

    def test_manifest_cannot_claim_other_suite_count_environment_or_origin(self):
        changes = {"schema": 99, "suite": "browser", "kind": "reused", "outcome": "skipped",
                   "run_id": 77, "run_attempt": 1, "source_commit": "f" * 40, "source_tree": "f" * 40,
                   "repository": "other/repository", "workflow": "other.yml", "job": "other job",
                   "fingerprint": "forged", "environment_fingerprint": "forged", "environment_inputs": {},
                   "source_fingerprint": "forged", "selection": {}, "counts": {}, "results_sha256": "forged"}
        for key, value in [*changes.items(), ("schema", True), ("run_attempt", True)]:
            fixture = Fixture()
            fixture.manifests["dotnet"][key] = value
            fixture.archive("dotnet")
            with self.subTest(key=key):
                self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

    def test_expiration_and_missing_integrity_metadata_are_misses(self):
        for key, value in (("expired", True), ("expires_at", "2020-01-01T00:00:00Z"),
                           ("digest", None), ("digest", "sha256:" + "0" * 64),
                           ("name", "asap-test-evidence-v1-dotnet-7-1"), ("size_in_bytes", 0)):
            fixture = Fixture()
            fixture.artifacts["dotnet"][key] = value
            with self.subTest(key=key):
                self.assertFalse(fixture.lookup()["dotnet"]["reuse"])
        fixture = Fixture()
        fixture.run["created_at"] = reuse.iso(fixture.now + timedelta(days=1))
        self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

    def test_fresh_original_attempt_of_old_run_is_valid_until_its_execution_expires(self):
        fixture = Fixture()
        fixture.run["created_at"] = reuse.iso(fixture.now - timedelta(days=30))
        self.assertTrue(all(d["reuse"] for d in fixture.lookup().values()))
        fixture.now += timedelta(days=15)
        self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))

    def test_cached_origin_context_preserves_deployment_progress_and_rechecks_advanced_attempt(self):
        fixture = Fixture()
        validator = reuse.Validator(fixture, fixture.config, fixture.current, now=fixture.now)
        validator.validate(fixture.run, fixture.artifacts["dotnet"], "dotnet")
        fixture.run["status"] = "in_progress"
        validator.validate(fixture.run, fixture.artifacts["browser"], "browser")
        fixture.run["status"] = "completed"
        fixture.attempts[2] = copy.deepcopy(fixture.run)
        fixture.run["run_attempt"] = 3
        fixture.attempt_jobs[3] = [success(name=fixture.config["executors"]["dotnet"]["name"], run_id=7,
                                         head_sha=fixture.run["head_sha"], conclusion="failure")]
        with self.assertRaises(reuse.EvidenceError):
            validator.validate(fixture.run, fixture.artifacts["browser"], "browser")

    def test_forged_execution_timestamp_and_expiration_are_misses(self):
        for key, value in (("executed_at", "2020-01-01T00:00:00Z"), ("executed_at", "2099-01-01T00:00:00Z"),
                           ("expires_at", "2099-01-01T00:00:00Z"), ("lifetime_days", 365)):
            fixture = Fixture()
            fixture.manifests["dotnet"][key] = value
            fixture.archive("dotnet")
            with self.subTest(key=key):
                self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

    def test_artifact_origin_must_match_repository_head_run_and_attempt(self):
        for key in ("id", "repository_id", "head_repository_id", "head_sha"):
            fixture = Fixture()
            fixture.artifacts["dotnet"]["workflow_run"][key] = "incorrect"
            with self.subTest(key=key):
                self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

    def test_missing_corrupt_and_unexpected_archive_contents_fall_back(self):
        fixture = Fixture()
        fixture.artifacts.clear()
        self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))
        fixture = Fixture()
        fixture.archives[10] = b"corrupt archive"
        self.assertFalse(fixture.lookup()["dotnet"]["reuse"])
        for extra in ("../commands.sh", "commands.py", "nested/manifest.json"):
            fixture = Fixture()
            fixture.archive("dotnet", extra)
            with self.subTest(extra=extra):
                self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

    def test_results_need_all_discovered_unique_passing_outcomes(self):
        for key, value in (("total", 1), ("executed", 1), ("passed", 1), ("failed", 1), ("skipped", 1), ("failed", False),
                           ("identities", []), ("identities", [fp.digest("same")] * 2)):
            fixture = Fixture()
            fixture.reports["dotnet"][key] = value
            fixture.manifests["dotnet"]["results_sha256"] = fp.digest(fixture.reports["dotnet"])
            fixture.archive("dotnet")
            with self.subTest(key=key):
                self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

    def test_exact_checkout_log_is_required_and_manifest_cannot_substitute_tree(self):
        for logs in (b"", b"checkout skipped", b"git log -1 --format=%H\nforged"):
            fixture = Fixture()
            fixture.logs = logs
            self.assertFalse(fixture.lookup()["dotnet"]["reuse"])
        fixture = Fixture()
        fixture.entries[0][3] = "e" * 40
        self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

    def test_synthetic_merge_can_reuse_across_different_commit_ids(self):
        fixture = Fixture()
        fixture.run.update(event="pull_request", head_sha="e" * 40)
        for job in fixture.jobs.values():
            job["head_sha"] = fixture.run["head_sha"]
        fixture.gate["head_sha"] = fixture.run["head_sha"]
        fixture.source["parents"] = [{"sha": "f" * 40}, {"sha": fixture.run["head_sha"]}]
        for artifact in fixture.artifacts.values():
            artifact["workflow_run"]["head_sha"] = fixture.run["head_sha"]
        fixture.prs = [{"head": {"sha": fixture.run["head_sha"], "repo": fixture.run["head_repository"]},
                        "base": {"repo": fixture.run["repository"], "ref": "main"}, "author_association": "MEMBER"}]
        fixture.current["commit"] = "1" * 40  # The current merge commit differs; included content is identical.
        self.assertTrue(all(d["reuse"] for d in fixture.lookup().values()))
        fixture.prs[0]["head"]["sha"] = "3" * 40  # The trusted PR advanced after the original run.
        self.assertTrue(all(d["reuse"] for d in fixture.lookup().values()))
        fixture.source["parents"][1]["sha"] = "2" * 40
        self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

    def test_untrusted_same_repository_pr_author_cannot_supply_evidence(self):
        fixture = Fixture()
        fixture.run["event"] = "pull_request"
        fixture.source["parents"] = [{"sha": "f" * 40}, {"sha": fixture.run["head_sha"]}]
        fixture.prs = [{"head": {"sha": fixture.run["head_sha"], "repo": fixture.run["head_repository"]},
                        "base": {"repo": fixture.run["repository"], "ref": "main"}, "author_association": "CONTRIBUTOR"}]
        self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

    def test_api_denial_rate_limit_and_unexpected_data_require_normal_tests(self):
        for error in (urllib.error.HTTPError("https://api.github.com", 403, "rate limit", {}, None),
                      urllib.error.URLError("unavailable"), KeyError("unexpected response")):
            fixture = Fixture()
            fixture.failure = error
            self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))
            if isinstance(error, urllib.error.HTTPError):
                error.close()

    def test_force_full_bypasses_api_and_partial_hit_is_independent(self):
        fixture = Fixture()
        self.assertFalse(any(d["reuse"] for d in fixture.lookup(forced=True).values()))
        self.assertEqual(fixture.calls, [])
        fixture.artifacts.pop("browser")
        fixture.artifacts.pop("frontend")
        decisions = fixture.lookup()
        self.assertEqual({s: d["reuse"] for s, d in decisions.items()},
                         {"dotnet": True, "browser": False, "frontend": False})

    def test_api_failure_after_a_partial_hit_restores_full_execution(self):
        fixture = Fixture()
        fixture.artifacts.pop("browser")
        fixture.artifacts.pop("frontend")
        original = fixture.pages

        def pages(path, key, **kwargs):
            if path.startswith("/actions/artifacts?name=") and "-browser-" in path:
                raise urllib.error.URLError("API unavailable after first suite")
            return original(path, key, **kwargs)

        with mock.patch.object(fixture, "pages", side_effect=pages):
            self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))

    def test_artifact_download_outage_after_a_hit_restores_full_execution(self):
        fixture = Fixture()
        original = fixture.request

        def request(path, **kwargs):
            if path == "/actions/artifacts/11/zip":
                raise urllib.error.URLError("Download unavailable after the SQL HIT")
            return original(path, **kwargs)

        with mock.patch.object(fixture, "request", side_effect=request):
            self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))

    def test_unknown_environment_only_disables_affected_suites(self):
        fixture = Fixture()
        fixture.current["suites"]["dotnet"]["unavailable"] = "SQL identity unavailable"
        fixture.current["suites"]["browser"]["unavailable"] = "SQL identity unavailable"
        decisions = fixture.lookup()
        self.assertFalse(decisions["dotnet"]["reuse"])
        self.assertFalse(decisions["browser"]["reuse"])
        self.assertTrue(decisions["frontend"]["reuse"])


class ApiTests(unittest.TestCase):
    def test_live_probe_exercises_real_validator_with_independent_invalidation_and_corruption(self):
        fixture = Fixture()
        with mock.patch.object(reuse, "utcnow", return_value=fixture.now):
            report = probe_reuse.probes(fixture.config, fixture.current, 7, lambda: fixture)
        self.assertTrue(all(case["passed"] for case in report.values()))

    def test_history_search_is_bounded_and_paginated(self):
        api = reuse.GitHub("clcdpc/asap-pocketbase", "synthetic-token")
        with mock.patch.object(api, "get", return_value={"workflow_runs": [{}] * 10}) as get:
            self.assertEqual(len(list(api.pages("/actions/workflows/dotnet.yml/runs?status=success", "workflow_runs"))), 30)
        self.assertEqual(get.call_count, 3)
        self.assertTrue(get.call_args.args[0].endswith("&per_page=10&page=3"))

    def test_content_addressed_lookup_ignores_arbitrary_recent_draft_and_reuse_runs(self):
        fixture = Fixture()
        original = fixture.pages
        def pages(path, key, **kwargs):
            self.assertFalse(path.startswith("/actions/workflows/"), "Run history must not bound evidence discovery")
            if path.startswith("/actions/artifacts?name="):
                self.assertEqual(kwargs["size"], 100)
            return original(path, key, **kwargs)
        with mock.patch.object(fixture, "pages", side_effect=pages):
            self.assertTrue(all(d["reuse"] for d in fixture.lookup().values()))
        self.assertEqual(len([path for path in fixture.calls if path.startswith("/actions/artifacts?name=")]), 3)
        # Thousands of unrelated runs are absent from this server-side exact-key
        # query, rather than occupying the former latest-30-run search window.
        for suite in fp.SUITES:
            self.assertIn("/actions/artifacts?name=" + fixture.artifacts[suite]["name"], fixture.calls)

    def test_signed_storage_redirect_drops_authorization_and_rejects_http(self):
        request = urllib.request.Request("https://api.github.com/repos/repo/artifact", headers={"Authorization": "Bearer synthetic"})
        redirect = reuse.SafeRedirect().redirect_request(request, None, 302, "", {}, "https://storage.example/artifact")
        self.assertIsNone(redirect.get_header("Authorization"))
        with self.assertRaises(ValueError):
            reuse.SafeRedirect().redirect_request(request, None, 302, "", {}, "http://storage.example/artifact")

    def test_only_explicit_force_or_invalid_selection_requires_fresh_execution(self):
        for env in ({"ASAP_FORCE_FULL_CI": "true"}, {"ASAP_FORCE_FULL_CI": "true", "GITHUB_EVENT_NAME": "release"}):
            with mock.patch.dict(os.environ, env, clear=True):
                self.assertTrue(reuse.forced_full())
        for env in ({"GITHUB_EVENT_NAME": "release"},
                    {"GITHUB_REF_TYPE": "tag", "GITHUB_EVENT_NAME": "push", "GITHUB_REF_NAME": "v1.0.0"},
                    {"GITHUB_REF_TYPE": "tag", "GITHUB_EVENT_NAME": "push", "GITHUB_REF_NAME": "v1.0.0-test.24"},
                    {"ASAP_DEPLOY_TEST_IIS": "true", "GITHUB_EVENT_NAME": "workflow_dispatch"}):
            with mock.patch.dict(os.environ, env, clear=True), \
                 mock.patch.object(test_selection, "load", return_value={"decision": {"fresh": False}}):
                self.assertFalse(reuse.forced_full())
        for fresh in (True, False, None, 0, "false"):
            with mock.patch.dict(os.environ, {}, clear=True), \
                 mock.patch.object(test_selection, "load", return_value={"decision": {"fresh": fresh}}):
                self.assertEqual(reuse.forced_full(), fresh is not False)
        with mock.patch.dict(os.environ, {}, clear=True), mock.patch.object(test_selection, "load", side_effect=ValueError):
            self.assertTrue(reuse.forced_full())


class EnvironmentTests(unittest.TestCase):
    def environment(self, extra=None, sql_error=False, tool_version="version-1", package_version="1"):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            reuse.write_json(root / "node_modules/playwright/package.json", {"version": "1.62.1"})
            reuse.write_json(root / "node_modules/playwright-core/browsers.json", {
                "browsers": [{"name": "chromium", "revision": "1234", "browserVersion": "151.0"},
                             {"name": "chromium-headless-shell", "revision": "1234", "browserVersion": "151.0"}]})
            def command(arguments, *_):
                return "libc6=" + package_version if arguments[0] == "dpkg-query" else tool_version
            with mock.patch.dict(os.environ, {"RUNNER_OS": "Linux", "RUNNER_ARCH": "X64", **(extra or {})}, clear=True), \
                 mock.patch.object(fp.platform, "system", return_value="Linux"), \
                 mock.patch.object(fp, "command", side_effect=command), \
                 mock.patch.object(fp, "nuget_identity", return_value="nuget-content"), \
                 mock.patch.object(fp, "sql_image", side_effect=ValueError("unavailable") if sql_error else None,
                                   return_value="mcr.microsoft.com/mssql/server@sha256:" + "1" * 64):
                return fp.environment(fp.configuration(), root)

    def test_throwaway_environment_drift_is_observed_without_invalidating_reuse(self):
        first, problems, image = self.environment()
        self.assertEqual(problems, {})
        self.assertEqual(image, fp.SQL_TAG)
        self.assertIn("sql_image", first["dotnet"])
        self.assertIn("playwright", first["browser"])
        self.assertNotIn("sql_image", first["frontend"])
        second, _, _ = self.environment(tool_version="version-2")
        native_changed, _, _ = self.environment(package_version="2")
        for suite in fp.SUITES:
            self.assertNotEqual(fp.digest(first[suite]), fp.digest(second[suite]))
            self.assertNotEqual(fp.digest(first[suite]), fp.digest(native_changed[suite]))
            self.assertEqual(fp.environment_fingerprint(first[suite]), fp.environment_fingerprint(second[suite]))
            self.assertEqual(fp.environment_fingerprint(first[suite]), fp.environment_fingerprint(native_changed[suite]))

    def test_repository_policy_and_resolved_test_dependencies_still_invalidate(self):
        environments, _, _ = self.environment()
        for suite in fp.SUITES:
            original = environments[suite]
            for key in ("epoch", "nuget", "playwright"):
                changed = {**original, key: "changed test input"}
                with self.subTest(suite=suite, key=key):
                    self.assertNotEqual(fp.environment_fingerprint(original), fp.environment_fingerprint(changed))

    def test_sql_resolution_is_deferred_to_fresh_execution(self):
        _, problems, image = self.environment(sql_error=True)
        self.assertEqual(problems, {})
        self.assertEqual(image, fp.SQL_TAG)

    def test_missing_observational_tools_do_not_disable_reuse(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            reuse.write_json(root / "node_modules/playwright/package.json", {"version": "1.62.1"})
            reuse.write_json(root / "node_modules/playwright-core/browsers.json", {
                "browsers": [{"name": "chromium", "revision": "1234"}]})
            with mock.patch.dict(os.environ, {"RUNNER_OS": "Linux", "RUNNER_ARCH": "X64"}, clear=True), \
                 mock.patch.object(fp, "command", side_effect=RuntimeError("private probe detail")), \
                 mock.patch.object(fp, "nuget_identity", return_value="resolved-content"), \
                 mock.patch.object(fp, "sql_image", side_effect=AssertionError("Not needed for reuse")):
                environments, problems, _ = fp.environment(fp.configuration(), root)
                self.assertEqual(problems, {})
                for suite in fp.SUITES:
                    self.assertTrue(fp.environment_fingerprint(environments[suite]))
                self.assertNotIn("private probe detail", json.dumps(environments))

    def test_unidentified_runtime_extensions_disable_affected_reuse_without_exporting_values(self):
        for variable, suites in (("ASAP_TEST_CHROMIUM_EXECUTABLE_PATH", {"browser"}),
                                 ("PLAYWRIGHT_BROWSERS_PATH", {"browser"}), ("NODE_OPTIONS", set(fp.SUITES)),
                                 ("DOTNET_STARTUP_HOOKS", set(fp.SUITES))):
            environment, problems, _ = self.environment({variable: "sensitive-runtime-override"})
            with self.subTest(variable=variable):
                self.assertEqual(set(problems), suites)
                self.assertNotIn("sensitive-runtime-override", json.dumps(environment))

    def test_runner_machine_name_is_not_an_identity_component(self):
        first, _, _ = self.environment({"RUNNER_NAME": "ephemeral-one"})
        second, _, _ = self.environment({"RUNNER_NAME": "ephemeral-two"})
        self.assertEqual(first, second)

    def test_registry_body_requires_its_immutable_sha256(self):
        body = fp.canonical({"schemaVersion": 2})
        response = mock.MagicMock()
        response.__enter__.return_value = response
        response.read.return_value = body
        response.headers = {"Docker-Content-Digest": "sha256:" + hashlib.sha256(body).hexdigest()}
        with mock.patch.object(fp.urllib.request, "urlopen", return_value=response):
            self.assertEqual(fp.sql_image(), "mcr.microsoft.com/mssql/server@" + response.headers["Docker-Content-Digest"])
            response.headers = {"Docker-Content-Digest": "sha256:" + "0" * 64}
            with self.assertRaises(ValueError):
                fp.sql_image()

    def test_plan_failure_writes_misses_and_does_not_leak_exception_details(self):
        with tempfile.TemporaryDirectory() as name:
            directory = Path(name)
            output = directory / "outputs"
            fixture = Fixture()
            fixture.current["sql_image"] = fp.SQL_TAG
            env = {"GITHUB_SHA": fixture.current["commit"], "GITHUB_RUN_ID": "99", "GITHUB_RUN_ATTEMPT": "1",
                   "GITHUB_OUTPUT": str(output), "GITHUB_JOB": "dotnet-qualification"}
            for failure in ("fingerprint", "api"):
                with self.subTest(failure=failure), mock.patch.dict(os.environ, env, clear=True), \
                     mock.patch.object(fp, "calculate", side_effect=RuntimeError("secret") if failure == "fingerprint" else None,
                                       return_value=fixture.current), \
                     mock.patch.object(reuse, "api_client", side_effect=RuntimeError("secret")), \
                     mock.patch("sys.stdout", new_callable=io.StringIO) as printed:
                    reuse.plan(directory)
                plan = json.loads((directory / "plan.json").read_bytes())
                self.assertFalse(any(d["reuse"] for d in plan["decisions"].values()))
                self.assertNotIn("secret", printed.getvalue())
                self.assertIn("dotnet=false", output.read_text())


class GateTests(unittest.TestCase):
    def run_gate(self, fixture, outcomes, decisions=None, mutate_plan=None, forced=False, scope=None, environments=None, owned=None):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        directory = Path(temporary.name)
        reuse.write_json(directory / "current.json", fixture.current)
        if decisions is not None:
            plan = {"run_id": "99", "attempt": "1", "decisions": decisions}
            if mutate_plan:
                mutate_plan(plan)
            reuse.write_json(directory / "plan.json", plan)
        env = {"GITHUB_SHA": fixture.current["commit"], "GITHUB_RUN_ID": "99", "GITHUB_RUN_ATTEMPT": "1",
               "GITHUB_OUTPUT": str(directory / "outputs"),
               **{s.upper() + "_OUTCOME": value for s, value in outcomes.items() if value is not None}}
        if scope:
            env["ASAP_CI_SUITES"] = scope
        selections = copy.deepcopy(fixture.selections)
        with mock.patch.dict(os.environ, env, clear=True), mock.patch.object(reuse, "utcnow", return_value=fixture.now), \
             mock.patch.object(fp, "configuration", return_value=fixture.config), \
             mock.patch.object(fp, "checkout", return_value=(fixture.current["commit"], fixture.current["tree"])), \
             mock.patch.object(fp, "tracked_entries", return_value=fixture.entries), \
             mock.patch.object(fp, "selections", return_value=selections), \
             mock.patch.object(fp, "environment", return_value=(environments or {s: fixture.current["suites"][s]["environment_inputs"] for s in fp.SUITES}, {}, fp.SQL_TAG)), \
             mock.patch.object(reuse, "forced_full", return_value=forced), \
             mock.patch.object(reuse, "api_client", return_value=fixture), \
             mock.patch.object(reuse, "execution_results", side_effect=lambda s, *args: results(fixture.selections[s])), \
             mock.patch("sys.stdout", new_callable=io.StringIO):
            rows = {}
            for suite in (owned,) if owned else fp.SUITES:
                os.environ["GITHUB_JOB"] = fp.EXECUTORS[suite]["id"]
                if decisions is not None:
                    plan["job"] = os.environ["GITHUB_JOB"]
                    reuse.write_json(directory / "plan.json", plan)
                reuse.verify(directory)
                rows.update(json.loads((directory / "verification.json").read_bytes()))
            reuse.write_json(directory / "verification.json", rows)
        return directory

    def test_ambient_split_scope_cannot_narrow_the_required_suite_population(self):
        for scope in ("dotnet,frontend", "browser"):
            suites = fp.SUITES
            directory = self.run_gate(Fixture(), {s: "success" for s in suites}, scope=scope)
            self.assertEqual(set(json.loads((directory / "verification.json").read_bytes())), set(suites))
            self.assertEqual({p.name for p in (directory / "evidence").iterdir()}, set(suites))
            for suite in suites:
                with self.subTest(scope=scope, missing=suite), self.assertRaises(reuse.EvidenceError):
                    self.run_gate(Fixture(), {s: "success" for s in suites if s != suite}, scope=scope)

    def test_successful_executions_produce_original_evidence_with_counts(self):
        fixture = Fixture()
        directory = self.run_gate(fixture, {s: "success" for s in fp.SUITES})
        for suite in fp.SUITES:
            manifest = json.loads((directory / "evidence" / suite / "manifest.json").read_bytes())
            self.assertEqual(manifest["kind"], "executed")
            self.assertEqual(manifest["counts"]["passed"], 2)
            self.assertEqual(manifest["selection"]["expected"], 2)
            self.assertEqual(manifest["job"], fixture.config["executors"][suite]["name"])

    def test_worker_cannot_attest_another_suite_even_with_ambient_scope(self):
        for suite in fp.SUITES:
            directory = self.run_gate(Fixture(), {suite: "success"}, scope=",".join(fp.SUITES), owned=suite)
            self.assertEqual(set(json.loads((directory / "verification.json").read_bytes())), {suite})
            self.assertEqual((directory / "outputs").read_text().strip(), f"verified_{suite}=true")

    def test_all_hits_are_revalidated_and_do_not_create_original_evidence(self):
        fixture = Fixture()
        decisions = fixture.lookup()
        fixture.calls.clear()
        directory = self.run_gate(fixture, {s: "skipped" for s in fp.SUITES}, decisions)
        self.assertFalse((directory / "evidence").exists())
        self.assertIn("/actions/artifacts/10", fixture.calls)
        rows = json.loads((directory / "verification.json").read_bytes())
        self.assertTrue(all(row["status"] == "REUSED" for row in rows.values()))

    def test_force_full_gate_rejects_skipped_tests_even_with_matching_full_evidence(self):
        fixture = Fixture()
        with self.assertRaises(ValueError):
            self.run_gate(fixture, {s: "skipped" for s in fp.SUITES}, fixture.lookup(), forced=True)

    def test_gate_rechecks_actual_resolved_dependencies_after_lookup(self):
        fixture = Fixture()
        decisions = fixture.lookup()
        changed = {s: copy.deepcopy(fixture.current["suites"][s]["environment_inputs"]) for s in fp.SUITES}
        changed["dotnet"]["nuget"] = "changed-untracked-restored-dependency"
        with self.assertRaises(reuse.EvidenceError):
            self.run_gate(fixture, {s: "skipped" for s in fp.SUITES}, decisions, environments=changed)

    def test_deployment_only_retry_reuses_and_revalidates_original_qualification_attempt(self):
        fixture = Fixture()
        fixture.attempts[2] = copy.deepcopy(fixture.run)
        fixture.attempts[2]["conclusion"] = "failure"
        fixture.run["run_attempt"] = 3
        fixture.attempt_jobs[3] = [success(name="Deploy exact artifact to test IIS", run_id=7,
                                          head_sha=fixture.run["head_sha"], status="in_progress", conclusion=None)]
        decisions = fixture.lookup()
        directory = self.run_gate(fixture, {s: "skipped" for s in fp.SUITES}, decisions)
        rows = json.loads((directory / "verification.json").read_bytes())
        self.assertTrue(all(row["status"] == "REUSED" and row["source"]["attempt"] == 2 for row in rows.values()))
        self.assertIn("/actions/runs/7/attempts/2", fixture.calls)
        self.assertIn("/actions/runs/7/attempts/3/jobs", fixture.calls)

    def test_plan_to_final_gate_tolerates_deployment_status_change_but_not_unfinished_qualification(self):
        fixture = Fixture()
        decisions = fixture.lookup()
        fixture.run.update(status="in_progress", conclusion=None)
        directory = self.run_gate(fixture, {s: "skipped" for s in fp.SUITES}, decisions)
        self.assertTrue(all(row["status"] == "REUSED" for row in json.loads((directory / "verification.json").read_bytes()).values()))
        fixture.gate.update(status="in_progress", conclusion=None)
        with self.assertRaises(reuse.EvidenceError):
            self.run_gate(fixture, {s: "skipped" for s in fp.SUITES}, decisions)

    def test_skipped_tests_without_valid_evidence_fail_required_gate(self):
        fixture = Fixture()
        with self.assertRaises(ValueError):
            self.run_gate(fixture, {s: "skipped" for s in fp.SUITES})

    def test_failed_or_missing_current_result_fails_gate(self):
        for suite in fp.SUITES:
            for outcome in ("failure", "cancelled", "skipped", None):
                fixture = Fixture()
                outcomes = {s: "success" for s in fp.SUITES}
                outcomes[suite] = outcome
                with self.subTest(suite=suite, outcome=outcome), self.assertRaises(ValueError):
                    self.run_gate(fixture, outcomes)

    def test_partial_hit_only_records_executed_suites(self):
        # Exercise the real verifier under each fixed owning job for all eight combinations.
        for mask in range(8):
            fixture = Fixture()
            hits = {suite for index, suite in enumerate(fp.SUITES) if mask & (1 << index)}
            for suite in set(fp.SUITES) - hits:
                fixture.artifacts.pop(suite)
            decisions = fixture.lookup()
            self.assertEqual({s for s, d in decisions.items() if d["reuse"]}, hits)
            directory = self.run_gate(fixture, {s: "skipped" if s in hits else "success" for s in fp.SUITES}, decisions)
            rows = json.loads((directory / "verification.json").read_bytes())
            for suite in fp.SUITES:
                with self.subTest(mask=mask, suite=suite):
                    self.assertEqual(rows[suite]["status"], "REUSED" if suite in hits else "EXECUTED")
                    self.assertEqual((directory / "evidence" / suite / "manifest.json").exists(), suite not in hits)

    def test_artifact_lost_after_lookup_cannot_authorize_skip(self):
        fixture = Fixture()
        decisions = fixture.lookup()
        fixture.archives.clear()
        with self.assertRaises(ValueError):
            self.run_gate(fixture, {s: "skipped" for s in fp.SUITES}, decisions)

    def test_forged_current_fingerprint_and_other_attempt_plan_fail_gate(self):
        fixture = Fixture()
        fixture.current["suites"]["dotnet"]["fingerprint"] = "forged"
        with self.assertRaises(ValueError):
            self.run_gate(fixture, {s: "success" for s in fp.SUITES})
        fixture = Fixture()
        with self.assertRaises(ValueError):
            self.run_gate(fixture, {s: "skipped" for s in fp.SUITES}, fixture.lookup(),
                          lambda plan: plan.update(attempt="2"))

    def test_summary_identifies_source_age_fingerprint_and_actual_timing(self):
        fixture = Fixture()
        source = fixture.validate()
        rows = {s: {"status": "REUSED", "reason": "Verified successful original execution", "source": source} for s in fp.SUITES}
        report = reuse.summary(rows, fixture.current)
        self.assertIn("| .NET/SQL | REUSED | [Original run](https://github.com/clcdpc/asap-pocketbase/actions/runs/7)", report)
        self.assertIn("25.00 min saved", report)
        self.assertIn("Evidence age 1.50 h", report)
        self.assertIn(fixture.current["suites"]["dotnet"]["fingerprint"], report)


class NativeResultsTests(unittest.TestCase):
    def report(self, outcome="Passed", counts=None):
        counts = counts or 'total="2" executed="2" passed="2" failed="0" notExecuted="0"'
        return (f'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'
                f'<UnitTestResult executionId="one" testId="one" outcome="{outcome}"/>'
                '<UnitTestResult executionId="two" testId="two" outcome="Passed"/></Results>'
                f'<ResultSummary outcome="Completed"><Counters {counts}/></ResultSummary></TestRun>')

    def test_native_reports_need_complete_successful_counts_and_outcomes(self):
        with tempfile.TemporaryDirectory() as name, mock.patch.object(fp, "ROOT", Path(name)):
            directory = Path(name)
            path = directory / ".artifacts/browser-performance/test-results.trx"
            path.parent.mkdir(parents=True)
            selection = {"minimum": 2, "expected": 2}
            path.write_text(self.report())
            self.assertEqual(reuse.execution_results("browser", selection, directory)["passed"], 2)
            for report in (self.report(outcome="Skipped"), self.report(counts='total="2" executed="2" passed="2" failed="1"'),
                           self.report(counts='total="3" executed="3" passed="3" failed="0"'), "<partial"):
                path.write_text(report)
                with self.subTest(report=report), self.assertRaises((ValueError, ET.ParseError)):
                    reuse.execution_results("browser", selection, directory)

    def test_native_suites_read_only_their_own_trx_after_sequential_execution(self):
        with tempfile.TemporaryDirectory() as name, mock.patch.object(fp, "ROOT", Path(name)):
            selected = {"minimum": 2, "expected": 2}
            paths = {s: Path(name) / ".artifacts" / output / "test-results.trx"
                     for s, output in (("dotnet", "ci-performance"), ("browser", "browser-performance"))}
            for path in paths.values():
                path.parent.mkdir(parents=True)
            for failed in paths:
                for suite, path in paths.items():
                    path.write_text(self.report(outcome="Failed" if suite == failed else "Passed"))
                with self.assertRaises(reuse.EvidenceError):
                    reuse.execution_results(failed, selected)
                passed = next(s for s in paths if s != failed)
                self.assertEqual(reuse.execution_results(passed, selected)["passed"], 2)

    def test_frontend_filtered_or_incomplete_runs_cannot_be_evidence(self):
        with tempfile.TemporaryDirectory() as name:
            directory = Path(name)
            selection = {"minimum": 2, "expected": 2, "files": ["a.test.js", "b.test.js"]}
            data = {"filter": "", "files": selection["files"], "executed": 2, "passed": 2, "failed": 0, "skipped": 0}
            reuse.write_json(directory / "frontend-results.json", data)
            self.assertEqual(reuse.execution_results("frontend", selection, directory)["passed"], 2)
            for key, value in (("filter", "one-test"), ("files", ["a.test.js"]), ("failed", 1), ("skipped", 1)):
                reuse.write_json(directory / "frontend-results.json", {**data, key: value})
                with self.subTest(key=key), self.assertRaises(ValueError):
                    reuse.execution_results("frontend", selection, directory)

    def test_native_omissions_duplicates_and_equal_count_substitution_fail(self):
        with tempfile.TemporaryDirectory() as name, mock.patch.object(fp, "ROOT", Path(name)):
            directory = Path(name)
            selected = {"minimum": 2, "expected": 2, "identities": sorted(fp.digest(uid) for uid in ("one", "two"))}
            path = directory / ".artifacts/browser-performance/test-results.trx"
            path.parent.mkdir(parents=True)
            path.write_text(self.report())
            reuse.execution_results("browser", selected, directory)
            for report in (self.report().replace('testId="two"', 'testId="one"'),
                           self.report().replace('testId="two"', 'testId="wrong"'),
                           self.report().replace('executionId="two"', 'executionId="one"'),
                           self.report().replace('testId="two"', ''),
                           self.report().replace('testId="two" outcome="Passed"', 'testId="two" outcome="Failed"')):
                path.write_text(report)
                with self.subTest(report=report), self.assertRaises(reuse.EvidenceError):
                    reuse.execution_results("browser", selected, directory)


class ExecutorScopeTests(unittest.TestCase):
    def test_plan_uses_fixed_job_ownership_despite_ambient_scope(self):
        for owned, owner in fp.EXECUTORS.items():
            for forced in (False, True):
                with tempfile.TemporaryDirectory() as name:
                    fixture = Fixture()
                    directory = Path(name)
                    output = directory / "outputs"
                    env = {"GITHUB_SHA": fixture.current["commit"], "GITHUB_RUN_ID": "99",
                           "GITHUB_RUN_ATTEMPT": "1", "GITHUB_OUTPUT": str(output),
                           "GITHUB_JOB": owner["id"], "ASAP_CI_SUITES": ",".join(fp.SUITES)}
                    with mock.patch.dict(os.environ, env, clear=True), \
                         mock.patch.object(fp, "calculate", return_value=fixture.current), \
                         mock.patch.object(reuse, "api_client", return_value=fixture), \
                         mock.patch.object(reuse, "forced_full", return_value=forced), \
                         mock.patch.object(reuse, "utcnow", return_value=fixture.now), \
                         mock.patch("sys.stdout", new_callable=io.StringIO):
                        reuse.plan(directory)
                    plan = json.loads((directory / "plan.json").read_bytes())
                    self.assertEqual(plan["job"], owner["id"])
                    self.assertEqual(set(plan["decisions"]), {owned})
                    self.assertEqual(plan["decisions"][owned]["reuse"], not forced)
                    exported = dict(line.split("=", 1) for line in output.read_text().splitlines())
                    self.assertEqual(set(exported), {owned, "schema", owned + "_fingerprint"})

    def test_only_fixed_owning_executors_can_qualify_their_suite(self):
        for suite, owner in fp.EXECUTORS.items():
            self.assertEqual(reuse.validate_executor({"GITHUB_JOB": owner["id"], "ASAP_CI_SUITES": "other"}), suite)
        for job in (None, "", "draft-validation", "validation", "removed-executor"):
            with self.assertRaises(reuse.EvidenceError):
                reuse.validate_executor({"GITHUB_JOB": job})


class WorkflowTests(unittest.TestCase):
    def test_required_check_and_always_current_commit_packaging_are_preserved(self):
        workflow = (fp.ROOT / ".github/workflows/dotnet.yml").read_text(encoding="utf-8")
        for token in ("name: ${{ needs.validation-policy.outputs.mode == 'draft' && 'Draft CI result' || 'Build, test and package' }}",
                      "runs-on: ubuntu-24.04", "ref: ${{ github.sha }}",
                      "dotnet build Asap.sln --configuration Release", "run: npm ci", "run: npm test",
                      "npm run test:browser", "python3 -B scripts/ci/selection.py run-draft", "bash scripts/ci/run-with-performance.sh",
                      "name: Summarize .NET/SQL diagnostic evidence", "name: Upload .NET/SQL performance telemetry",
                      "dotnet publish src/Asap.Web/Asap.Web.csproj",
                      "$commit = (git rev-parse HEAD).Trim().ToLowerInvariant()", "if ($commit -ne $env:GITHUB_SHA)",
                      '"commit=$commit" >> $env:GITHUB_OUTPUT',
                      "commit: ${{ steps.identity.outputs.commit }}", "TESTED_COMMIT: ${{ steps.identity.outputs.commit }}",
                      "commitSha = $env:TESTED_COMMIT", "-ValidateOnly", "ExpectedZipSha256", "zip_sha256=$zipHash",
                      "name: Remove SQL Server test container\n        if: ${{ always() }}"):
            self.assertIn(token, workflow)
        self.assertNotIn("pull_request_target", workflow)
        self.assertNotIn("paths-ignore:", workflow)
        self.assertNotIn("actions: write", workflow)
        self.assertIn("actions: read", workflow)
        self.assertIn("    permissions:\n      contents: read\n      actions: read\n      pull-requests: read\n", workflow)
        self.assertNotIn(": write\n", workflow)
        self.assertIn("force_full_ci:", workflow)
        self.assertLess(workflow.index("name: Restore and build"), workflow.index("name: Find reusable test executions"))
        self.assertLess(workflow.index("name: Find reusable test executions"), workflow.index("run: npx playwright install --with-deps chromium"))
        self.assertNotIn("install-deps chromium", workflow)
        self.assertIn('sql_image="$(python3 -B scripts/ci/reuse.py sql-image)"', workflow)
        self.assertIn("SQL_IMAGE: ${{ steps.sql.outputs.image }}", workflow)
        self.assertIn("name: Verify required test suites\n        id: verify\n        if: ${{ always() }}", workflow)
        for suite in fp.SUITES:
            self.assertIn(f"if: ${{{{ steps.reuse.outputs.{suite} != 'true' }}}}", workflow)
            self.assertIn(f"name: asap-test-evidence-v${{{{ steps.reuse.outputs.schema }}}}-{suite}-${{{{ steps.reuse.outputs.{suite}_fingerprint }}}}", workflow)
        for suite in ("dotnet", "browser"):
            condition = f"if: ${{{{ steps.reuse.outputs.{suite} != 'true' }}}}"
            self.assertGreaterEqual(workflow.count(condition), 4)
        self.assertNotIn("ASAP_CI_SUITES:", workflow)
        # Packaging is unconditional on reuse; only the existing test-IIS activation gate can deploy.
        publishing = workflow.split("      - name: Verify publish artifacts\n", 1)[1].split("  dotnet-qualification:", 1)[0]
        self.assertNotIn("steps.reuse.outputs", publishing)


if __name__ == "__main__":
    unittest.main()
