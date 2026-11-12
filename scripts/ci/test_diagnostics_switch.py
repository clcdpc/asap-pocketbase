"""Always-run checks for the diagnostic opt-in boundary, without sampling resources."""

import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


@unittest.skipUnless(shutil.which("bash"), "Bash wrapper")
class DiagnosticsSwitchTests(unittest.TestCase):
    def test_modes_preserve_workload_and_only_explicit_true_loads_collector(self):
        source = Path(__file__).with_name("run-with-performance.sh")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            wrapper = root / source.name
            shutil.copyfile(source, wrapper)
            # A collector stand-in records invocation and executes exactly the
            # supplied workload. No real resource probes or child trees here.
            wrapper.with_name("performance.py").write_text(
                "import os,sys\nfrom pathlib import Path\n"
                "if __name__ == '__main__':\n"
                " Path('collector-started').touch()\n"
                " args=sys.argv[sys.argv.index('--')+1:]\n"
                " os.execv(args[0],args)\n", encoding="utf-8")
            for value in (None, "false", "", "1", "TRUE", "true"):
                with self.subTest(value=value):
                    marker = root / "collector-started"
                    marker.unlink(missing_ok=True)
                    env = dict(os.environ, WORKLOAD_VALUE="kept")
                    env.pop("CI_PERFORMANCE_DIAGNOSTICS", None)
                    if value is not None:
                        env["CI_PERFORMANCE_DIAGNOSTICS"] = value
                    workload = [sys.executable, "-c",
                        "import os,sys; print(sys.argv[1]); print(sys.stdin.read()); "
                        "print(os.environ['WORKLOAD_VALUE'],file=sys.stderr); raise SystemExit(23)",
                        "argument with spaces & symbols"]
                    result = subprocess.run([shutil.which("bash"), str(wrapper), *workload],
                        cwd=root, env=env, input="input retained", capture_output=True, text=True, timeout=5)
                    self.assertEqual(result.returncode, 23, result.stderr)
                    self.assertEqual(result.stdout, "argument with spaces & symbols\ninput retained\n")
                    self.assertEqual(result.stderr, "kept\n")
                    self.assertEqual(marker.exists(), value == "true")

    def test_default_off_does_not_require_python_or_collector(self):
        source = Path(__file__).with_name("run-with-performance.sh")
        with tempfile.TemporaryDirectory() as directory:
            wrapper = Path(directory) / source.name
            shutil.copyfile(source, wrapper)
            env = dict(os.environ, PATH="", CI_PERFORMANCE_DIAGNOSTICS="false")
            result = subprocess.run([shutil.which("bash"), str(wrapper), sys.executable,
                "-c", "print('workload ran')"], env=env, capture_output=True, text=True, timeout=5)
            self.assertEqual(result.returncode, 0)
            self.assertEqual(result.stdout, "workload ran\n")
            self.assertEqual(result.stderr, "")


if __name__ == "__main__":
    unittest.main()
