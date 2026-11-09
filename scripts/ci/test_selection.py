"""Exercise the real selector, Git integration diffs, native discovery and result gate."""

import copy
import json
import os
from pathlib import Path
import shutil
import signal
import time
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

    def test_unknown_implementation_and_deleted_contracts_require_complete_population(self):
        for path in ("src/Retired/Source.cs", "unknown/fixture.json"):
            selected = policy.paths_policy([path])
            self.assertEqual(selected["categories"], policy.ALL)
            self.assertFalse(selected["fresh"])

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
        self.assertEqual(policy.paths_policy(["src/Asap.Web/Features/Patron/PatronSessionService.cs"])["categories"], ["core"])

    def test_selection_and_enforcement_changes_allow_matching_full_evidence(self):
        for path in (".github/workflows/dotnet.yml", "scripts/ci/selection.py", "scripts/ci/reviewed-test-categories.json",
                     "scripts/ci/fingerprints.py", "scripts/ci/reuse.py", "scripts/ci/test_selection.py",
                     "scripts/deployment/Deploy-AsapTest.ps1", "tests/Asap.Tests/TestCategories.cs",
                     "tests/ci_workflow_contract.test.js", "tests/test_iis_deployment_contract.test.js",
                     "tests/run_all.js", "tests/browser/run.cjs", "src/Asap.Web/Asap.Web.csproj",
                     "docs/implementation/ci.md", "README.md"):
            with self.subTest(path=path):
                selected = policy.paths_policy([path])
                self.assertEqual(selected["categories"], policy.ALL)
                self.assertFalse(selected["fresh"])

    def test_missing_invalid_or_unsupported_metadata_fails_closed(self):
        for env in ({}, {"GITHUB_EVENT_NAME": "schedule"}, {"GITHUB_EVENT_NAME": "push"},
                    {"GITHUB_EVENT_NAME": "pull_request"}, {"GITHUB_EVENT_NAME": "push", "GITHUB_EVENT_PATH": __file__}):
            with self.subTest(env=env):
                selected = policy.decision(env)
                self.assertEqual(selected["categories"], policy.ALL)
                self.assertTrue(selected["fresh"])
                self.assertIn("fallback", selected["reason"])

    def test_only_explicit_force_demands_fresh_manual_qualification(self):
        for env in ({"ASAP_FORCE_FULL_CI": "true"},
                    {"GITHUB_EVENT_NAME": "workflow_dispatch", "ASAP_FORCE_FULL_CI": "true"}):
            with self.subTest(env=env):
                selected = policy.decision(env)
                self.assertEqual(selected["categories"], policy.ALL)
                self.assertTrue(selected["fresh"])
        selected = policy.decision({"GITHUB_EVENT_NAME": "workflow_dispatch"})
        self.assertEqual(selected["categories"], policy.ALL)
        self.assertFalse(selected["fresh"])
        selected = policy.decision({"GITHUB_EVENT_NAME": "workflow_dispatch", "ASAP_DEPLOY_TEST_IIS": "true"})
        self.assertFalse(selected["fresh"])

    def test_invalid_path_lists_cannot_narrow_selection(self):
        for paths in ([], None, ["../database.sql"], ["a\\b"], ["/root"], ["a\nb"], ["a//b"], [""],
                      ["scripts/ci/reuse.py", "../invalid"]):
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
        self.git("update-ref", "refs/remotes/origin/main", base)
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

    def test_lightweight_and_annotated_tag_pushes_allow_same_commit_reuse(self):
        for annotated in (False, True):
            tag = "v1.0.0-test." + str(int(annotated))
            self.git("tag", *(["-a", "-m", "test qualification"] if annotated else []), tag, self.initial)
            ref = "refs/tags/" + tag
            event = {"before": "0" * 40, "after": self.git("rev-parse", ref), "ref": ref,
                     "created": True, "deleted": False, "forced": False}
            (self.root / "event.json").write_text(json.dumps(event))
            env = {**self.env(self.initial, "push"), "GITHUB_REF": ref, "GITHUB_REF_TYPE": "tag"}
            with self.subTest(annotated=annotated):
                selected = policy.decision(env, self.root)
                self.assertEqual(selected["categories"], policy.ALL)
                self.assertFalse(selected["fresh"], selected)

    def test_same_commit_manual_branch_release_and_deployment_events_allow_reuse(self):
        tag = "v1.0.0-test.9"
        self.git("tag", tag, self.initial)
        tag_ref = "refs/tags/" + tag
        cases = (
            ("workflow_dispatch", tag_ref, {}),
            ("release", tag_ref, {"release": {"tag_name": tag}}),
            ("push", "refs/heads/topic", {"ref": "refs/heads/topic", "before": self.initial,
                                         "after": self.initial, "deleted": False, "forced": False}),
            ("push", "refs/heads/new", {"ref": "refs/heads/new", "before": "0" * 40,
                                       "after": self.initial, "created": True, "deleted": False, "forced": False}),
            ("deployment", "", {"deployment": {"sha": self.initial}}),
            ("deployment_status", tag_ref, {"deployment": {"sha": self.initial}}),
            ("schedule", "refs/heads/main", {}),
        )
        for event_name, ref, event in cases:
            (self.root / "event.json").write_text(json.dumps(event))
            env = {**self.env(self.initial, event_name), "GITHUB_REF": ref, "ASAP_DEPLOY_TEST_IIS": "true",
                   "GITHUB_REF_TYPE": "tag" if ref.startswith("refs/tags/") else "branch"}
            with self.subTest(event=event_name, ref=ref):
                selected = policy.decision(env, self.root)
                self.assertEqual(selected["categories"], policy.ALL)
                self.assertFalse(selected["fresh"], selected)
                self.assertTrue(policy.decision({**env, "ASAP_FORCE_FULL_CI": "true"}, self.root)["fresh"])

    def test_invalid_tag_release_and_deployment_metadata_still_demands_fresh_execution(self):
        tag = "v1.0.0-test.9"
        self.git("tag", tag, self.initial)
        ref = "refs/tags/" + tag
        for event_name, event, extra in (
                ("push", {"ref": ref, "after": "f" * 40, "deleted": False, "forced": False}, {}),
                ("push", {"ref": ref, "after": self.initial, "deleted": True, "forced": False}, {}),
                ("workflow_dispatch", {}, {"GITHUB_REF": "refs/tags/missing"}),
                ("workflow_dispatch", {}, {"GITHUB_REF_TYPE": "branch"}),
                ("release", {"release": {"tag_name": "different-tag"}}, {}),
                ("deployment", {"deployment": {"sha": "f" * 40}}, {})):
            (self.root / "event.json").write_text(json.dumps(event))
            env = {**self.env(self.initial, event_name), "GITHUB_REF": ref, "GITHUB_REF_TYPE": "tag", **extra}
            with self.subTest(event=event_name, extra=extra):
                self.assertTrue(policy.decision(env, self.root)["fresh"])
        tested = self.commit("code.txt", "another commit")
        (self.root / "event.json").write_text("{}")
        env = {**self.env(tested, "workflow_dispatch"), "GITHUB_REF": ref, "GITHUB_REF_TYPE": "tag"}
        self.assertTrue(policy.decision(env, self.root)["fresh"])

    def test_base_changes_since_fork_are_included(self):
        event, env = self.integration(advance_base=True)
        paths = policy.changed_paths("pull_request", event, env, self.root, event["pull_request"])
        self.assertIn("database/Asap.Database/Tables/New.sql", paths)
        self.assertEqual(policy.paths_policy(paths)["categories"], policy.ALL)

    def test_stale_advertised_base_uses_verified_main_merge_parent(self):
        event, env = self.integration(advance_base=True)
        event["pull_request"]["base"]["sha"] = self.initial
        current = copy.deepcopy(event["pull_request"])
        paths = policy.changed_paths("pull_request", event, env, self.root, current)
        self.assertIn("database/Asap.Database/Tables/New.sql", paths)
        (self.root / "event.json").write_text(json.dumps(event))
        with mock.patch.object(reuse.GitHub, "get", return_value=current):
            self.assertFalse(policy.decision({**env, "GH_TOKEN": "fixture"}, self.root)["fresh"])

    def test_merge_parent_must_belong_to_trusted_main_history(self):
        event, env = self.integration(advance_base=True)
        self.git("update-ref", "refs/remotes/origin/main", self.initial)
        with self.assertRaises(policy.SelectionError):
            policy.changed_paths("pull_request", event, env, self.root, event["pull_request"])

    def test_unfetched_new_advertised_base_does_not_change_planning_or_final_verification(self):
        event, _ = self.integration(advance_base=True)
        tested, base = self.git("rev-parse", "HEAD"), event["pull_request"]["base"]["sha"]
        self.git("branch", "tested-merge", tested)
        self.git("checkout", "-q", "--detach", base)
        self.git("branch", "-f", "main", base)
        self.git("checkout", "-q", "main")
        with tempfile.TemporaryDirectory() as name:
            runner = Path(name) / "runner"
            subprocess.check_call(["git", "clone", "--no-local", "--quiet", str(self.root), str(runner)])
            policy.git("checkout", "-q", "--detach", tested, root=runner)
            event_path = runner / ".git/event.json"
            event_path.write_text(json.dumps(event))
            env = {**self.env(tested), "GITHUB_EVENT_PATH": str(event_path), "GH_TOKEN": "fixture",
                   "GITHUB_REF": "refs/pull/1/merge", "ASAP_FORCE_FULL_CI": "false"}
            current = copy.deepcopy(event["pull_request"])
            directory = runner / ".artifacts/test-selection"
            suites = {suite: {"expected": 1} for suite in fp.SUITES}
            with mock.patch.dict(os.environ, env), \
                 mock.patch.object(reuse.GitHub, "get", side_effect=lambda path: copy.deepcopy(current)), \
                 mock.patch.object(policy, "population", return_value=(suites, 0, {})), \
                 mock.patch("sys.stdout"):
                policy.prepare(runner, directory)
                planned = policy.load(runner, directory=directory)
                newer = self.commit("src/new-main-change.txt", "main advanced after runner clone")
                self.assertNotEqual(newer, base)
                self.assertNotEqual(subprocess.run(["git", "cat-file", "-e", newer], cwd=runner,
                                                   stderr=subprocess.DEVNULL).returncode, 0)
                current["base"]["sha"] = newer
                verified = policy.load(runner, verify=True, directory=directory)
                self.assertFalse(verified["decision"]["fresh"])
                self.assertEqual(verified, planned)

    def test_changed_head_base_merge_parent_or_trust_cannot_use_selective_diff(self):
        event, env = self.integration()
        for field in ("head", "base", "draft", "trust", "fork", "closed"):
            current = copy.deepcopy(event["pull_request"])
            if field in ("head", "base"):
                current[field]["sha"] = "invalid" if field == "base" else "f" * 40
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
            self.assertTrue(selected["fresh"])
            self.assertNotIn("private details", selected["reason"])

    def test_main_push_diff_and_missing_or_forced_metadata(self):
        tested = self.commit("src/Asap.Web/Frontend/app.js", "push")
        event = {"before": self.initial, "after": tested, "ref": "refs/heads/main", "deleted": False, "forced": False}
        env = self.env(tested, "push")
        path = self.root / "event.json"
        path.write_text(json.dumps(event))
        self.assertEqual(policy.decision(env, self.root)["categories"], ["core"])
        for change in ({"before": "0" * 40}, {"before": "f" * 40}, {"forced": "true"}, {"after": self.initial},
                       {"deleted": True}, {"ref": "refs/heads/other"}):
            path.write_text(json.dumps({**event, **change}))
            selected = policy.decision(env, self.root)
            self.assertEqual(selected["categories"], policy.ALL)
            self.assertTrue(selected["fresh"])


    def test_renamed_or_deleted_dependency_is_still_required(self):
        before = self.commit("src/Unreviewed/Source.cs", "source")
        self.git("mv", "src/Unreviewed/Source.cs", "src/Asap.Web/Frontend/retired.txt")
        self.git("commit", "-qm", "rename")
        tested = self.git("rev-parse", "HEAD")
        event = {"before": before, "after": tested, "ref": "refs/heads/main", "deleted": False, "forced": False}
        paths = policy.changed_paths("push", event, self.env(tested, "push"), self.root)
        self.assertIn("src/Unreviewed/Source.cs", paths)
        self.assertFalse(policy.paths_policy(paths)["fresh"])


class PopulationIdentityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "scripts/ci").mkdir(parents=True)
        (self.root / "tests").mkdir()
        (self.root / "tests/frontend.test.js").touch()
        portable_fixture(self.root / "tests")
        self.parts = {key: {key + str(i): {"method": "Asap.Tests." + key + ".Case" + str(i), "name": key}
                           for i in range(8)}
                      for key in policy.PARTITIONS}

    def population(self):
        reviewed = {"Browser": [value["method"] for value in self.parts["browser"].values()]}
        (self.root / "scripts/ci/reviewed-test-categories.json").write_text(json.dumps(reviewed))
        def discover(expression=None, root=None):
            if expression is None:
                return {uid: value for tests in self.parts.values() for uid, value in tests.items()}
            return self.parts[next(key for key, value in policy.FILTERS.items() if value == expression)]
        with mock.patch.object(policy, "discover", side_effect=discover):
            return policy.population(policy.full("complete", fresh=False), self.root)[0]

    def test_another_partition_or_frontend_population_does_not_invalidate_native_suite(self):
        before = self.population()
        self.parts["browser"]["new"] = {"method": "Asap.Tests.browser.New", "name": "new"}
        after = self.population()
        for suite in ("dotnet_a", "dotnet_b"):
            self.assertEqual(before[suite], after[suite])
        self.assertTrue(any(before[suite] != after[suite] for suite in ("browser_a", "browser_b")))
        (self.root / "tests/new.test.js").touch()
        with_frontend = self.population()
        for suite in policy.NATIVE_SUITES:
            self.assertEqual(after[suite], with_frontend[suite])
        self.assertNotEqual(after["frontend"], with_frontend["frontend"])

    def test_reclassification_changes_both_affected_suite_keys(self):
        before = self.population()
        self.parts["core"]["other"] = {"method": "Asap.Tests.core.Other", "name": "other"}
        self.parts["browser"]["core0"] = self.parts["core"].pop("core0")
        after = self.population()
        for kind in ("dotnet_", "browser_"):
            self.assertTrue(any(before[suite] != after[suite] for suite in policy.NATIVE_SUITES if suite.startswith(kind)))

    def test_additions_removals_and_seed_changes_cannot_reuse_stale_populations(self):
        before = self.population()
        method = "Asap.Tests.core.NewParameterizedMethod"
        suite = "dotnet_" + ("a" if policy.worker_partition(method) == 0 else "b")
        self.parts["core"]["new-row-1"] = {"method": method, "name": "row 1"}
        self.parts["core"]["new-row-2"] = {"method": method, "name": "row 2"}
        added = self.population()
        self.assertEqual(added[suite]["expected"], before[suite]["expected"] + 2)
        self.assertTrue({"new-row-1", "new-row-2"} <= set(added[suite]["uids"]))
        for other in set(policy.NATIVE_SUITES) - {suite}:
            self.assertEqual(before[other], added[other])
        del self.parts["core"]["new-row-1"]
        removed = self.population()
        self.assertNotEqual(added[suite], removed[suite])
        with mock.patch.object(policy, "PARTITION_SEED", "changed-partition-definition"):
            changed = self.population()
        for suite in policy.NATIVE_SUITES:
            self.assertNotEqual(removed[suite], changed[suite])

    def test_empty_worker_fails_closed(self):
        with mock.patch.object(policy, "worker_partition", return_value=0), self.assertRaises(policy.SelectionError):
            self.population()

    def test_group_definition_changes_invalidate_every_partition_identity(self):
        before = self.population()
        with mock.patch.dict(policy.METHOD_ANCHORS, {"Asap.Tests.core.Case0": "Asap.Tests.core.Case1"}):
            changed = self.population()
        for suite in policy.NATIVE_SUITES:
            self.assertNotEqual(before[suite]["partition"], changed[suite]["partition"])


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

    def test_four_workers_cover_exact_native_population_and_keep_method_rows_together(self):
        workers = policy.worker_populations(self.partitions)
        owned, methods = set(), {}
        for suite, tests in workers.items():
            self.assertTrue(tests)
            self.assertFalse(owned.intersection(tests))
            owned.update(tests)
            for value in tests.values():
                self.assertEqual(methods.setdefault(value["method"], suite), suite)
        self.assertEqual(owned, set(self.catalog))
        for category, kind in (("core", "dotnet"), ("browser", "browser")):
            self.assertEqual(set(workers[kind + "_a"]) | set(workers[kind + "_b"]), set(self.partitions[category]))

    def test_weekly_recipient_state_group_stays_together_and_cannot_lose_its_anchor(self):
        workers = policy.worker_populations(self.partitions)
        for method, anchor in policy.METHOD_ANCHORS.items():
            methods = {value["method"] for value in self.catalog.values()}
            self.assertTrue({method, anchor} <= methods)
            self.assertEqual(policy.worker_partition(method), policy.worker_partition(anchor))
            owner = next(tests for tests in workers.values() if any(value["method"] == method for value in tests.values()))
            self.assertIn(anchor, {value["method"] for value in owner.values()})
            parts = copy.deepcopy(self.partitions)
            parts["core"] = {uid: value for uid, value in parts["core"].items() if value["method"] != anchor}
            with self.assertRaises(policy.SelectionError):
                policy.worker_populations(parts)

    def test_native_uid_filter_executes_the_exact_selected_population(self):
        workers = policy.worker_populations(self.partitions)
        with tempfile.TemporaryDirectory() as name:
            for suite, tests in workers.items():
                response = Path(name) / (suite + ".rsp")
                response.write_text("\n".join(["--list-tests", "json", "--no-ansi", "--filter-uid", *sorted(tests)]) + "\n")
                raw = subprocess.check_output(["dotnet", policy.APPLICATION, "@" + str(response)],
                                              cwd=policy.ROOT, text=True, timeout=90)
                self.assertEqual(sorted(test["uid"] for test in json.loads(raw)["tests"]), sorted(tests), suite)

    def test_missing_duplicated_or_reclassified_test_cannot_pass(self):
        uid = next(iter(self.partitions["core"]))
        for mutation in ("missing", "duplicate", "reclassified"):
            parts = copy.deepcopy(self.partitions)
            if mutation != "duplicate":
                parts["core"].pop(uid)
            if mutation != "missing":
                parts["browser"][uid] = self.catalog[uid]
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
        core_ids = sorted(fp.digest(uid) for uid in self.partitions["core"])[1:]
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

    def test_untrusted_plan_kind_cannot_be_loaded_as_qualification(self):
        with tempfile.TemporaryDirectory() as name:
            directory = Path(name)
            value = {"schema": 2, "kind": "reused", "commit": policy.git("rev-parse", "HEAD")}
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


