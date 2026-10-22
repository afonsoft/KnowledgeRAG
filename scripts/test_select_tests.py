#!/usr/bin/env python3
"""Tests for select-tests.py (SPEC-20261021, #596) — stdlib unittest, no deps.

Run: python3 scripts/test_select_tests.py
"""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import importlib
sel = importlib.import_module("select-tests")

GRAPH = {
    "src/KnowledgeHub.Shared": [],
    "src/KnowledgeHub.McpEngine": ["src/KnowledgeHub.Shared"],
    "src/KnowledgeHub.Client": ["src/KnowledgeHub.Shared"],
    "src/KnowledgeHub.Server": [
        "src/KnowledgeHub.Shared", "src/KnowledgeHub.McpEngine",
        "src/KnowledgeHub.Client"],
    "sdks/dotnet/KnowledgeHub.Sdk": [],
    "src/KnowledgeHub.Review.Cli": ["sdks/dotnet/KnowledgeHub.Sdk"],
    "tests/KnowledgeHub.Tests.Unit": [
        "src/KnowledgeHub.Shared", "src/KnowledgeHub.McpEngine",
        "src/KnowledgeHub.Server", "src/KnowledgeHub.Review.Cli",
        "sdks/dotnet/KnowledgeHub.Sdk"],
    "tests/KnowledgeHub.Tests.Integration": ["src/KnowledgeHub.Server"],
    "tests/KnowledgeHub.Tests.Performance": [
        "src/KnowledgeHub.Server", "tests/KnowledgeHub.Tests.Integration"],
    "tests/KnowledgeHub.Tests.Ui": [
        "src/KnowledgeHub.Server", "src/KnowledgeHub.Client"],
}


class ClassifyTests(unittest.TestCase):
    def test_docs_are_ignorable(self):
        self.assertEqual("none", sel.classify("docs/en/API.md", GRAPH))
        self.assertEqual("none", sel.classify(".specs/SPEC-1.md", GRAPH))
        self.assertEqual("none", sel.classify("README.md", GRAPH))

    def test_shared_triggers_all(self):
        # Shared is a project dir but classify returns it as a project —
        # its breadth shows up through dependents covering all tests.
        self.assertEqual(
            "src/KnowledgeHub.Shared",
            sel.classify("src/KnowledgeHub.Shared/Dtos/X.cs", GRAPH))

    def test_root_build_files_trigger_all(self):
        for f in ("KnowledgeHub.slnx", "global.json", "Directory.Build.props"):
            self.assertEqual("all", sel.classify(f, GRAPH))

    def test_unknown_path_is_all(self):
        self.assertEqual("all", sel.classify("weird/path.bin", GRAPH))

    def test_non_dotnet_sdks_ignorable(self):
        self.assertEqual("none", sel.classify("sdks/python/kh/client.py", GRAPH))
        self.assertEqual("none", sel.classify("sdks/go/client.go", GRAPH))

    def test_dotnet_sdk_maps_to_sdk_project(self):
        self.assertEqual(
            "sdks/dotnet/KnowledgeHub.Sdk",
            sel.classify("sdks/dotnet/KnowledgeHub.Sdk/Client.cs", GRAPH))


class SelectTests(unittest.TestCase):
    def test_docs_only_change_selects_nothing(self):
        projects, fallback, _ = sel.select(["docs/en/API.md", "README.md"], GRAPH)
        self.assertEqual([], projects)
        self.assertFalse(fallback)

    def test_server_change_hits_all_server_dependents(self):
        projects, fallback, _ = sel.select(
            ["src/KnowledgeHub.Server/Services/AgentService.cs"], GRAPH)
        self.assertFalse(fallback)
        self.assertIn("tests/KnowledgeHub.Tests.Unit", projects)
        self.assertIn("tests/KnowledgeHub.Tests.Integration", projects)
        self.assertIn("tests/KnowledgeHub.Tests.Performance", projects)
        self.assertIn("tests/KnowledgeHub.Tests.Ui", projects)

    def test_engine_change_hits_unit_via_server(self):
        projects, fallback, _ = sel.select(
            ["src/KnowledgeHub.McpEngine/Agents/ChainAst/X.cs"], GRAPH)
        self.assertIn("tests/KnowledgeHub.Tests.Unit", projects)
        self.assertIn("tests/KnowledgeHub.Tests.Integration", projects)

    def test_test_project_change_selects_itself_and_dependents(self):
        projects, fallback, _ = sel.select(
            ["tests/KnowledgeHub.Tests.Integration/AgentApiTests.cs"], GRAPH)
        self.assertIn("tests/KnowledgeHub.Tests.Integration", projects)
        self.assertIn("tests/KnowledgeHub.Tests.Performance", projects)
        self.assertNotIn("tests/KnowledgeHub.Tests.Unit", projects)

    def test_slnx_falls_back_to_all(self):
        projects, fallback, _ = sel.select(["KnowledgeHub.slnx"], GRAPH)
        self.assertTrue(fallback)
        self.assertEqual(4, len(projects))

    def test_sdk_change_hits_unit_and_review_cli_consumers(self):
        projects, fallback, _ = sel.select(
            ["sdks/dotnet/KnowledgeHub.Sdk/Client.cs"], GRAPH)
        self.assertEqual(["tests/KnowledgeHub.Tests.Unit"], projects)

    def test_workflow_change_runs_everything(self):
        projects, fallback, _ = sel.select(
            [".github/workflows/ci-build-test.yml"], GRAPH)
        self.assertTrue(fallback)
        self.assertEqual(4, len(projects))

    def test_non_workflow_github_files_ignorable(self):
        projects, fallback, _ = sel.select(
            [".github/dependabot.yml", ".github/CODEOWNERS"], GRAPH)
        self.assertEqual([], projects)
        self.assertFalse(fallback)

    def test_fallback_result_is_json_serializable(self):
        # CI serializes select() output via --format=json — the fallback
        # branch used to return a set and crash the runner.
        import json
        projects, fallback, changed = sel.select(
            [".github/workflows/ci-build-test.yml"], GRAPH)
        self.assertTrue(fallback)
        json.dumps({"projects": projects, "fallback": fallback, "changed": changed})


if __name__ == "__main__":
    unittest.main(verbosity=2)
