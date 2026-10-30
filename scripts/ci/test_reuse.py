"""Adversarial reuse contracts: temporary Git trees, native reports, and mocked Actions API."""

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


def success(**fields):
    return {"status": "completed", "conclusion": "success", **fields}


def results(selection):
    count = selection["expected"]
    return {"total": count, "executed": count, "passed": count, "failed": 0, "skipped": 0,
            "identities": [fp.digest(name) for name in selection.get("files", [str(i) for i in range(count)])]}


class Fixture:
    def __init__(self):
        self.now = datetime(2026, 10, 29, 12, tzinfo=timezone.utc)
        self.config = fp.configuration()
        self.entries = [["src/production.cs", "100644", "blob", "c" * 40],
                        [self.config["workflow"], "100644", "blob", "d" * 40]]
        self.current = {"commit": "a" * 40, "tree": "b" * 40, "suites": {}}
        self.selections = {}
        for suite in fp.SUITES:
            selection = {"filter": "actual " + suite + " selection", "minimum": 2, "expected": 2}
            if suite == "frontend":
                selection["files"] = ["a.test.js", "b.test.js"]
            self.selections[suite] = selection
            environment = {"epoch": "image-v1", "runtime": "actual-version", "suite": suite}
            inputs = {"schema": self.config["schema"], "suite": suite,
                      "source": fp.source_fingerprint(self.entries, suite, self.config),
                      "environment": fp.digest(environment), "selection": selection}
            self.current["suites"][suite] = {**inputs, "fingerprint": fp.digest(inputs),
                                              "environment_inputs": environment, "unavailable": None}
        repository = {"id": self.config["repository_id"], "full_name": self.config["repository"]}
        self.run = success(id=7, run_attempt=2, head_sha=self.current["commit"], workflow_id=8,
                           path=self.config["workflow"], repository=copy.deepcopy(repository),
                           head_repository=copy.deepcopy(repository), event="push", head_branch="main",
                           created_at=reuse.iso(self.now - timedelta(hours=3)))
        self.job = success(id=9, run_id=7, head_sha=self.run["head_sha"], name=self.config["job"],
                           labels=self.config["runner_labels"], runner_group_name=self.config["runner_group"],
                           started_at=reuse.iso(self.now - timedelta(hours=2)),
                           completed_at=reuse.iso(self.now - timedelta(hours=1)), steps=[])
        for name in [*reuse.STEPS.values(), "Verify required test suites",
                     *(f"Upload original {s} execution evidence" for s in fp.SUITES)]:
            self.job["steps"].append(success(name=name, started_at=reuse.iso(self.now - timedelta(minutes=120)),
                                              completed_at=reuse.iso(self.now - timedelta(minutes=95))))
        self.source = {"sha": self.current["commit"], "tree": {"sha": self.current["tree"]}, "parents": []}
        self.logs = (f"2026-10-29T10:00:00.000Z [command]/usr/bin/git log -1 --format=%H\n"
                     f"2026-10-29T10:00:00.001Z {self.current['commit']}\n").encode()
        self.prs = []
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
                "workflow": self.config["workflow"], "job": self.config["job"], "run_id": 7, "run_attempt": 2,
                "source_commit": self.current["commit"], "source_tree": self.current["tree"],
                "source_fingerprint": identity["source"], "fingerprint": identity["fingerprint"],
                "environment_fingerprint": identity["environment"], "environment_inputs": identity["environment_inputs"],
                "selection": identity["selection"], "counts": {k: report[k] for k in ("total", "executed", "passed", "failed", "skipped")},
                "results_sha256": fp.digest(report), "executed_at": reuse.iso(executed),
                "expires_at": reuse.iso(executed + timedelta(days=self.config["lifetime_days"])),
                "lifetime_days": self.config["lifetime_days"],
            }
            self.artifacts[suite] = {"id": index, "name": reuse.artifact_name(self.config, suite, 7, 2),
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
            return copy.deepcopy(self.run)
        if path == f"/git/commits/{self.source['sha']}":
            return copy.deepcopy(self.source)
        if path == f"/git/trees/{self.current['tree']}?recursive=1":
            return {"sha": self.current["tree"], "truncated": False,
                    "tree": [dict(zip(("path", "mode", "type", "sha"), e)) for e in self.entries]}
        if path.startswith("/actions/artifacts/"):
            return copy.deepcopy(next(a for a in self.artifacts.values() if a["id"] == int(path.split("/")[-1])))
        raise AssertionError("Unexpected API endpoint: " + path)

    def request(self, path, limit=None):
        self.calls.append(path)
        if self.failure:
            raise self.failure
        if path == "/actions/jobs/9/logs":
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
            return iter([copy.deepcopy(self.job)])
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


class DependencyTests(unittest.TestCase):
    def test_unknown_and_shared_dependencies_invalidate_every_suite(self):
        config = fp.configuration()
        for path in ("unknown-new-file", "src/Asap.Web/Frontend/staff/app.js", "src/Shared/SecurityContract.cs",
                     "database/Asap.Database/Scripts/PostDeployment.sql", "tests/Asap.Tests/Sql/DatabaseBaselineTests.cs",
                     "dev/review/src/Asap.ReviewInit/LocalReviewer.sql", "dev/review/src/Asap.ReviewProviders/ReviewProviders.cs",
                     ".github/workflows/dotnet.yml", "scripts/ci/reuse.py", "global.json", "package-lock.json",
                     "Directory.Packages.props", "docs/implementation/test-iis-activation.md"):
            for suite in fp.SUITES:
                with self.subTest(path=path, suite=suite):
                    before = [[path, "100644", "blob", "a" * 40]]
                    after = [[path, "100644", "blob", "b" * 40]]
                    self.assertNotEqual(fp.source_fingerprint(before, suite, config), fp.source_fingerprint(after, suite, config))

    def test_exact_acceptance_screenshot_files_do_not_invalidate_sql(self):
        config = fp.configuration()
        for path in ("tests/acceptance/run.cjs", "tests/acceptance/helpers/reporting.cjs", "tests/acceptance/helpers/accessibility.cjs"):
            self.assertEqual(fp.source_fingerprint([[path, "100644", "blob", "a"]], "dotnet", config),
                             fp.source_fingerprint([[path, "100644", "blob", "b"]], "dotnet", config))
        # New files beside an exclusion are unknown dependencies, never implicitly excluded.
        path = "tests/acceptance/helpers/new-shared-fixture.cjs"
        self.assertNotEqual(fp.source_fingerprint([], "dotnet", config),
                            fp.source_fingerprint([[path, "100644", "blob", "b"]], "dotnet", config))

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
        for key, value in (("schema", 2), ("environment", "new-runtime-or-epoch"),
                           ("selection", {**original["selection"], "expected": 3}),
                           ("selection", {**original["selection"], "minimum": 3}),
                           ("selection", {**original["selection"], "filter": "new-filter"})):
            changed = {k: original[k] for k in ("schema", "suite", "source", "environment", "selection")}
            changed[key] = value
            self.assertNotEqual(fp.digest(changed), original["fingerprint"])

    def test_selection_tracks_current_guards_and_partition(self):
        selections = fp.selections()
        self.assertGreaterEqual(selections["dotnet"]["minimum"], 1439)
        self.assertEqual(selections["browser"]["minimum"], 17)
        self.assertEqual(sorted(term.replace("FullyQualifiedName!~", "") for term in selections["dotnet"]["filter"].split("&")),
                         sorted(term.replace("FullyQualifiedName~", "") for term in selections["browser"]["filter"].split("|")))
        self.assertEqual(selections["frontend"]["minimum"], len(selections["frontend"]["files"]))

    def test_pending_411_count_change_requires_new_evidence(self):
        fixture = Fixture()
        fixture.current["suites"]["dotnet"]["selection"]["minimum"] = 1525
        fixture.current["suites"]["dotnet"]["selection"]["expected"] = 1525
        self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

    def test_discovery_failure_or_below_minimum_is_not_a_match(self):
        for output in ("Discovered 0 tests.", "Discovered 1 tests.", "unexpected output"):
            with self.subTest(output=output), mock.patch.object(fp, "command", return_value=output):
                with self.assertRaises(ValueError):
                    fp.discover({"filter": "fixture", "minimum": 2})
        with mock.patch.object(fp, "command", return_value="Discovered 1525 tests.\n"):
            self.assertEqual(fp.discover({"filter": "fixture", "minimum": 1439}), 1525)


class ProvenanceTests(unittest.TestCase):
    def test_identical_successful_original_run_hits_all_suites(self):
        fixture = Fixture()
        decisions = fixture.lookup()
        self.assertTrue(all(d["reuse"] for d in decisions.values()))
        self.assertEqual(decisions["dotnet"]["source"]["saved_seconds"], 1500)

    def test_unsuccessful_run_never_hits_even_with_passing_artifacts(self):
        for status, conclusion in (("completed", "failure"), ("completed", "cancelled"),
                                   ("completed", "timed_out"), ("queued", None), ("in_progress", None),
                                   ("completed", "neutral")):
            fixture = Fixture()
            fixture.run.update(status=status, conclusion=conclusion)
            with self.subTest(status=status, conclusion=conclusion):
                self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))

    def test_prior_passing_tests_with_later_failed_packaging_are_rejected(self):
        fixture = Fixture()
        fixture.run["conclusion"] = "failure"
        self.assertTrue(all(successful["conclusion"] == "success" for successful in fixture.job["steps"]))
        self.assertFalse(any(d["reuse"] for d in fixture.lookup().values()))

    def test_wrong_repository_workflow_job_or_runner_is_rejected(self):
        for target, key, value in (("repository", "id", 42), ("head_repository", "id", 42),
                                   ("run", "workflow_id", 42), ("run", "path", ".github/workflows/other.yml"),
                                   ("job", "name", "Other job"), ("job", "runner_group_name", "other"),
                                   ("job", "labels", ["ubuntu-latest"]), ("job", "head_sha", "f" * 40)):
            fixture = Fixture()
            record = fixture.run[target] if target in ("repository", "head_repository") else getattr(fixture, target)
            record[key] = value
            with self.subTest(target=target, key=key):
                self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

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
        for key, value in changes.items():
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
        fixture.run["created_at"] = reuse.iso(fixture.now - timedelta(days=15))
        self.assertFalse(fixture.lookup()["dotnet"]["reuse"])

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
        for key, value in (("total", 1), ("executed", 1), ("passed", 1), ("failed", 1), ("skipped", 1),
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
        fixture.job["head_sha"] = fixture.run["head_sha"]
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

    def test_signed_storage_redirect_drops_authorization_and_rejects_http(self):
        request = urllib.request.Request("https://api.github.com/repos/repo/artifact", headers={"Authorization": "Bearer synthetic"})
        redirect = reuse.SafeRedirect().redirect_request(request, None, 302, "", {}, "https://storage.example/artifact")
        self.assertIsNone(redirect.get_header("Authorization"))
        with self.assertRaises(ValueError):
            reuse.SafeRedirect().redirect_request(request, None, 302, "", {}, "http://storage.example/artifact")

    def test_forced_release_deployment_and_manual_requests(self):
        for key in ("ASAP_FORCE_FULL_CI", "ASAP_DEPLOY_TEST_IIS", "GITHUB_REF_TYPE", "GITHUB_EVENT_NAME"):
            value = {"GITHUB_REF_TYPE": "tag", "GITHUB_EVENT_NAME": "release"}.get(key, "true")
            with mock.patch.dict(os.environ, {key: value}, clear=True):
                self.assertTrue(reuse.forced_full())
        with mock.patch.dict(os.environ, {}, clear=True):
            self.assertFalse(reuse.forced_full())


class EnvironmentTests(unittest.TestCase):
    def environment(self, extra=None, sql_error=False, tool_version="version-1"):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            reuse.write_json(root / "node_modules/playwright/package.json", {"version": "1.62.1"})
            reuse.write_json(root / "node_modules/playwright-core/browsers.json", {
                "browsers": [{"name": "chromium", "revision": "1234", "browserVersion": "151.0"},
                             {"name": "chromium-headless-shell", "revision": "1234", "browserVersion": "151.0"}]})
            with mock.patch.dict(os.environ, {"RUNNER_OS": "Linux", "RUNNER_ARCH": "X64", **(extra or {})}, clear=True), \
                 mock.patch.object(fp, "command", return_value=tool_version), \
                 mock.patch.object(fp, "nuget_identity", return_value="nuget-content"), \
                 mock.patch.object(fp, "sql_image", side_effect=ValueError("unavailable") if sql_error else None,
                                   return_value="mcr.microsoft.com/mssql/server@sha256:" + "1" * 64):
                return fp.environment(fp.configuration(), root)

    def test_observed_toolchain_changes_and_sql_digest_are_environment_inputs(self):
        first, problems, image = self.environment()
        self.assertEqual(problems, {})
        self.assertIn("@sha256:", image)
        self.assertIn("sql_image", first["dotnet"])
        self.assertIn("playwright", first["browser"])
        self.assertNotIn("sql_image", first["frontend"])
        second, _, _ = self.environment(tool_version="version-2")
        for suite in fp.SUITES:
            self.assertNotEqual(fp.digest(first[suite]), fp.digest(second[suite]))

    def test_sql_identity_failure_disables_only_sql_dependent_reuse(self):
        _, problems, image = self.environment(sql_error=True)
        self.assertEqual(set(problems), {"dotnet", "browser"})
        self.assertEqual(image, fp.SQL_TAG)

    def test_unidentified_runtime_extensions_disable_affected_reuse_without_exporting_values(self):
        for variable, suites in (("ASAP_TEST_CHROMIUM_EXECUTABLE_PATH", {"browser"}),
                                 ("PLAYWRIGHT_BROWSERS_PATH", {"browser"}), ("NODE_OPTIONS", set(fp.SUITES)),
                                 ("DOTNET_STARTUP_HOOKS", {"dotnet", "browser"})):
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
                   "GITHUB_OUTPUT": str(output)}
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
    def run_gate(self, fixture, outcomes, decisions=None, mutate_plan=None):
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
               **{s.upper() + "_OUTCOME": value for s, value in outcomes.items() if value is not None}}
        selections = {s: {k: v for k, v in selection.items() if k != "expected"} for s, selection in fixture.selections.items()}
        with mock.patch.dict(os.environ, env, clear=True), mock.patch.object(reuse, "utcnow", return_value=fixture.now), \
             mock.patch.object(fp, "configuration", return_value=fixture.config), \
             mock.patch.object(fp, "checkout", return_value=(fixture.current["commit"], fixture.current["tree"])), \
             mock.patch.object(fp, "tracked_entries", return_value=fixture.entries), \
             mock.patch.object(fp, "selections", return_value=selections), \
             mock.patch.object(reuse, "api_client", return_value=fixture), \
             mock.patch.object(reuse, "execution_results", side_effect=lambda s, *args: results(fixture.selections[s])), \
             mock.patch("sys.stdout", new_callable=io.StringIO):
            reuse.verify(directory)
        return directory

    def test_successful_executions_produce_original_evidence_with_counts(self):
        fixture = Fixture()
        directory = self.run_gate(fixture, {s: "success" for s in fp.SUITES})
        for suite in fp.SUITES:
            manifest = json.loads((directory / "evidence" / suite / "manifest.json").read_bytes())
            self.assertEqual(manifest["kind"], "executed")
            self.assertEqual(manifest["counts"]["passed"], 2)
            self.assertEqual(manifest["selection"]["expected"], 2)

    def test_all_hits_are_revalidated_and_do_not_create_original_evidence(self):
        fixture = Fixture()
        decisions = fixture.lookup()
        fixture.calls.clear()
        directory = self.run_gate(fixture, {s: "skipped" for s in fp.SUITES}, decisions)
        self.assertFalse((directory / "evidence").exists())
        self.assertIn("/actions/artifacts/10", fixture.calls)
        rows = json.loads((directory / "verification.json").read_bytes())
        self.assertTrue(all(row["status"] == "REUSED" for row in rows.values()))

    def test_skipped_tests_without_valid_evidence_fail_required_gate(self):
        fixture = Fixture()
        with self.assertRaises(ValueError):
            self.run_gate(fixture, {s: "skipped" for s in fp.SUITES})

    def test_failed_or_missing_current_result_fails_gate(self):
        for outcome in ("failure", "cancelled", None):
            fixture = Fixture()
            with self.subTest(outcome=outcome), self.assertRaises(ValueError):
                self.run_gate(fixture, {"dotnet": outcome, "browser": "success", "frontend": "success"})

    def test_partial_hit_only_records_executed_suites(self):
        fixture = Fixture()
        fixture.artifacts.pop("browser")
        fixture.artifacts.pop("frontend")
        decisions = fixture.lookup()
        directory = self.run_gate(fixture, {"dotnet": "skipped", "browser": "success", "frontend": "success"}, decisions)
        self.assertFalse((directory / "evidence/dotnet").exists())
        self.assertTrue((directory / "evidence/browser/manifest.json").exists())
        self.assertTrue((directory / "evidence/frontend/manifest.json").exists())

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
                f'<UnitTestResult executionId="one" outcome="{outcome}"/>'
                '<UnitTestResult executionId="two" outcome="Passed"/></Results>'
                f'<ResultSummary outcome="Completed"><Counters {counts}/></ResultSummary></TestRun>')

    def test_native_reports_need_complete_successful_counts_and_outcomes(self):
        with tempfile.TemporaryDirectory() as name:
            directory = Path(name)
            path = directory / "browser-results.trx"
            selection = {"minimum": 2, "expected": 2}
            path.write_text(self.report())
            self.assertEqual(reuse.execution_results("browser", selection, directory)["passed"], 2)
            for report in (self.report(outcome="Skipped"), self.report(counts='total="2" executed="2" passed="2" failed="1"'),
                           self.report(counts='total="3" executed="3" passed="3" failed="0"'), "<partial"):
                path.write_text(report)
                with self.subTest(report=report), self.assertRaises((ValueError, ET.ParseError)):
                    reuse.execution_results("browser", selection, directory)

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


