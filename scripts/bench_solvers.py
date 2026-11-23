#!/usr/bin/env python3
"""tetra3rs quality + perf regression harness.

Solves each frame in tests/samples/ with tetra3rs and prints timing
and solution quality.  Use this to verify all sample frames still solve
after solver or catalog changes, and to measure wall-time performance on
different hardware (laptop, Pi 4, Pi Zero 2 W).

Run (after `uv sync`):

    uv run python scripts/bench_solvers.py

Baseline numbers and historical findings live in:
  specs/start/SPEC_SOLVER_PERFORMANCE.md
  docs/superpowers/specs/2026-05-18-tetra3rs-bench-findings.md

The script loads the production catalog at data/tetra3rs_gaia.bin
(`--db narrow`, the default) or data/tetra3rs_gaia_wide.bin (`--db wide`).
If the file is missing (unlikely outside the repo), it falls back to
.cache/<file>, regenerating it from the bundled gaia-catalog package if
needed (~30 s, one-time).

The samples in tests/samples are 8.86 deg frames, so only the narrow
database solves them. `--db wide` runs the same harness with the FOV
estimate at the centre of the wide range (override with --fov /
--fov-error); until wide-field samples exist (spec task 6.3) every sample
is expected to report FAIL.
"""

from __future__ import annotations

import argparse
import math
import platform
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SAMPLES_DIR = REPO_ROOT / "tests" / "samples"
CACHE_DIR = REPO_ROOT / ".cache"

# fork: camera-controls (see specs/camera-server/SPEC_CAMERA_CONTROLS.md)
# Database parameters (file, FOV range, magnitude) live in one place:
# the spec table in build_tetra3rs_catalog.py.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_tetra3rs_catalog import (  # noqa: E402
    DB_SPECS,
    EPOCH_PROPER_MOTION_YEAR,
    PATTERN_MAX_ERROR,
    VERIFICATION_STARS_PER_FOV,
)

# Match the production solve params from evf.solver.solver for the
# narrow database; the wide database uses the centre of its range.
FOV_DEG = 8.86
FOV_MAX_ERROR_DEG = 1.5

SAMPLES = ["a.png", "b.png", "c.png", "d.png", "orion.png"]


_t3rs_db = None
_db_name = "narrow"
_fov_deg = FOV_DEG
_fov_max_error_deg = FOV_MAX_ERROR_DEG


def get_or_build_t3rs_db(db_name: str = "narrow"):
    import tetra3rs

    spec = DB_SPECS[db_name]
    prod_path = REPO_ROOT / "data" / spec.filename
    cache_path = CACHE_DIR / spec.filename

    # Prefer the production prebuilt so we measure exactly what ships.
    if prod_path.exists():
        print(f"[tetra3rs] loading production db from {prod_path.relative_to(REPO_ROOT)}")
        return tetra3rs.SolverDatabase.load_from_file(str(prod_path))

    # Fall back to cached regen (or generate fresh if needed).
    if cache_path.exists():
        print(f"[tetra3rs] production db missing; loading cache from {cache_path.relative_to(REPO_ROOT)}")
        return tetra3rs.SolverDatabase.load_from_file(str(cache_path))

    print(
        f"[tetra3rs] generating {db_name} database "
        f"(FOV {spec.min_fov_deg}°..{spec.max_fov_deg}°) "
        "from bundled gaia-catalog — one-time, ~30 s..."
    )
    CACHE_DIR.mkdir(exist_ok=True)
    t0 = time.perf_counter()
    db = tetra3rs.SolverDatabase.generate_from_gaia(
        max_fov_deg=spec.max_fov_deg,
        min_fov_deg=spec.min_fov_deg,
        star_max_magnitude=spec.star_max_magnitude,
        pattern_max_error=PATTERN_MAX_ERROR,
        verification_stars_per_fov=VERIFICATION_STARS_PER_FOV,
        epoch_proper_motion_year=EPOCH_PROPER_MOTION_YEAR,
    )
    db.save_to_file(str(cache_path))
    print(
        f"[tetra3rs] built in {time.perf_counter() - t0:.1f}s "
        f"({db.num_stars} stars, {db.num_patterns} patterns) → "
        f"{cache_path.relative_to(REPO_ROOT)}"
    )
    return db


