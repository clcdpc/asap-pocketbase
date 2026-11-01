"""Exercise the real selector, Git integration diffs, native discovery and result gate."""

import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest import mock

import fingerprints as fp
import reuse
import selection as policy


class PolicyTests(unittest.TestCase):
    def test_reviewed_unrelated_changes_keep_core(self):
        for path in ("src/Asap.Web/Frontend/staff/app.js", "src/Asap.Web/Features/Analytics/AnalyticsService.cs",
                     "src/Asap.Web/Features/Staff/StaffRequestService.cs", "tests/Asap.Tests/Unit/TimeoutSemanticsTests.cs",
                     "tests/Asap.Tests/Integration/PatronJourneyTests.cs", "tests/browser/patron.cjs", "tests/request_helpers.test.js"):
            with self.subTest(path=path):
                self.assertEqual(policy.paths_policy([path])["categories"], ["core"])

    def test_migration_implementation_fixtures_and_contracts_qualify_all_history(self):
        for path in ("src/Asap.Migration/MigrationImporter.cs", "src/Asap.Migration/MigrationContract.cs",
                     "tests/Asap.Tests/Migration/MigrationCliTests.cs", "tests/fixtures/migration/source.json"):
            selected = policy.paths_policy([path])
            self.assertEqual(selected["categories"], policy.ALL)
            self.assertTrue(selected["fresh"])

    def test_runtime_dependencies_require_corresponding_import_oracles(self):
        for path in ("src/Asap.Web/Features/Patron/PatronConfigurationService.cs",
                     "src/Asap.Web/Features/Patron/PatronMessageDefaults.cs",
                     "src/Asap.Web/Features/Staff/TitleRequestEndpoints.cs"):
            with self.subTest(path=path):
                self.assertEqual(policy.paths_policy([path])["categories"], ["core", "migration"])

    def test_schema_deployment_shared_configuration_and_serialization_dependencies(self):
        for path in ("database/Asap.Database/Tables/SchemaVersion.sql", "src/Shared/SecurityContract.cs",
                     "src/Asap.Web/Infrastructure/Data/AsapDbContext.cs",
                     "src/Asap.Web/Infrastructure/Configuration/ExternalConfiguration.cs",
                     "src/Asap.Web/Infrastructure/Development/DacpacDeploymentService.cs",
                     "Directory.Packages.props", "global.json", "package-lock.json", "src/new-serializer.cs"):
            with self.subTest(path=path):
                self.assertEqual(policy.paths_policy([path])["categories"], policy.ALL)

    def test_shared_test_seed_dependency_requires_full_qualification(self):
        self.assertEqual(policy.paths_policy(["tests/Asap.Tests/Email/BuiltInEmailTemplateExpectations.cs"])["categories"], policy.ALL)

    def test_upgrade_tests_and_session_identity_dependency(self):
        self.assertEqual(policy.paths_policy(["tests/Asap.Tests/Sql/DatabaseBaselineTests.cs"])["categories"], policy.ALL)
        self.assertEqual(policy.paths_policy(["src/Asap.Web/Features/Patron/PatronSessionService.cs"])["categories"], ["core", "upgrade"])

    def test_selection_and_enforcement_changes_force_fresh_full_validation(self):
        for path in (".github/workflows/dotnet.yml", "scripts/ci/selection.py", "scripts/ci/reviewed-test-categories.json",
                     "scripts/ci/fingerprints.py", "scripts/ci/reuse.py", "scripts/ci/test_selection.py",
                     "scripts/deployment/Deploy-AsapTest.ps1", "tests/Asap.Tests/TestCategories.cs",
                     "tests/ci_workflow_contract.test.js", "tests/test_iis_deployment_contract.test.js",
                     "tests/run_all.js", "tests/browser/run.cjs", "src/Asap.Web/Asap.Web.csproj"):
            with self.subTest(path=path):
                selected = policy.paths_policy([path])
                self.assertEqual(selected["categories"], policy.ALL)
                self.assertTrue(selected["fresh"])

    def test_missing_invalid_or_unsupported_metadata_fails_closed(self):
        for env in ({}, {"GITHUB_EVENT_NAME": "schedule"}, {"GITHUB_EVENT_NAME": "push"},
                    {"GITHUB_EVENT_NAME": "pull_request"}, {"GITHUB_EVENT_NAME": "push", "GITHUB_EVENT_PATH": __file__}):
            with self.subTest(env=env):
                selected = policy.decision(env)
                self.assertEqual(selected["categories"], policy.ALL)
                self.assertTrue(selected["fresh"])
                self.assertIn("fallback", selected["reason"])

    def test_manual_full_release_and_test_iis_qualification(self):
        for env in ({"ASAP_FORCE_FULL_CI": "true"}, {"GITHUB_EVENT_NAME": "release"},
                    {"GITHUB_EVENT_NAME": "workflow_dispatch", "ASAP_FORCE_FULL_CI": "true"},
                    {"GITHUB_REF_TYPE": "tag", "GITHUB_REF_NAME": "v1.0.0-test.1", "GITHUB_EVENT_NAME": "push"},
                    {"GITHUB_EVENT_NAME": "workflow_dispatch", "ASAP_DEPLOY_TEST_IIS": "true"}):
            with self.subTest(env=env):
                selected = policy.decision(env)
                self.assertEqual(selected["categories"], policy.ALL)
                self.assertTrue(selected["fresh"])
        selected = policy.decision({"GITHUB_EVENT_NAME": "workflow_dispatch"})
        self.assertEqual(selected["categories"], policy.ALL)
        self.assertFalse(selected["fresh"])

    def test_invalid_path_lists_cannot_narrow_selection(self):
        for paths in ([], None, ["../database.sql"], ["a\\b"], ["/root"], ["a\nb"], ["a//b"], [""]):
            with self.subTest(paths=paths), self.assertRaises(policy.SelectionError):
                policy.paths_policy(paths)


class GitDiffTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.email", "ci@example.org")
        self.git("config", "user.name", "CI fixture")
        self.git("config", "core.autocrlf", "false")
        self.initial = self.commit("src/Asap.Web/Frontend/app.js", "base")

    def git(self, *args):
        return policy.git(*args, root=self.root)

    def commit(self, path, content):
        file = self.root / path
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(content)
        self.git("add", ".")
        self.git("commit", "-qm", "fixture")
        return self.git("rev-parse", "HEAD")

    def env(self, commit, event="pull_request"):
        return {"GITHUB_SHA": commit, "GITHUB_REPOSITORY": "clcdpc/asap-pocketbase", "GITHUB_EVENT_NAME": event,
                "GITHUB_REF": "refs/heads/main", "GITHUB_EVENT_PATH": str(self.root / "event.json")}

    def integration(self, advance_base=False):
        self.git("checkout", "-qb", "pr")
        head = self.commit("src/Asap.Web/Frontend/app.js", "PR")
        self.git("checkout", "-q", "main")
        base = self.commit("database/Asap.Database/Tables/New.sql", "schema") if advance_base else self.initial
        self.git("merge", "--no-ff", "-qm", "synthetic merge", head)
        tested = self.git("rev-parse", "HEAD")
        repo = {"full_name": "clcdpc/asap-pocketbase"}
        pr = {"number": 1, "state": "open", "draft": False, "author_association": "COLLABORATOR",
              "head": {"sha": head, "repo": repo}, "base": {"sha": base, "ref": "main", "repo": repo}}
        return {"pull_request": pr}, self.env(tested)

    def test_exact_pr_merge_selects_only_changed_dependencies(self):
        event, env = self.integration()
        paths = policy.changed_paths("pull_request", event, env, self.root, event["pull_request"])
        self.assertEqual(paths, ["src/Asap.Web/Frontend/app.js"])
        self.assertEqual(policy.paths_policy(paths)["categories"], ["core"])

    def test_base_changes_since_fork_are_included(self):
        event, env = self.integration(advance_base=True)
        paths = policy.changed_paths("pull_request", event, env, self.root, event["pull_request"])
        self.assertIn("database/Asap.Database/Tables/New.sql", paths)
        self.assertEqual(policy.paths_policy(paths)["categories"], policy.ALL)

    def test_changed_head_base_merge_parent_or_trust_cannot_use_selective_diff(self):
        event, env = self.integration()
        for field in ("head", "base", "draft", "trust", "fork", "closed"):
            current = copy.deepcopy(event["pull_request"])
            if field in ("head", "base"):
                current[field]["sha"] = "f" * 40
            elif field == "draft":
                current["draft"] = True
            elif field == "trust":
                current["author_association"] = "CONTRIBUTOR"
            elif field == "fork":
                current["head"]["repo"]["full_name"] = "outsider/repo"
            else:
                current["state"] = "closed"
            with self.subTest(field=field), self.assertRaises(policy.SelectionError):
                policy.changed_paths("pull_request", event, env, self.root, current)
        env["GITHUB_SHA"] = event["pull_request"]["head"]["sha"]
        with self.assertRaises(policy.SelectionError):
            policy.changed_paths("pull_request", event, env, self.root)

    def test_actual_decision_checks_live_pr_metadata_and_falls_back_on_failure(self):
        event, env = self.integration()
        (self.root / "event.json").write_text(json.dumps(event))
        env["GH_TOKEN"] = "fixture-token"
        with mock.patch.object(reuse.GitHub, "get", return_value=event["pull_request"]):
            self.assertEqual(policy.decision(env, self.root)["categories"], ["core"])
        with mock.patch.object(reuse.GitHub, "get", side_effect=RuntimeError("private details")):
            selected = policy.decision(env, self.root)
            self.assertEqual(selected["categories"], policy.ALL)
            self.assertNotIn("private details", selected["reason"])

    def test_main_push_diff_and_missing_or_forced_metadata(self):
        tested = self.commit("src/Asap.Web/Frontend/app.js", "push")
        event = {"before": self.initial, "after": tested, "ref": "refs/heads/main", "deleted": False, "forced": False}
        env = self.env(tested, "push")
        path = self.root / "event.json"
        path.write_text(json.dumps(event))
        self.assertEqual(policy.decision(env, self.root)["categories"], ["core"])
        for change in ({"before": "0" * 40}, {"before": "f" * 40}, {"forced": True}, {"after": self.initial},
                       {"deleted": True}, {"ref": "refs/heads/other"}):
            path.write_text(json.dumps({**event, **change}))
            selected = policy.decision(env, self.root)
            self.assertEqual(selected["categories"], policy.ALL)
            self.assertTrue(selected["fresh"])

    def test_renamed_or_deleted_dependency_is_still_required(self):
        before = self.commit("src/Asap.Migration/Source.cs", "source")
        self.git("mv", "src/Asap.Migration/Source.cs", "src/Asap.Web/Frontend/retired.txt")
        self.git("commit", "-qm", "rename")
        tested = self.git("rev-parse", "HEAD")
        event = {"before": before, "after": tested, "ref": "refs/heads/main", "deleted": False, "forced": False}
        paths = policy.changed_paths("push", event, self.env(tested, "push"), self.root)
        self.assertIn("src/Asap.Migration/Source.cs", paths)
        self.assertIn("migration", policy.paths_policy(paths)["categories"])


class PopulationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not (policy.ROOT / policy.APPLICATION).exists():
            raise unittest.SkipTest("Build Release tests before exercising installed native MTP discovery")
        cls.catalog = policy.discover()
        cls.partitions = {key: policy.discover(policy.FILTERS[key]) for key in policy.PARTITIONS}
        cls.reviewed = json.loads((policy.ROOT / "scripts/ci/reviewed-test-categories.json").read_bytes())

    def test_actual_discovery_is_a_disjoint_complete_union(self):
        policy.validate_partitions(self.catalog, self.partitions, self.reviewed)
        self.assertEqual(len(self.catalog), sum(len(value) for value in self.partitions.values()))
        self.assertTrue(any("PatronJourneyTests" in value["method"] for value in self.partitions["core"].values()))
        self.assertTrue(any("DacpacDeploysSql2022CompatibilityAndSingletonRows" in value["method"]
                            for value in self.partitions["core"].values()))

    def test_missing_duplicated_or_reclassified_test_cannot_pass(self):
        uid = next(iter(self.partitions["core"]))
        for mutation in ("missing", "duplicate", "reclassified"):
            parts = copy.deepcopy(self.partitions)
            if mutation != "duplicate":
                parts["core"].pop(uid)
            if mutation != "missing":
                parts["migration"][uid] = self.catalog[uid]
            with self.subTest(mutation=mutation), self.assertRaises(policy.SelectionError):
                policy.validate_partitions(self.catalog, parts, self.reviewed)

    def test_new_unclassified_test_is_required_core(self):
        catalog, parts = copy.deepcopy(self.catalog), copy.deepcopy(self.partitions)
        uid = "00000000-0000-0000-0000-000000000001"
        catalog[uid] = {"method": "Asap.Tests.Unit.NewTests.NewUnclassifiedTest", "name": "NewUnclassifiedTest"}
        with self.assertRaises(policy.SelectionError):
            policy.validate_partitions(catalog, parts, self.reviewed)
        parts["core"][uid] = catalog[uid]
        policy.validate_partitions(catalog, parts, self.reviewed)

    def test_reduced_result_cannot_satisfy_full_gate_or_same_count_wrong_tests(self):
        core_ids = sorted(fp.digest(uid) for uid in self.partitions["core"])
        full_ids = sorted(fp.digest(uid) for key in policy.ALL for uid in self.partitions[key])
        results = {"total": len(core_ids), "executed": len(core_ids), "passed": len(core_ids),
                   "failed": 0, "skipped": 0, "identities": core_ids}
        selected = {"minimum": len(full_ids), "expected": len(full_ids), "identities": full_ids}
        with self.assertRaises(reuse.EvidenceError):
            reuse.passing_results(results, selected)
        selected = {"minimum": len(core_ids), "expected": len(core_ids), "identities": core_ids}
        reuse.passing_results(results, selected)
        results["identities"] = sorted([fp.digest("different test, same count"), *core_ids[1:]])
        with self.assertRaises(reuse.EvidenceError):
            reuse.passing_results(results, selected)

    def test_benchmark_artifact_cannot_be_loaded_as_qualification(self):
        with tempfile.TemporaryDirectory() as name:
            directory = Path(name)
            value = {"schema": 1, "kind": "benchmark", "commit": policy.git("rev-parse", "HEAD")}
            (directory / "selection.json").write_text(json.dumps(value))
            with self.assertRaises(policy.SelectionError):
                policy.load(directory=directory)

    def test_malformed_empty_or_duplicated_native_discovery_fails(self):
        test = {"uid": "00000000-0000-0000-0000-000000000001", "displayName": "Case",
                "type": {"namespace": "Asap.Tests", "typeName": "Fixture", "methodName": "Case"}}
        for raw in ("not JSON", json.dumps({"schemaVersion": 2, "tests": [test]}),
                    json.dumps({"schemaVersion": 1, "tests": []}),
                    json.dumps({"schemaVersion": 1, "tests": [test, test]})):
            with self.subTest(raw=raw), mock.patch.object(subprocess, "check_output", return_value=raw), self.assertRaises(ValueError):
                policy.discover()