class WorkflowTests(unittest.TestCase):
    def test_required_check_and_always_current_commit_packaging_are_preserved(self):
        workflow = (fp.ROOT / ".github/workflows/dotnet.yml").read_text(encoding="utf-8")
        for token in ("name: Build, test and package", "runs-on: clc-linux", "ref: ${{ github.sha }}",
                      "dotnet build Asap.sln --configuration Release", "run: npm ci", "run: npm test",
                      "run: npm run test:browser", "--minimum-expected-tests ", "bash scripts/ci/run-with-performance.sh",
                      "name: Summarize .NET/SQL diagnostic evidence", "name: Upload .NET/SQL performance telemetry",
                      "dotnet publish src/Asap.Web/Asap.Web.csproj", "dotnet publish src/Asap.Migration/Asap.Migration.csproj",
                      "commitSha = $env:GITHUB_SHA", "-ValidateOnly", "ExpectedZipSha256", "zip_sha256=$zipHash",
                      "name: Remove SQL Server test container\n        if: ${{ always() }}"):
            self.assertIn(token, workflow)
        self.assertNotIn("pull_request_target", workflow)
        self.assertNotIn("paths-ignore:", workflow)
        self.assertNotIn("actions: write", workflow)
        self.assertIn("actions: read", workflow)
        self.assertIn("force_full_ci:", workflow)
        self.assertIn("name: Verify required test suites\n        id: verify\n        if: ${{ always() }}", workflow)
        for suite in fp.SUITES:
            self.assertIn(f"if: ${{{{ steps.reuse.outputs.{suite} != 'true' }}}}", workflow)
            self.assertIn(f"name: asap-test-evidence-v${{{{ steps.reuse.outputs.schema }}}}-{suite}-${{{{ github.run_id }}}}-${{{{ github.run_attempt }}}}", workflow)
        sql_condition = "if: ${{ steps.reuse.outputs.dotnet != 'true' || steps.reuse.outputs.browser != 'true' }}"
        self.assertEqual(workflow.count(sql_condition), 3)
        # Packaging is unconditional on reuse; only the existing test-IIS activation gate can deploy.
        publishing = workflow.split("      - name: Verify publish artifacts\n", 1)[1].split("  deploy-test-iis:", 1)[0]
        self.assertNotIn("steps.reuse.outputs", publishing)


if __name__ == "__main__":
    unittest.main()
