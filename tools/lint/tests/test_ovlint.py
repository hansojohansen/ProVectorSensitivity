"""Tests for tools/lint/ovlint.py. Stdlib unittest; also runs under pytest.

    python3 -m unittest discover -s tools/lint/tests -v
"""

from __future__ import annotations

import io
import shutil
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

HERE = Path(__file__).resolve().parent
FIXTURES = HERE / "fixtures"
sys.path.insert(0, str(HERE.parent))

import ovlint  # noqa: E402


class TreeCase(unittest.TestCase):
    """Builds a throwaway repo tree per test."""

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def put(self, rel: str, fixture: str | None = None, text: str | None = None) -> None:
        dest = self.root / rel
        dest.parent.mkdir(parents=True, exist_ok=True)
        if fixture is not None:
            shutil.copyfile(FIXTURES / fixture, dest)
        else:
            dest.write_text(text or "", encoding="utf-8")

    def findings(self):
        return ovlint.lint(self.root)[0]

    def rules_at(self, rel: str) -> list[tuple[int, str]]:
        return [(f.line, f.rule) for f in self.findings() if f.path == rel]


class BannedApiTests(TreeCase):
    def test_every_banned_api_is_caught(self) -> None:
        self.put("src/OpenVector.Bad/Bad.cs", "bad_all_apis.cs")
        got = self.findings()
        tokens = {f.message.split("'")[1] for f in got}
        expected = {
            "OpenProcess", "ReadProcessMemory", "WriteProcessMemory", "CreateRemoteThread",
            "VirtualAllocEx", "NtReadVirtualMemory", "SetWindowsHookExW", "SendInput",
            "mouse_event", "keybd_event", "RIDEV_INPUTSINK", "BitBlt", "PrintWindow",
            "CopyFromScreen", "IDXGIOutputDuplication", "DuplicateOutput",
            "GetProcessesByName", "GetProcesses",
        }
        self.assertEqual(expected - tokens, set(), f"missed: {expected - tokens}")
        rules = {f.rule for f in got}
        self.assertEqual(rules, {r.id for r in ovlint.API_RULES})

    def test_clean_code_passes(self) -> None:
        self.put("src/OpenVector.Core/Good.cs", "good_clean.cs")
        self.assertEqual(self.findings(), [])

    def test_tools_are_scanned_and_python_is_checked(self) -> None:
        self.put("tools/Thing/bad_tool.py", "bad_tool.py")
        self.assertEqual(
            self.rules_at("tools/Thing/bad_tool.py"),
            [(3, "input-injection"), (4, "process-enumeration")],
        )

    def test_excluded_locations_are_not_scanned(self) -> None:
        for rel in (
            "tools/lint/x.cs",
            "research/rawaccel-angular/x.cs",
            "reference/python/x.py",
            "tests/OpenVector.Integration/x.cs",
            "docs/x.cs",
            "src/OpenVector.Core/bin/Debug/x.cs",
            "src/OpenVector.Core/obj/x.cs",
            "src/OpenVector.Core/README.md",
        ):
            self.put(rel, "bad_all_apis.cs")
        self.assertEqual(self.findings(), [])

    def test_binary_files_are_skipped(self) -> None:
        dest = self.root / "src/X/blob.cs"
        dest.parent.mkdir(parents=True)
        dest.write_bytes(b"SendInput\x00\x01\x02")
        self.assertEqual(self.findings(), [])


class AllowCommentTests(TreeCase):
    def test_valid_allows_suppress_and_are_listed(self) -> None:
        self.put("src/X/Allowed.cs", "allowed.cs")
        findings, allows = ovlint.lint(self.root)
        self.assertEqual(findings, [])
        self.assertEqual(
            sorted((a.line, a.rule) for a in allows),
            [(4, "input-injection"), (5, "screen-capture"), (7, "process-open"), (8, "process-memory")],
        )
        self.assertTrue(all(len(a.reason.split()) >= ovlint.MIN_REASON_WORDS for a in allows))

    def test_malformed_wrong_and_stale_allows_fail(self) -> None:
        self.put("src/X/BadAllow.cs", "bad_allow.cs")
        got = self.rules_at("src/X/BadAllow.cs")
        self.assertIn((4, "allow-syntax"), got)      # missing justification
        self.assertIn((4, "input-injection"), got)   # ...so SendInput is still reported
        self.assertIn((5, "allow-syntax"), got)      # unknown rule
        self.assertIn((5, "screen-capture"), got)
        self.assertIn((6, "stale-allow"), got)       # suppresses nothing
        self.assertIn((8, "stale-allow"), got)       # wrong rule for next line
        self.assertIn((9, "input-injection"), got)

    def test_allow_does_not_leak_past_next_line(self) -> None:
        self.put(
            "src/X/Leak.cs",
            text="// openvector-lint-allow: input-injection only the very next line please\n"
            "void A() => SendInput();\n"
            "void B() => SendInput();\n",
        )
        self.assertEqual(self.rules_at("src/X/Leak.cs"), [(3, "input-injection")])

    def test_trailing_allow_in_api_text_is_not_itself_flagged(self) -> None:
        self.put(
            "src/X/T.cs",
            text="int x = 1; // openvector-lint-allow: input-injection we do not call SendInput here\n",
        )
        self.assertEqual(self.rules_at("src/X/T.cs"), [(1, "stale-allow")])


