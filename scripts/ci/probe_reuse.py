"""Read-only live Actions provenance/invalidation/corruption probes; never executes a test suite."""

import argparse
import copy
import json
import os
from pathlib import Path

import fingerprints as fp
import reuse


class Origin:
    def __init__(self, api, run_id, corrupt=False):
        self.api, self.run_id, self.corrupt = api, run_id, corrupt

    def get(self, path):
        return self.api.get(path)

    def request(self, path, **kwargs):
        body = self.api.request(path, **kwargs)
        if self.corrupt and path.endswith("/zip"):
            return body[:-1] + bytes([body[-1] ^ 1])
        return body

    def pages(self, path, key, **kwargs):
        if path.startswith("/actions/workflows/dotnet.yml/runs?"):
            return iter([self.api.get(f"/actions/runs/{self.run_id}")])
        return self.api.pages(path, key, **kwargs)


def probes(config, current, run_id, api_factory):
    changed = copy.deepcopy(current)
    identity = changed["suites"]["browser"]
    identity["selection"]["expected"] += 1
    identity["fingerprint"] = fp.digest({k: identity[k] for k in ("schema", "suite", "source", "environment", "selection")})
    cases = (
        ("baseline", current, False, {s: True for s in fp.SUITES}),
        ("browser-count-invalidation", changed, False, {"dotnet": True, "browser": False, "frontend": True}),
        ("corrupt-artifact-fallback", current, True, {s: False for s in fp.SUITES}),
    )
    report = {}
    for name, inputs, corrupt, expected in cases:
        decisions = reuse.lookup(Origin(api_factory(), run_id, corrupt), config, inputs)
        actual = {s: d["reuse"] for s, d in decisions.items()}
        report[name] = {"passed": actual == expected, "decisions": decisions}
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", type=int, required=True)
    parser.add_argument("--current", type=Path, required=True, help="current.json from that run's verification artifact")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    config = fp.configuration()
    current = json.loads(args.current.read_bytes())
    report = probes(config, current, args.run, lambda: reuse.GitHub(config["repository"], os.environ["GH_TOKEN"]))
    reuse.write_json(args.output, report)
    for name, case in report.items():
        print(f"{name}: {'PASS' if case['passed'] else 'FAIL'}; "
              + ", ".join(f"{s}={'HIT' if d['reuse'] else 'MISS'}" for s, d in case["decisions"].items()))
    reuse.require(all(case["passed"] for case in report.values()), "Live reuse decision probe failed")


if __name__ == "__main__":
    main()
