# Copyright (C) 2026 Arun Venkataswamy
#
# This file is part of PushNav.
#
# PushNav is free software: you can redistribute it and/or modify it
# under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# PushNav is distributed in the hope that it will be useful, but
# WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
# General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with PushNav. If not, see <https://www.gnu.org/licenses/>.

# fork: camera-controls (see specs/camera-server/SPEC_CAMERA_CONTROLS.md)
"""Generate the prebuilt tetra3rs SolverDatabases in data/.

One-shot dev tool. Run this when:
  - first creating the .bin files
  - tetra3rs is upgraded to a version with an incompatible .bin format
  - the db parameters below need to change

Two databases are defined (specs/camera-server/SPEC_CAMERA_CONTROLS.md
section 5.4):

  narrow  data/tetra3rs_gaia.bin       FOV 7..10 deg,  mag <= 8  (the original)
  wide    data/tetra3rs_gaia_wide.bin  FOV 18..32 deg, mag <= 7

Narrow parameters mirror the existing hip8_database.npz settings (decoded
from its props_packed) for matched-FOV comparison parity. See
docs/superpowers/specs/2026-05-18-tetra3rs-bench-findings.md.

Every built (or already present) file gets an entry in data/solver_dbs.json
(FOV range, magnitude, counts, content hash). The engine reads that
registry, so the FOV ranges live only here. data/VERSION.json gets the
matching solver_db_version / solver_db_version_wide strings.

Run:
    uv run --group dev python scripts/build_tetra3rs_catalog.py            # both
    uv run --group dev python scripts/build_tetra3rs_catalog.py --db wide
    uv run --group dev python scripts/build_tetra3rs_catalog.py --db narrow --force
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import time
from dataclasses import dataclass
from pathlib import Path

import tetra3rs

REPO_ROOT = Path(__file__).resolve().parent.parent
DATA_DIR = REPO_ROOT / "data"
REGISTRY_NAME = "solver_dbs.json"
VERSION_NAME = "VERSION.json"

# Parameters shared by every database.
PATTERN_MAX_ERROR = 0.005
VERIFICATION_STARS_PER_FOV = 30
EPOCH_PROPER_MOTION_YEAR = 2026.0


@dataclass(frozen=True)
class DbSpec:
    name: str
    filename: str
    min_fov_deg: float
    max_fov_deg: float
    star_max_magnitude: float
    # Key in data/VERSION.json that carries "<stem>_<sha12>_mag<M>".
    version_key: str

    @property
    def stem(self) -> str:
        return Path(self.filename).stem


DB_SPECS: dict[str, DbSpec] = {
    "narrow": DbSpec(
        name="narrow",
        filename="tetra3rs_gaia.bin",
        min_fov_deg=7.0,
        max_fov_deg=10.0,
        star_max_magnitude=8.0,
        version_key="solver_db_version",
    ),
    "wide": DbSpec(
        name="wide",
        filename="tetra3rs_gaia_wide.bin",
        min_fov_deg=18.0,
        max_fov_deg=32.0,
        star_max_magnitude=7.0,
        version_key="solver_db_version_wide",
    ),
}


def file_sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def version_string(spec: DbSpec, sha256: str) -> str:
    """E.g. tetra3rs_gaia_0fbb55d9d9e4_mag8 / tetra3rs_gaia_wide_<sha12>_mag7."""
    return f"{spec.stem}_{sha256[:12]}_mag{spec.star_max_magnitude:g}"


def registry_entry(spec: DbSpec, path: Path, num_stars: int, num_patterns: int) -> dict:
    return {
        "name": spec.name,
        "file": spec.filename,
        "min_fov_deg": spec.min_fov_deg,
        "max_fov_deg": spec.max_fov_deg,
        "star_max_magnitude": spec.star_max_magnitude,
        "pattern_max_error": PATTERN_MAX_ERROR,
        "verification_stars_per_fov": VERIFICATION_STARS_PER_FOV,
        "epoch_proper_motion_year": EPOCH_PROPER_MOTION_YEAR,
        "num_stars": num_stars,
        "num_patterns": num_patterns,
        "size_bytes": path.stat().st_size,
        "sha256": file_sha256(path),
    }


def _read_json(path: Path, default):
    if not path.exists():
        return default
    with path.open("r", encoding="utf-8") as f:
        return json.load(f)


def _write_json(path: Path, data) -> None:
    with path.open("w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=2)
        f.write("\n")


def update_registry(out_dir: Path, entries: list[dict]) -> None:
    """Merge entries into <out_dir>/solver_dbs.json (keyed by name)."""
    path = out_dir / REGISTRY_NAME
    registry = _read_json(path, {"version": 1, "databases": []})
    by_name = {e["name"]: e for e in registry.get("databases", [])}
    for e in entries:
        by_name[e["name"]] = e
    registry["version"] = 1
    # Narrowest range first, matching the selection order in evf.solver.databases.
    registry["databases"] = sorted(
        by_name.values(), key=lambda e: (e["max_fov_deg"] - e["min_fov_deg"], e["name"])
    )
    _write_json(path, registry)


def update_version_json(out_dir: Path, entries: list[dict]) -> None:
    """Set the solver_db_version* keys of <out_dir>/VERSION.json (if it exists)."""
    path = out_dir / VERSION_NAME
    if not path.exists():
        return
    version = _read_json(path, {})
    for e in entries:
        spec = DB_SPECS[e["name"]]
        version[spec.version_key] = version_string(spec, e["sha256"])
    _write_json(path, version)


def build_one(spec: DbSpec, out_dir: Path, force: bool) -> dict:
    """Build (or reuse) one database file; return its registry entry."""
    out_path = out_dir / spec.filename
    if out_path.exists() and not force:
        print(f"{spec.name}: {out_path.name} already exists, skipping "
              "(use --force to regenerate)")
        db = tetra3rs.SolverDatabase.load_from_file(str(out_path))
        return registry_entry(spec, out_path, db.num_stars, db.num_patterns)
    print(f"{spec.name}: generating tetra3rs db (FOV {spec.min_fov_deg}..{spec.max_fov_deg} deg, "
          f"mag<={spec.star_max_magnitude})...")
    t0 = time.perf_counter()
    db = tetra3rs.SolverDatabase.generate_from_gaia(
        max_fov_deg=spec.max_fov_deg,
        min_fov_deg=spec.min_fov_deg,
        star_max_magnitude=spec.star_max_magnitude,
        pattern_max_error=PATTERN_MAX_ERROR,
        verification_stars_per_fov=VERIFICATION_STARS_PER_FOV,
        epoch_proper_motion_year=EPOCH_PROPER_MOTION_YEAR,
    )
    elapsed = time.perf_counter() - t0
    out_path.parent.mkdir(parents=True, exist_ok=True)
    db.save_to_file(str(out_path))
    size_mb = out_path.stat().st_size / 1_000_000
    print(f"{spec.name}: built in {elapsed:.1f}s ({db.num_stars} stars, "
          f"{db.num_patterns} patterns), wrote {out_path.name} ({size_mb:.1f} MB)")
    return registry_entry(spec, out_path, db.num_stars, db.num_patterns)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Build the prebuilt tetra3rs solver databases.")
    ap.add_argument("--db", choices=["narrow", "wide", "all"], default="all",
                    help="which database to build (default: all)")
    ap.add_argument("--force", action="store_true",
                    help="regenerate files that already exist")
    ap.add_argument("--out-dir", type=Path, default=DATA_DIR,
                    help="output directory (default: data/)")
    args = ap.parse_args(argv)

    names = list(DB_SPECS) if args.db == "all" else [args.db]
    out_dir = args.out_dir.resolve()
    entries = [build_one(DB_SPECS[name], out_dir, args.force) for name in names]
    update_registry(out_dir, entries)
    update_version_json(out_dir, entries)
    print(f"Updated {out_dir / REGISTRY_NAME}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