class FrontendSafetyTests(unittest.TestCase):
    def test_safety_runs_once_before_remaining_tests_and_reports_complete_union(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            shutil.copyfile(policy.ROOT / "tests/run_all.js", root / "run_all.js")
            files = ["ci_workflow_contract.test.js", "test_iis_deployment_contract.test.js", "ordinary.test.js"]
            marker = root / "executed"
            for file in files:
                (root / file).write_text("require('fs').appendFileSync(process.env.FIXTURE_MARKER, __filename + '\\n');")
            safety, results = root / "safety.json", root / "results.json"
            env = {**os.environ, "FIXTURE_MARKER": str(marker), "ASAP_CI_FRONTEND_RESULTS": str(safety)}
            subprocess.run(["node", "run_all.js", "--ci-safety"], cwd=root, env=env, capture_output=True, check=True)
            env.update(ASAP_CI_FRONTEND_RESULTS=str(results), ASAP_CI_FRONTEND_SAFETY=str(safety))
            subprocess.run(["node", "run_all.js", "--ci-remaining"], cwd=root, env=env, capture_output=True, check=True)
            self.assertEqual(len(marker.read_text().splitlines()), 3)
            raw = json.loads(results.read_bytes())
            self.assertEqual(raw["files"], sorted(files))
            self.assertEqual(raw["passed"], 3)
            self.assertEqual(raw["filter"], "")
            bad = json.loads(safety.read_bytes())
            for mutation in ({"failed": 1}, {"skipped": 1}, {"files": []}, {"passed": 0}):
                safety.write_text(json.dumps({**bad, **mutation}))
                run = subprocess.run(["node", "run_all.js", "--ci-remaining"], cwd=root, env=env, capture_output=True)
                self.assertNotEqual(run.returncode, 0)


if __name__ == "__main__":
    unittest.main()