class ResearchReferenceTests(TreeCase):
    def test_project_referencing_research_fails(self) -> None:
        self.put("src/OpenVector.App/OpenVector.App.csproj", "bad_research.csproj")
        got = self.rules_at("src/OpenVector.App/OpenVector.App.csproj")
        self.assertEqual(got, [(3, "research-reference"), (3, "research-reference"), (4, "research-reference")])

    def test_root_solution_referencing_research_fails(self) -> None:
        self.put("OpenVector.slnx", "bad_root.slnx")
        self.assertEqual(self.rules_at("OpenVector.slnx"), [(3, "research-reference"), (3, "research-reference")])

    def test_tests_and_tools_projects_are_checked(self) -> None:
        self.put("tests/X.Tests/X.Tests.csproj", "bad_research.csproj")
        self.put("tools/Spike/Spike.csproj", "bad_research.csproj")
        paths = {f.path for f in self.findings()}
        self.assertEqual(paths, {"tests/X.Tests/X.Tests.csproj", "tools/Spike/Spike.csproj"})

    def test_installer_files_are_checked(self) -> None:
        self.put("installer/Product.wxs", text='<File Source="..\\research\\x.sys" />\n')
        self.put("installer/setup.iss", text="Source: rawaccel-angular\\driver.sys\n")
        self.assertEqual({f.path for f in self.findings()}, {"installer/Product.wxs", "installer/setup.iss"})

    def test_docs_research_and_similar_words_are_fine(self) -> None:
        self.put("src/OpenVector.Core/OpenVector.Core.csproj", "good_project.csproj")
        self.put("Directory.Build.props", "good_project.csproj")
        self.assertEqual(self.findings(), [])

    def test_xml_allow_comment_works(self) -> None:
        self.put("src/X/X.csproj", "allowed_research.csproj")
        self.assertEqual(self.findings(), [])

    def test_workflows_checked_except_research_workflows(self) -> None:
        self.put(".github/workflows/release.yml", text="run: dotnet build research/rawaccel-angular\n")
        self.put(".github/workflows/research-driver.yml", text="run: msbuild research/rawaccel-angular\n")
        self.assertEqual({f.path for f in self.findings()}, {".github/workflows/release.yml"})

    def test_research_tree_itself_is_not_checked(self) -> None:
        self.put("research/rawaccel-angular/rawaccel.sln", "bad_root.slnx")
        self.assertEqual(self.findings(), [])


class CliTests(TreeCase):
    def run_main(self, *args: str) -> tuple[int, str]:
        buf = io.StringIO()
        with redirect_stdout(buf):
            code = ovlint.main(["--root", str(self.root), *args])
        return code, buf.getvalue()

    def test_exit_codes(self) -> None:
        self.put("src/X/Good.cs", "good_clean.cs")
        self.assertEqual(self.run_main()[0], 0)
        self.put("src/X/Bad.cs", "bad_all_apis.cs")
        code, out = self.run_main()
        self.assertEqual(code, 1)
        self.assertIn("src/X/Bad.cs:", out)

    def test_list_allows(self) -> None:
        self.put("src/X/Allowed.cs", "allowed.cs")
        code, out = self.run_main("--list-allows")
        self.assertEqual(code, 0)
        self.assertIn("src/X/Allowed.cs:4: input-injection: test harness only", out)

    def test_bad_root(self) -> None:
        self.assertEqual(ovlint.main(["--root", str(self.root / "missing")]), 2)


if __name__ == "__main__":
    unittest.main()
