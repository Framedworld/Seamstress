#!/usr/bin/env python3
"""Copy the tailoring skill into the plugin.

The single source of truth is docs/skills/seamstress-garment-tailoring/SKILL.md. The plugin ships a generated copy in
plugin/skills/seamstress-garment-tailoring/SKILL.md, because an installed plugin is copied on its own and cannot read
files outside its folder. Edit the source, then run:

    python plugin/scripts/sync_skill.py          # write the copy
    python plugin/scripts/sync_skill.py --check  # exit 1 if the copy is out of date (for CI or a pre-commit hook)
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SKILLS = ["seamstress-garment-tailoring"]

NOTE = (
    "<!-- Generated from docs/skills/{name}/SKILL.md by plugin/scripts/sync_skill.py. Do not edit this copy: edit\n"
    "     the source and run the script again. -->\n"
    "\n"
    "> Paths in this skill (`docs/`, `samples/`, `src/`, `scripts/`) are relative to the Seamstress checkout.\n"
    "\n"
)


def render(name: str) -> str:
    source = (REPO / "docs" / "skills" / name / "SKILL.md").read_text(encoding="utf-8").replace("\r\n", "\n")
    if not source.startswith("---\n"):
        raise SystemExit(f"{name}: SKILL.md must start with YAML frontmatter")
    end = source.index("\n---\n", 4) + len("\n---\n")
    head, body = source[:end], source[end:].lstrip("\n")
    return head + "\n" + NOTE.format(name=name) + body


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true", help="only report whether the copy is up to date")
    args = parser.parse_args()

    stale = []
    for name in SKILLS:
        target = REPO / "plugin" / "skills" / name / "SKILL.md"
        wanted = render(name)
        current = target.read_text(encoding="utf-8") if target.exists() else None
        if current == wanted:
            continue
        if args.check:
            stale.append(str(target.relative_to(REPO)))
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        with open(target, "w", encoding="utf-8", newline="\n") as f:
            f.write(wanted)
        print(f"wrote {target.relative_to(REPO)}")

    if stale:
        print("out of date (run python plugin/scripts/sync_skill.py): " + ", ".join(stale), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
