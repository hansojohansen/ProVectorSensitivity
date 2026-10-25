#!/usr/bin/env python3
"""Validate JSON files under schemas/ (CI helper).

* Every *.json under schemas/ must parse.
* Every *.schema.json must be a valid JSON Schema for its declared $schema draft. This uses the
  `jsonschema` package if it is installed. Without it, only parsing is checked and a notice is printed.
* If schemas/examples/<name>/*.json exist, they are validated against schemas/<name>.schema.json
  (jsonschema required).

Exit 0 if OK or schemas/ does not exist yet, 1 on any error.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path


def main(argv: list[str]) -> int:
    root = Path(argv[1]) if len(argv) > 1 else Path(__file__).resolve().parents[2]
    sdir = root / "schemas"
    if not sdir.is_dir():
        print("check_schemas: no schemas/ directory yet, skipping")
        return 0
    try:
        import jsonschema  # type: ignore
        from jsonschema.validators import validator_for  # type: ignore
    except ImportError:
        jsonschema = None
        print("check_schemas: 'jsonschema' not installed; checking JSON syntax only")

    errors = 0
    docs: dict[Path, object] = {}
    for p in sorted(sdir.rglob("*.json")):
        try:
            docs[p] = json.loads(p.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as e:
            print(f"{p.relative_to(root)}: invalid JSON: {e}")
            errors += 1

    if jsonschema is not None:
        for p, doc in docs.items():
            if not p.name.endswith(".schema.json"):
                continue
            try:
                validator_for(doc).check_schema(doc)
            except jsonschema.exceptions.SchemaError as e:
                print(f"{p.relative_to(root)}: invalid schema: {e.message}")
                errors += 1
        examples = sdir / "examples"
        if examples.is_dir():
            for exdir in sorted(d for d in examples.iterdir() if d.is_dir()):
                schema_path = sdir / f"{exdir.name}.schema.json"
                if schema_path not in docs:
                    print(f"{exdir.relative_to(root)}: no matching {schema_path.name}")
                    errors += 1
                    continue
                schema = docs[schema_path]
                validator = validator_for(schema)(schema)
                for ex in sorted(exdir.glob("*.json")):
                    if ex not in docs:
                        continue
                    for err in validator.iter_errors(docs[ex]):
                        print(f"{ex.relative_to(root)}: {'/'.join(map(str, err.path)) or '<root>'}: {err.message}")
                        errors += 1

    print(f"check_schemas: {len(docs)} file(s), {errors} error(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
