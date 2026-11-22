#!/usr/bin/env python3
"""Check that a `dotnet test` trx report shows a suite that ran and did not fail.

`dotnet test` exits 0 on a run that discovered nothing, so CI parses the
report rather than trusting the exit code. Each argument is a trx path
or a glob, and must name exactly one file. A report fails when it is
missing or unparseable, has no `Counters`, ran zero tests, passed none,
or counts anything as failed, error, timeout or aborted. Skipped tests
are fine; xUnit counts them as the gap between `total` and `executed`
and leaves `notExecuted` at 0, so that gap is printed as `skipped`.

Usage:
    scripts/trx-check.py test-results/results.trx
    scripts/trx-check.py 'test-results/*.trx'
    scripts/trx-check.py --self-test
"""

from __future__ import annotations

import glob
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
FAILING = ("failed", "error", "timeout", "aborted")
SHOWN = ("total", "executed", "passed", "notExecuted", *FAILING)


def check(report: Path) -> tuple[str | None, list[str]]:
    """Return the report's counts line, or None, and its findings."""
    try:
        root = ET.parse(report).getroot()
    except (OSError, ET.ParseError) as error:
        return None, [f"{report}: cannot be read as a trx: {error}"]
    counters = root.find(f"{NS}ResultSummary/{NS}Counters")
    if counters is None:
        return None, [f"{report}: has no Counters element to read"]
    counts: dict[str, int] = {}
    for name in SHOWN:
        raw = counters.get(name, "0")
        try:
            counts[name] = int(raw)
        except ValueError:
            return None, [f"{report}: Counters {name}={raw!r} is not a number"]
    line = f"{report}: " + ", ".join(f"{name} {counts[name]}" for name in SHOWN) + f", skipped {counts['total'] - counts['executed']}"
    findings = []
    if counts["total"] == 0:
        findings.append(f"{report}: zero tests ran, which dotnet test reports as success")
    elif counts["passed"] == 0:
        findings.append(f"{report}: no test passed, so the suite did not really run")
    for name in FAILING:
        if counts[name] > 0:
            findings.append(f"{report}: {counts[name]} {name}")
    return line, findings


def resolve(argument: str) -> tuple[Path | None, str | None]:
    """Resolve a path or glob to the one file it must name."""
    matches = sorted(glob.glob(argument)) if glob.has_magic(argument) else [argument]
    if len(matches) != 1:
        return None, f"{argument}: {len(matches)} trx files, not one"
    path = Path(matches[0])
    if not path.is_file():
        return None, f"{path}: no trx was written, so the suite did not run"
    return path, None


def run(arguments: list[str], quiet: bool = False) -> list[str]:
    """Check every argument, printing each report's counts, and return the findings."""
    findings = []
    for argument in arguments:
        path, problem = resolve(argument)
        if problem is not None:
            findings.append(problem)
            continue
        line, found = check(path)
        if line is not None and not quiet:
            print(line)
        findings.extend(found)
    return findings


def trx(**counts: int) -> str:
    """A minimal trx carrying the given counters."""
    attributes = " ".join(f'{name}="{value}"' for name, value in counts.items())
    return f'<TestRun xmlns="{NS[1:-1]}"><ResultSummary outcome="Completed"><Counters {attributes} /></ResultSummary></TestRun>'


SELF_TEST: list[tuple[str, str | None, bool]] = [
    ("skipped", trx(total=15, executed=13, passed=13, failed=0, notExecuted=2), True),
    ("all passed", trx(total=3, executed=3, passed=3), True),
    ("one failed", trx(total=3, executed=3, passed=2, failed=1), False),
    ("one error", trx(total=3, executed=3, passed=2, error=1), False),
    ("one timeout", trx(total=3, executed=3, passed=2, timeout=1), False),
    ("aborted", trx(total=3, executed=1, passed=1, aborted=1), False),
    ("zero tests", trx(total=0, executed=0, passed=0), False),
    ("all skipped", trx(total=2, executed=0, passed=0, notExecuted=2), False),
    ("no counters", f'<TestRun xmlns="{NS[1:-1]}"><ResultSummary /></TestRun>', False),
    ("garbled count", trx(total="many", passed=1), False),
    ("not xml", "dotnet test crashed", False),
    ("missing", None, False),
]


def self_test() -> list[str]:
    """Check the rule against the report shapes it has to get right."""
    failures = []
    with tempfile.TemporaryDirectory() as scratch:
        for name, content, passes in SELF_TEST:
            path = Path(scratch) / f"{name.replace(' ', '-')}.trx"
            if content is not None:
                path.write_text(content, encoding="utf-8")
            findings = run([str(path)], quiet=True)
            if (not findings) != passes:
                failures.append(f"{name}: expected {'a pass' if passes else 'a finding'}, got {findings or 'a pass'}")
        Path(scratch, "a_1.trx").write_text(trx(total=1, passed=1), encoding="utf-8")
        Path(scratch, "a_2.trx").write_text(trx(total=1, passed=1), encoding="utf-8")
        if not run([str(Path(scratch) / "a_*.trx")], quiet=True):
            failures.append("a glob naming two files: expected a finding, got a pass")
        if not run([str(Path(scratch) / "b_*.trx")], quiet=True):
            failures.append("a glob naming nothing: expected a finding, got a pass")
    return failures


def main() -> int:
    if "--self-test" in sys.argv:
        failures = self_test()
        for failure in failures:
            print(f"scripts/trx-check.py: {failure}")
        print(f"{len(failures)} finding(s)")
        return 1 if failures else 0
    arguments = sys.argv[1:]
    if not arguments:
        print(__doc__.strip(), file=sys.stderr)
        return 2
    findings = run(arguments)
    for finding in findings:
        print(finding, file=sys.stderr)
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
