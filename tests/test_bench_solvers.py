# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md section 5.4.2).
"""scripts/bench_solvers.py: the friendly message when tetra3rs is missing.

bench_solvers.py imports the database table from build_tetra3rs_catalog.py at
module level; that import must not need tetra3rs, or the "tetra3rs not
installed" message in main() is unreachable.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

_REPO = Path(__file__).resolve().parent.parent
_SCRIPTS = _REPO / "scripts"

# `sys.modules["tetra3rs"] = None` makes `import tetra3rs` raise ImportError.
_RUN_WITHOUT_TETRA3RS = """
import runpy, sys
sys.modules["tetra3rs"] = None
sys.argv = [{script!r}] + {args!r}
runpy.run_path({script!r}, run_name="__main__")
"""


def _run_without_tetra3rs(script: str, *args: str) -> subprocess.CompletedProcess:
    code = _RUN_WITHOUT_TETRA3RS.format(script=str(_SCRIPTS / script), args=list(args))
    return subprocess.run(
        [sys.executable, "-I", "-c", code],
        capture_output=True, text=True, timeout=60, cwd=str(_REPO),
    )


def test_bench_reports_a_missing_tetra3rs_instead_of_a_traceback():
    proc = _run_without_tetra3rs("bench_solvers.py")
    assert proc.returncode == 2
    assert "tetra3rs not installed" in proc.stderr
    assert "Traceback" not in proc.stderr


def test_bench_db_option_still_works_without_tetra3rs_for_the_message():
    proc = _run_without_tetra3rs("bench_solvers.py", "--db", "wide")
    assert proc.returncode == 2
    assert "tetra3rs not installed" in proc.stderr


def test_database_table_is_importable_without_tetra3rs():
    code = (
        "import sys\n"
        "sys.modules['tetra3rs'] = None\n"
        f"sys.path.insert(0, {str(_SCRIPTS)!r})\n"
        "import build_tetra3rs_catalog as b\n"
        "print(sorted(b.DB_SPECS))\n"
    )
    proc = subprocess.run([sys.executable, "-I", "-c", code], capture_output=True, text=True, timeout=60)
    assert proc.returncode == 0, proc.stderr
    assert "['narrow', 'wide']" in proc.stdout