def portable_fixture(directory):
    declaration = json.loads((policy.ROOT / "tests/frontend-tests.json").read_bytes())
    (directory / "frontend-tests.json").write_text(json.dumps(declaration))
    for file in declaration["portable"]:
        path = directory / file
        path.parent.mkdir(exist_ok=True)
        path.write_text("require('node:assert/strict').equal(1, 1);")
    return declaration["portable"]


class FrontendSafetyTests(unittest.TestCase):
    def runner_fixture(self, root):
        shutil.copyfile(policy.ROOT / "tests/run_all.js", root / "run_all.js")
        portable_fixture(root)

    @unittest.skipIf(os.name == "nt", "POSIX process-group cancellation contract")
    def test_cancellation_kills_uncooperative_child_and_cannot_emit_passing_evidence(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            self.runner_fixture(root)
            (root / "a.test.js").write_text(
                "const fs=require('fs');process.on('SIGTERM',()=>{});"
                "fs.writeFileSync('child.pid.pending',String(process.pid));"
                "fs.renameSync('child.pid.pending','child.pid');setInterval(()=>{},1000);")
            results = root / "frontend-results.json"
            env = {**os.environ, "ASAP_CI_FRONTEND_RESULTS": str(results)}
            runner = subprocess.Popen(["node", "run_all.js"], cwd=root, env=env,
                                      stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            child = None
            try:
                deadline = time.monotonic() + 5
                while not (root / "child.pid").exists():
                    self.assertIsNone(runner.poll(), "Runner exited before its child started")
                    self.assertLess(time.monotonic(), deadline, "Child startup timed out")
                    time.sleep(0.02)
                child = int((root / "child.pid").read_text())
                runner.send_signal(signal.SIGTERM)
                runner.communicate(timeout=12)
                self.assertNotEqual(runner.returncode, 0)
                raw = json.loads(results.read_text())
                self.assertEqual(raw["passed"], 0)
                self.assertEqual(raw["failed"], len(raw["files"]))
                with self.assertRaises(reuse.EvidenceError):
                    reuse.execution_results("frontend", {"files": raw["files"], "minimum": len(raw["files"])}, root)
                with self.assertRaises(ProcessLookupError):
                    os.kill(child, 0)
            finally:
                if runner.poll() is None:
                    runner.terminate()
                    try:
                        runner.communicate(timeout=8)
                    except subprocess.TimeoutExpired:
                        runner.kill()
                        runner.communicate(timeout=5)
                if child is not None:
                    try:
                        os.killpg(child, signal.SIGKILL)
                    except ProcessLookupError:
                        pass

    def test_serial_runner_preserves_all_failures_and_completion_order(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            self.runner_fixture(root)
            for own, previous, code in (("a", None, 2), ("b", "a", 0), ("c", "b", 3)):
                prerequisite = ("if(!fs.existsSync('" + previous + ".done'))process.exit(99);"
                                if previous else "")
                (root / (own + ".test.js")).write_text(
                    "const fs=require('fs');" + prerequisite +
                    "fs.writeFileSync('" + own + ".done','');console.log('" + own + " output');"
                    "process.exit(" + str(code) + ");")
            results = root / "results.json"
            env = {**os.environ, "ASAP_CI_FRONTEND_RESULTS": str(results)}
            run = subprocess.run(["node", "run_all.js"], cwd=root, env=env, capture_output=True, text=True, timeout=15)
            self.assertEqual(run.returncode, 1)
            raw = json.loads(results.read_text())
            self.assertEqual((raw["executed"], raw["passed"], raw["failed"]), (5, 3, 2))
            self.assertEqual(raw["workers"], 1)
            self.assertEqual([row["code"] for row in raw["file_results"]], [2, 0, 0, 0, 3])
            self.assertLess(run.stdout.index("a output"), run.stdout.index("b output"))
            self.assertLess(run.stdout.index("b output"), run.stdout.index("c output"))


    def test_safety_runs_once_before_remaining_tests_and_reports_complete_union(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            self.runner_fixture(root)
            files = ["ci_workflow_contract.test.js", "test_iis_deployment_contract.test.js", "ordinary.test.js",
                     *json.loads((root / "frontend-tests.json").read_bytes())["portable"]]
            marker = root / "executed"
            for file in files:
                (root / file).write_text("require('fs').appendFileSync(process.env.FIXTURE_MARKER, __filename + '\\n');")
            safety, results = root / "safety.json", root / "results.json"
            env = {**os.environ, "FIXTURE_MARKER": str(marker), "ASAP_CI_FRONTEND_RESULTS": str(safety)}
            subprocess.run(["node", "run_all.js", "--ci-safety"], cwd=root, env=env, capture_output=True, check=True)
            env.update(ASAP_CI_FRONTEND_RESULTS=str(results), ASAP_CI_FRONTEND_SAFETY=str(safety))
            subprocess.run(["node", "run_all.js", "--ci-remaining"], cwd=root, env=env, capture_output=True, check=True)
            self.assertEqual(len(marker.read_text().splitlines()), len(files))
            self.assertEqual(len(set(marker.read_text().splitlines())), len(files))
            raw = json.loads(results.read_bytes())
            self.assertEqual(raw["files"], sorted(files))
            self.assertEqual(raw["passed"], len(files))
            self.assertEqual(raw["filter"], "")
            bad = json.loads(safety.read_bytes())
            for mutation in ({"failed": 1}, {"skipped": 1}, {"files": []}, {"passed": 0},
                             {"file_results": []}, {"file_results": [bad["file_results"][0]] * 2}):
                safety.write_text(json.dumps({**bad, **mutation}))
                run = subprocess.run(["node", "run_all.js", "--ci-remaining"], cwd=root, env=env, capture_output=True)
                self.assertNotEqual(run.returncode, 0)

    def test_normal_discovery_failure_filter_and_missing_portable_file_fail_qualification(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            tests = root / "tests"
            tests.mkdir()
            self.runner_fixture(tests)
            selected = {"files": policy.frontend_files(root)}
            selected.update(minimum=len(selected["files"]), expected=len(selected["files"]))
            env = {**os.environ, "ASAP_CI_FRONTEND_RESULTS": str(tests / "frontend-results.json")}
            def run(*args):
                return subprocess.run(["node", "run_all.js", *args], cwd=tests, env=env,
                                      capture_output=True, timeout=15)
            self.assertEqual(run().returncode, 0)
            self.assertEqual(reuse.execution_results("frontend", selected, tests)["passed"], len(selected["files"]))
            self.assertEqual(run("authentication").returncode, 0)
            with self.assertRaises(reuse.EvidenceError):
                reuse.execution_results("frontend", selected, tests)
            (tests / "acceptance/runner.test.cjs").write_text("require('node:assert/strict').equal(1, 2);")
            self.assertNotEqual(run().returncode, 0)
            with self.assertRaises(reuse.EvidenceError):
                reuse.execution_results("frontend", selected, tests)
            (tests / "acceptance/runner.test.cjs").unlink()
            self.assertNotEqual(run().returncode, 0)
            with self.assertRaises(policy.SelectionError):
                policy.frontend_files(root)


if __name__ == "__main__":
    unittest.main()
