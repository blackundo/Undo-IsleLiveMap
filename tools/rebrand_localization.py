#!/usr/bin/env python3
"""Rebrand the reviewed The Isle Vietnamese Game.locres resources.

The handoff's 1.1.4 package is kept immutable because its checksum and
provenance describe that historical release.  This tool creates a new staged
resource tree and changes only the reviewed welcome/credit entry in the two
game locale files.  It intentionally does not touch IoStore/PAK assets.
"""

from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path


WELCOME_KEY = "2E0D8DC24EE8F1B6312F19A279F948C0"
OLD_MARKERS = (
    "THEPRIMALVN",
    "discord.gg/theprimalvn",
    "DINO VIỆT NAM",
    "discord.gg/dinovietnam",
)
NEW_WELCOME = "Chào mừng đến với THE ISLE\nPhát triển bởi Isle Live Map"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input-root", required=True, type=Path)
    parser.add_argument("--output-root", required=True, type=Path)
    parser.add_argument(
        "--pylocres-root",
        required=True,
        type=Path,
        help="Directory containing the pylocres Python package.",
    )
    return parser.parse_args()


def load_locres_module(root: Path):
    root = root.resolve(strict=True)
    sys.path.insert(0, str(root))
    try:
        from pylocres.locres import LocresFile  # type: ignore
    except ImportError as error:  # pragma: no cover - environment guard
        raise RuntimeError(f"pylocres is unavailable under {root}") from error
    return LocresFile


def find_welcome(locres):
    matches = [
        entry
        for namespace in locres
        for entry in namespace
        if entry.key == WELCOME_KEY
    ]
    if len(matches) != 1:
        raise RuntimeError(f"expected one welcome entry, found {len(matches)}")
    return matches[0]


def rebrand_file(input_path: Path, output_path: Path, LocresFile) -> None:
    source = LocresFile()
    source.read(input_path)
    entry = find_welcome(source)
    if entry.translation != NEW_WELCOME and not any(
        marker.casefold() in entry.translation.casefold() for marker in OLD_MARKERS
    ):
        raise RuntimeError(
            f"welcome entry in {input_path} does not match the reviewed source copy"
        )

    entry.translation = NEW_WELCOME
    output_path.parent.mkdir(parents=True, exist_ok=True)
    source.write(output_path)

    verified = LocresFile()
    verified.read(output_path)
    if find_welcome(verified).translation != NEW_WELCOME:
        raise RuntimeError(f"round-trip verification failed for {output_path}")


def main() -> int:
    args = parse_args()
    input_root = args.input_root.resolve(strict=True)
    output_root = args.output_root.resolve()
    if output_root.exists():
        raise RuntimeError(f"output root already exists; choose a new staging path: {output_root}")
    shutil.copytree(input_root, output_root)

    LocresFile = load_locres_module(args.pylocres_root)
    changed = []
    for locale in ("vi", "vi-VN"):
        path = output_root / "TheIsle" / "Content" / "Localization" / "Game" / locale / "Game.locres"
        if not path.is_file():
            raise RuntimeError(f"missing expected resource: {path}")
        rebrand_file(path, path, LocresFile)
        changed.append(str(path))

    print(f"PASS: rebranded {len(changed)} Game.locres files")
    for path in changed:
        print(path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