def solve_with_tetra3rs(path: Path) -> dict:
    import numpy as np
    import tetra3rs
    from PIL import Image

    global _t3rs_db
    if _t3rs_db is None:
        _t3rs_db = get_or_build_t3rs_db(_db_name)

    img = Image.open(path).convert("L")
    arr = np.asarray(img, dtype=np.float64)

    # Mirror tetra3's get_centroids_from_image filters as closely as the
    # tetra3rs API allows:
    #   tetra3 sigma=2 (Gaussian-blur on raw image)  → tetra3rs
    #     matched_filter_sigma=2.0 (Gaussian matched filter; suppresses
    #     extended emission like M42 nebulosity vs star-PSF responses).
    #   tetra3 max_area=2000  → tetra3rs max_pixels=2000 (blob-size cap;
    #     belt-and-braces vs extended sources).
    #   tetra3 pattern_checking_stars=30  → tetra3rs max_centroids=30
    #     (brightest-N cap; keeps the matcher's input clean).
    t0 = time.perf_counter()
    extraction = tetra3rs.extract_centroids(
        arr,
        sigma_threshold=5.0,
        max_pixels=2000,
        max_centroids=30,
        matched_filter_sigma=2.0,
    )
    t_extract_ms = (time.perf_counter() - t0) * 1000

    t0 = time.perf_counter()
    result = _t3rs_db.solve_from_centroids(
        extraction.centroids,
        fov_estimate_deg=_fov_deg,
        fov_max_error_deg=_fov_max_error_deg,
        image_shape=arr.shape,
    )
    t_solve_ms = (time.perf_counter() - t0) * 1000

    if result is None:
        return {
            "ok": False,
            "ra": None, "dec": None, "roll": None,
            "matches": 0, "prob": None, "rmse_arcsec": None,
            "status": "no-result",
            "n_centroids": len(extraction.centroids),
            "ms_extract": t_extract_ms,
            "ms_solve": t_solve_ms,
            "ms_wall": t_extract_ms + t_solve_ms,
            "ms_internal": None,
        }

    ok = result.ra_deg is not None and "match_found" in str(result.status).lower()
    return {
        "ok": ok,
        "ra": result.ra_deg,
        "dec": result.dec_deg,
        "roll": result.roll_deg,
        "matches": result.num_matches or 0,
        "prob": result.probability,
        "rmse_arcsec": result.rmse_arcsec,
        "status": str(result.status),
        "n_centroids": len(extraction.centroids),
        "ms_extract": t_extract_ms,
        "ms_solve": t_solve_ms,
        "ms_wall": t_extract_ms + t_solve_ms,
        "ms_internal": result.solve_time_ms,
    }


def main() -> int:
    global _db_name, _fov_deg, _fov_max_error_deg

    # The table uses non-ASCII characters; Windows consoles default to cp1252.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    ap = argparse.ArgumentParser(description="tetra3rs quality + perf harness")
    ap.add_argument("--db", choices=sorted(DB_SPECS), default="narrow",
                    help="database to benchmark (default: narrow)")
    ap.add_argument("--fov", type=float, default=None,
                    help="FOV estimate in degrees (default: 8.86 narrow, range centre wide)")
    ap.add_argument("--fov-error", type=float, default=None,
                    help="FOV max error in degrees (default: 1.5 narrow, half range wide)")
    args = ap.parse_args()
    spec = DB_SPECS[args.db]
    _db_name = args.db
    if args.db == "narrow":
        _fov_deg, _fov_max_error_deg = FOV_DEG, FOV_MAX_ERROR_DEG
    else:
        _fov_deg = (spec.min_fov_deg + spec.max_fov_deg) / 2.0
        _fov_max_error_deg = (spec.max_fov_deg - spec.min_fov_deg) / 2.0
    if args.fov is not None:
        _fov_deg = args.fov
    if args.fov_error is not None:
        _fov_max_error_deg = args.fov_error

    try:
        import tetra3rs  # noqa: F401
    except ImportError:
        print(
            "tetra3rs not installed. Run `uv sync` then retry:\n"
            "    uv run python scripts/bench_solvers.py",
            file=sys.stderr,
        )
        return 2

    from importlib.metadata import version as _pkg_version

    print(
        f"# bench_solvers.py — {platform.machine()} / "
        f"python {platform.python_version()} / tetra3rs {_pkg_version('tetra3rs')}"
    )
    print(f"# db={args.db} fov_estimate={_fov_deg:g} deg fov_max_error={_fov_max_error_deg:g} deg")
    print()

    cols = "{:<10} {:>12} {:>10} {:>9} {:>8}"
    print(cols.format("sample", "t3rs wall ms", "t3rs int ms", "rmse ″", "matches"))
    print("-" * 55)

    rows = []
    for name in SAMPLES:
        p = SAMPLES_DIR / name
        if not p.exists():
            print(f"{name:<10} (missing)")
            continue
        b = solve_with_tetra3rs(p)

        if not b["ok"]:
            print(
                f"{name:<10} {b['ms_wall']:>12.1f}  "
                f"FAIL ({b.get('status', '?')}, "
                f"matches={b.get('matches')}, centroids={b.get('n_centroids')})"
            )
            continue

        print(
            cols.format(
                name,
                f"{b['ms_wall']:.1f}",
                f"{b['ms_internal']:.1f}",
                f"{b['rmse_arcsec']:.2f}",
                str(b["matches"]),
            )
        )
        rows.append(b)

    if rows:
        print()
        t3rs_avg = sum(r["ms_wall"] for r in rows) / len(rows)
        print(
            f"avg wall: t3rs={t3rs_avg:.1f}ms (n={len(rows)})  "
            f"(baseline laptop ~29 ms, Pi 4 ~215 ms per SPEC_SOLVER_PERFORMANCE.md)"
        )

    return 0


if __name__ == "__main__":
    sys.exit(main())
