#!/usr/bin/env python3
"""OpenVector constraint linter (D5 anticheat-safety, D11 research isolation).

Stdlib-only. Run from anywhere:

    python3 tools/lint/ovlint.py                 # lint the repo this file lives in
    python3 tools/lint/ovlint.py --root PATH     # lint another tree (used by tests)
    python3 tools/lint/ovlint.py --list-allows   # print every allow comment for PM review

Exit code 0 = clean, 1 = violations found, 2 = usage error.
See tools/lint/README.md for the rules and the allowlist policy.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Iterable, Iterator

# --------------------------------------------------------------------------------------------
# Rules
# --------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class Rule:
    id: str
    description: str
    pattern: re.Pattern[str]


def _words(*names: str) -> re.Pattern[str]:
    """Match any of the identifiers as whole words (case-sensitive, like the Win32 exports)."""
    return re.compile(r"(?<![A-Za-z0-9_])(?:" + "|".join(names) + r")(?![A-Za-z0-9_])")


# Banned-API rules (D5). They apply to source-like files under src/ and tools/.
API_RULES: tuple[Rule, ...] = (
    Rule(
        "process-open",
        "opening another process (D5: never open game processes)",
        _words(r"(?:Nt|Zw)?OpenProcess"),
    ),
    Rule(
        "process-memory",
        "reading/writing/allocating another process's memory (D5: memory reading is permanently banned)",
        _words(
            r"ReadProcessMemory",
            r"WriteProcessMemory",
            r"(?:Nt|Zw)ReadVirtualMemory",
            r"(?:Nt|Zw)WriteVirtualMemory",
            r"VirtualAllocEx(?:Numa)?",
            r"VirtualProtectEx",
        ),
    ),
    Rule(
        "code-injection",
        "creating threads in another process (D5: never inject)",
        _words(r"CreateRemoteThread(?:Ex)?", r"(?:Nt|Zw|Rtl)CreateThreadEx", r"RtlCreateUserThread"),
    ),
    Rule(
        "input-hook",
        "global Windows hooks (D1/D5: user-mode hooks are rejected)",
        _words(r"SetWindowsHookEx[AW]?"),
    ),
    Rule(
        "input-injection",
        "synthesising input (D1/D5: injected input is what anticheats flag)",
        _words(r"SendInput", r"NtUserSendInput", r"mouse_event", r"keybd_event"),
    ),
    Rule(
        "background-raw-input",
        "receiving raw input while not in the foreground (D5: trainer reads input only in its own foreground window)",
        _words(r"RIDEV_(?:EX)?INPUTSINK"),
    ),
    Rule(
        "screen-capture",
        "capturing screen or window contents (D5: never look at the screen)",
        _words(
            r"BitBlt",
            r"PrintWindow",
            r"CopyFromScreen",
            r"IDXGIOutputDuplication",
            r"DuplicateOutput1?",
            r"GraphicsCaptureSession",
            r"GraphicsCaptureItem",
        ),
    ),
    Rule(
        "process-enumeration",
        "enumerating processes, e.g. for game detection (D5: never react to game state)",
        _words(
            r"GetProcesses(?:ByName)?",
            r"EnumProcesses",
            r"K32EnumProcesses",
            r"CreateToolhelp32Snapshot",
            r"Process32(?:First|Next)W?",
        ),
    ),
)

# Research-isolation rule (D11). Applies to project/solution/build/installer/workflow files.
RESEARCH_RULE = Rule(
    "research-reference",
    "product build/installer/workflow file references the research driver (D11: never ship or reference research/)",
    re.compile(
        r"(?:(?<![A-Za-z0-9_\-])(?<!docs[/\\])research[/\\])|rawaccel-angular",
        re.IGNORECASE,
    ),
)

META_ALLOW_SYNTAX = "allow-syntax"
META_STALE_ALLOW = "stale-allow"

ALL_RULE_IDS = frozenset(r.id for r in API_RULES) | {RESEARCH_RULE.id}

# --------------------------------------------------------------------------------------------
# File selection
# --------------------------------------------------------------------------------------------

API_SCAN_ROOTS = ("src", "tools")
# Paths (relative, posix) excluded from *every* rule. tools/lint holds the rule text and fixtures.
EXCLUDED_PREFIXES = ("tools/lint/", "research/")
SKIP_DIR_NAMES = frozenset({".git", "bin", "obj", ".vs", "node_modules", "__pycache__", ".venv", "TestResults"})

SOURCE_EXTENSIONS = frozenset(
    {
        ".cs", ".csx", ".fs", ".fsx", ".vb",
        ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".inl", ".def",
        ".py", ".ps1", ".psm1", ".bat", ".cmd", ".sh",
        ".js", ".mjs", ".ts", ".rs", ".go",
        ".xaml", ".axaml",
        ".csproj", ".fsproj", ".vcxproj", ".props", ".targets",
    }
)

# Build / solution / installer files checked for research references.
BUILD_EXTENSIONS = frozenset(
    {
        ".csproj", ".fsproj", ".vbproj", ".vcxproj", ".proj", ".props", ".targets",
        ".sln", ".slnx", ".slnf",
        ".wxs", ".wxi", ".wixproj", ".iss", ".nsi", ".nsh",
    }
)
BUILD_FILENAMES = frozenset({"global.json", "nuget.config", "velopack.json", "vpk.toml"})
BUILD_SCAN_ROOTS = ("src", "tools", "tests", "installer")
WORKFLOW_DIR = ".github/workflows"

MAX_FILE_BYTES = 4 * 1024 * 1024


@dataclass(frozen=True)
class Finding:
    path: str  # posix, relative to root
    line: int
    col: int
    rule: str
    message: str

    def format(self) -> str:
        return f"{self.path}:{self.line}:{self.col}: {self.rule}: {self.message}"


@dataclass(frozen=True)
class Allow:
    path: str
    line: int  # line the comment is on
    target_line: int  # line it applies to
    rule: str
    reason: str


def _rel(root: Path, p: Path) -> str:
    return PurePosixPath(p.relative_to(root).as_posix()).as_posix()


def _excluded(rel: str) -> bool:
    return any(rel == pre.rstrip("/") or rel.startswith(pre) for pre in EXCLUDED_PREFIXES)


def _walk(root: Path, sub: str) -> Iterator[Path]:
    base = root / sub
    if not base.is_dir():
        return
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIR_NAMES)
        for name in sorted(filenames):
            yield Path(dirpath) / name


def api_files(root: Path) -> Iterator[Path]:
    for sub in API_SCAN_ROOTS:
        for p in _walk(root, sub):
            if p.suffix.lower() in SOURCE_EXTENSIONS and not _excluded(_rel(root, p)):
                yield p


def _is_build_file(p: Path) -> bool:
    return p.suffix.lower() in BUILD_EXTENSIONS or p.name.lower() in BUILD_FILENAMES


def build_files(root: Path) -> Iterator[Path]:
    seen: set[Path] = set()
    for p in sorted(root.iterdir()) if root.is_dir() else []:
        if p.is_file() and _is_build_file(p):
            seen.add(p)
            yield p
    for sub in BUILD_SCAN_ROOTS:
        for p in _walk(root, sub):
            if p not in seen and _is_build_file(p) and not _excluded(_rel(root, p)):
                seen.add(p)
                yield p
    wf = root / WORKFLOW_DIR
    if wf.is_dir():
        for p in sorted(wf.iterdir()):
            # Research workflows (e.g. research-driver.yml) are allowed to build research/.
            if p.is_file() and p.suffix.lower() in (".yml", ".yaml") and not p.name.lower().startswith("research"):
                yield p


# --------------------------------------------------------------------------------------------
# Allow comments
# --------------------------------------------------------------------------------------------

ALLOW_TOKEN = "openvector-lint-allow:"
_ALLOW_RE = re.compile(r"openvector-lint-allow:\s*(?P<body>.*)$")
_COMMENT_TAIL_RE = re.compile(r"\s*(?:-->|\*/|#>)\s*$")
_COMMENT_PREFIX_RE = re.compile(r"(?://+|#+|;+|--|<!--|/\*+|\*|rem|REM|::|<#)")
MIN_REASON_WORDS = 3


def _read_text(p: Path) -> str | None:
    try:
        if p.stat().st_size > MAX_FILE_BYTES:
            return None
        data = p.read_bytes()
    except OSError:
        return None
    if b"\x00" in data[:8192]:
        return None  # binary
    return data.decode("utf-8", errors="replace")


def parse_allows(rel: str, lines: list[str]) -> tuple[list[Allow], list[Finding]]:
    """Return (allows, syntax findings). An allow applies to its own line; if the line holds only a
    comment, it (and any directly stacked allow-only lines) apply to the next non-allow line."""
    allows: list[Allow] = []
    errors: list[Finding] = []
    pending: list[tuple[int, str, str]] = []  # allow-only lines waiting for their target
    for idx, text in enumerate(lines, start=1):
        m = _ALLOW_RE.search(text)
        if m:
            body = _COMMENT_TAIL_RE.sub("", m.group("body")).strip()
            parts = body.split(None, 1)
            rule = parts[0] if parts else ""
            reason = parts[1].strip() if len(parts) > 1 else ""
            col = m.start() + 1
            if rule not in ALL_RULE_IDS:
                errors.append(Finding(rel, idx, col, META_ALLOW_SYNTAX,
                                      f"unknown rule '{rule}' in allow comment (known: {', '.join(sorted(ALL_RULE_IDS))})"))
                continue
            if len(reason.split()) < MIN_REASON_WORDS:
                errors.append(Finding(rel, idx, col, META_ALLOW_SYNTAX,
                                      f"allow comment for '{rule}' needs a justification of at least {MIN_REASON_WORDS} words"))
                continue
            comment_only = bool(_COMMENT_PREFIX_RE.fullmatch(text[: m.start()].strip()))
            # Same line always counts.
            allows.append(Allow(rel, idx, idx, rule, reason))
            if comment_only:
                pending.append((idx, rule, reason))
            continue
        if pending and text.strip():
            for (aline, rule, reason) in pending:
                allows.append(Allow(rel, aline, idx, rule, reason))
            pending = []
    for (aline, rule, _reason) in pending:
        errors.append(Finding(rel, aline, 1, META_ALLOW_SYNTAX, f"allow comment for '{rule}' has no following line to apply to"))
    return allows, errors


# --------------------------------------------------------------------------------------------
# Linting
# --------------------------------------------------------------------------------------------


def _scan(rel: str, text: str, rules: Iterable[Rule]) -> list[Finding]:
    """Rule matches on lines not covered by a matching allow comment, plus allow-syntax errors."""
    lines = text.splitlines()
    allows, findings = parse_allows(rel, lines)
    allowed = {(a.target_line, a.rule) for a in allows}
    for idx, line in enumerate(lines, start=1):
        if ALLOW_TOKEN in line:
            # Do not flag the API name inside the allow comment's own justification text.
            line = line[: line.index(ALLOW_TOKEN)]
        for rule in rules:
            if (idx, rule.id) in allowed:
                continue
            for m in rule.pattern.finditer(line):
                findings.append(Finding(rel, idx, m.start() + 1, rule.id, f"'{m.group(0)}': {rule.description}"))
    return findings


def lint(root: Path) -> tuple[list[Finding], list[Allow]]:
    """Lint the tree at root. Returns (findings, all allow comments that are in effect)."""
    root = root.resolve()
    findings: list[Finding] = []
    all_allows: list[Allow] = []
    per_file_rules: dict[Path, list[Rule]] = {}
    for p in api_files(root):
        per_file_rules.setdefault(p, []).extend(API_RULES)
    for p in build_files(root):
        per_file_rules.setdefault(p, []).append(RESEARCH_RULE)

    for p in sorted(per_file_rules):
        text = _read_text(p)
        if text is None:
            continue
        rel = _rel(root, p)
        rules = per_file_rules[p]
        f = _scan(rel, text, rules)
        allows, _errs = parse_allows(rel, text.splitlines())
        # Stale-allow detection: an allow whose (target line, rule) never matched.
        matched = _matched_pairs(text, rules)
        rule_ids = {r.id for r in rules}
        reported_stale: set[int] = set()
        for a in allows:
            if a.rule not in rule_ids:
                if a.line not in reported_stale:
                    f.append(Finding(rel, a.line, 1, META_STALE_ALLOW,
                                     f"rule '{a.rule}' does not apply to this file type/location"))
                    reported_stale.add(a.line)
                continue
            hits_any = any((t, a.rule) in matched for t in _targets_of(allows, a))
            if not hits_any and a.line not in reported_stale:
                f.append(Finding(rel, a.line, 1, META_STALE_ALLOW,
                                 f"allow comment for '{a.rule}' does not suppress anything; remove it"))
                reported_stale.add(a.line)
        findings.extend(f)
        # Report each allow comment once (by its own line) for --list-allows.
        seen_lines: set[int] = set()
        for a in allows:
            if a.line not in seen_lines:
                seen_lines.add(a.line)
                all_allows.append(a)
    findings.sort(key=lambda x: (x.path, x.line, x.col, x.rule))
    return findings, all_allows


def _targets_of(allows: list[Allow], a: Allow) -> set[int]:
    return {x.target_line for x in allows if x.line == a.line and x.rule == a.rule}


def _matched_pairs(text: str, rules: Iterable[Rule]) -> set[tuple[int, str]]:
    out: set[tuple[int, str]] = set()
    for idx, line in enumerate(text.splitlines(), start=1):
        if ALLOW_TOKEN in line:
            line = line[: line.index(ALLOW_TOKEN)]
        for rule in rules:
            if rule.pattern.search(line):
                out.add((idx, rule.id))
    return out


def main(argv: list[str] | None = None) -> int:
    default_root = Path(__file__).resolve().parents[2]
    ap = argparse.ArgumentParser(description="OpenVector D5/D11 constraint linter")
    ap.add_argument("--root", type=Path, default=default_root, help="repository root (default: this repo)")
    ap.add_argument("--list-allows", action="store_true", help="list allow comments for PM review and exit 0")
    ap.add_argument("--list-rules", action="store_true", help="list rules and exit 0")
    args = ap.parse_args(argv)

    if args.list_rules:
        for r in (*API_RULES, RESEARCH_RULE):
            print(f"{r.id}: {r.description}")
        return 0
    if not args.root.is_dir():
        print(f"ovlint: root '{args.root}' is not a directory", file=sys.stderr)
        return 2

    findings, allows = lint(args.root)
    if args.list_allows:
        if not allows:
            print("ovlint: no allow comments")
        for a in allows:
            print(f"{a.path}:{a.line}: {a.rule}: {a.reason}")
        return 0

    for f in findings:
        print(f.format())
    if findings:
        print(f"ovlint: {len(findings)} violation(s). See tools/lint/README.md.", file=sys.stderr)
        return 1
    print(f"ovlint: OK ({len(allows)} reviewed allow comment(s))")
    return 0


if __name__ == "__main__":
    sys.exit(main())
