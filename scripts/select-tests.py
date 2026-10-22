#!/usr/bin/env python3
"""select-tests.py — diff-scoped test selection (SPEC-20261021, #596).

Maps `git diff --name-only <base>...HEAD` to the test projects that must run,
following the ProjectReference graph (transitively) — the .NET analogue of
langchain's check_diff.py. Conservative: anything ambiguous falls back to
"run everything" (fallback=true) rather than silently skipping.

Usage:
    python3 scripts/select-tests.py --base origin/main
    python3 scripts/select-tests.py --base HEAD~1 --format=json
    python3 scripts/select-tests.py --files a.cs tests/KnowledgeHub.Tests.Unit/x.cs

Exit 0 always. Output:
    --format=dotnet  space-separated project paths for `dotnet test`
    --format=json    {"projects":[...],"fallback":bool,"changed":[...]}
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path, PurePosixPath

REPO_ROOT = Path(__file__).resolve().parent.parent

# Path prefixes that never affect .NET test outcomes.
IGNORABLE_PREFIXES = (
    "docs/", ".specs/", ".claude/", ".devin/", "scripts/",
    "dockerhub-overview", "logs/", "data/", "models/",
)
IGNORABLE_FILES = {
    "README.md", "README.pt-br.md", "CHANGELOG.md", "LICENSE", "AGENTS.md",
    "CLAUDE.md", "llms.txt", ".gitignore", ".gitattributes", ".dockerignore",
    "skills-lock.json", "backup.sh", "restore.sh", "install.sh",
    "docker-compose.yml", "docker-compose.override.yml",
    "docker-compose.override.yml.example", "Dockerfile", "SonarQube.Analysis.xml",
    ".env.example",
}
# Root files whose change invalidates every test project.
ALL_TRIGGER_FILES = {
    "KnowledgeHub.slnx", "global.json", "Directory.Build.props",
    "Directory.Packages.props", "dotnet-tools.json", "NuGet.config", "nuget.config",
}
# Extra dependency edges the csproj graph cannot express: the UI test suite
# drives the built Server+Client app even though it has no ProjectReference.
EXTRA_EDGES = {
    "tests/KnowledgeHub.Tests.Ui": [
        "src/KnowledgeHub.Server", "src/KnowledgeHub.Client",
    ],
}
# Non-.NET SDK trees have their own CI (sdk-ci.yml) — not dotnet-testable.
NON_DOTNET_SDKS = ("sdks/python/", "sdks/java/", "sdks/go/")


def git_changed_files(base: str) -> list[str]:
    out = subprocess.run(
        ["git", "diff", "--name-only", f"{base}...HEAD"],
        capture_output=True, text=True, cwd=REPO_ROOT, check=True,
    )
    return [ln.strip() for ln in out.stdout.splitlines() if ln.strip()]


def discover_projects() -> dict[str, list[str]]:
    """{project_dir: [referenced_project_dirs]} from .csproj ProjectReference."""
    graph: dict[str, list[str]] = {}
    for csproj in REPO_ROOT.rglob("*.csproj"):
        if any(part in {"bin", "obj"} for part in csproj.parts):
            continue
        proj_dir = csproj.parent.relative_to(REPO_ROOT).as_posix()
        refs = re.findall(
            r'ProjectReference\s+Include="([^"]+)"', csproj.read_text(encoding="utf-8"))
        deps = []
        for ref in refs:
            ref_path = (csproj.parent / ref.replace("\\", "/")).resolve()
            try:
                deps.append(ref_path.parent.relative_to(REPO_ROOT).as_posix())
            except ValueError:
                pass  # reference outside repo — ignore
        graph[proj_dir] = deps
    for proj, deps in EXTRA_EDGES.items():
        existing = graph.setdefault(proj, [])
        existing.extend(d for d in deps if d not in existing)
    return graph


def classify(path: str, graph: dict[str, list[str]]) -> str:
    """Return 'all', 'none', or a project_dir the file belongs to."""
    if PurePosixPath(path).name in IGNORABLE_FILES and "/" not in path:
        return "none"
    if path in ALL_TRIGGER_FILES:
        return "all"
    if path.startswith(".github/"):
        # workflow edits change CI itself — run everything; templates,
        # CODEOWNERS, dependabot configs etc. are inert for tests.
        return "all" if path.startswith(".github/workflows/") else "none"
    if any(path.startswith(p) for p in IGNORABLE_PREFIXES):
        return "none"
    if any(path.startswith(p) for p in NON_DOTNET_SDKS):
        return "none"  # separate SDK CI, not dotnet test
    if path.startswith("sdks/dotnet/"):
        return "sdks/dotnet/KnowledgeHub.Sdk"
    # longest matching project dir wins (src/..., tests/...)
    candidates = [d for d in graph if path.startswith(d + "/")]
    if candidates:
        return max(candidates, key=len)
    if path.startswith("tests/eval/"):
        return "none"  # eval datasets, not a dotnet project
    return "all"  # unknown root path — conservative


def reverse_graph(graph: dict[str, list[str]]) -> dict[str, list[str]]:
    rev: dict[str, list[str]] = {k: [] for k in graph}
    for proj, deps in graph.items():
        for dep in deps:
            rev.setdefault(dep, []).append(proj)
    return rev


def affected(changed_projects: set[str], graph: dict[str, list[str]]) -> set[str]:
    """changed projects + every project that transitively references them."""
    rev = reverse_graph(graph)
    seen = set(changed_projects)
    stack = list(changed_projects)
    while stack:
        for dependent in rev.get(stack.pop(), []):
            if dependent not in seen:
                seen.add(dependent)
                stack.append(dependent)
    return seen


def select(files: list[str], graph: dict[str, list[str]]) -> tuple[list[str], bool, list[str]]:
    changed_projects: set[str] = set()
    ignorable: list[str] = []
    fallback = False
    for f in files:
        kind = classify(f, graph)
        if kind == "all":
            fallback = True
        elif kind == "none":
            ignorable.append(f)
        else:
            changed_projects.add(kind)

    if fallback:
        projects = sorted(d for d in graph if d.startswith("tests/"))
        return projects, True, sorted(changed_projects)

    impacted = affected(changed_projects, graph)
    projects = sorted(d for d in impacted if d.startswith("tests/"))
    return projects, False, sorted(changed_projects)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="origin/main",
                    help="git base ref (default: origin/main)")
    ap.add_argument("--format", choices=["json", "dotnet"], default="dotnet")
    ap.add_argument("--files", nargs="*", default=None,
                    help="explicit file list (skips git diff — for tests)")
    args = ap.parse_args()

    files = args.files if args.files is not None else git_changed_files(args.base)
    graph = discover_projects()
    projects, fallback, changed = select(files, graph)

    if args.format == "json":
        print(json.dumps({
            "projects": projects, "fallback": fallback, "changed": changed,
        }, indent=2))
    else:
        print(" ".join(projects))
    return 0


if __name__ == "__main__":
    sys.exit(main())
