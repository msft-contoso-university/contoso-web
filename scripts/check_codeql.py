"""Fail closed on incomplete or non-clean candidate CodeQL SARIF evidence."""
import json
import sys
from pathlib import Path

EXPECTED_FILES = {"csharp.sarif", "actions.sarif", "python.sarif"}


def require_expected_files(names):
    if set(names) != EXPECTED_FILES:
        raise ValueError("Missing or unexpected CodeQL language evidence")


def validate_report(report, name):
    if not isinstance(report, dict) or report.get("version") != "2.1.0":
        raise ValueError(f"{name}: expected SARIF version 2.1.0")
    runs = report.get("runs")
    if not isinstance(runs, list) or not runs:
        raise ValueError(f"{name}: missing analysis runs")
    for run in runs:
        if not isinstance(run, dict):
            raise ValueError(f"{name}: invalid analysis run")
        tool = run.get("tool")
        driver = tool.get("driver") if isinstance(tool, dict) else None
        if not isinstance(driver, dict) or driver.get("name") != "CodeQL":
            raise ValueError(f"{name}: unexpected analysis driver")
        results = run.get("results", [])
        if not isinstance(results, list) or results:
            raise ValueError(f"{name}: security findings or invalid results require review/remediation")
        invocations = run.get("invocations", [])
        if not isinstance(invocations, list):
            raise ValueError(f"{name}: invalid invocation evidence")
        for invocation in invocations:
            if not isinstance(invocation, dict):
                raise ValueError(f"{name}: invalid invocation")
            if "executionSuccessful" in invocation and invocation["executionSuccessful"] is not True:
                raise ValueError(f"{name}: incomplete or unsuccessful analysis")


def main():
    files = sorted(Path(sys.argv[1]).glob("*.sarif"))
    require_expected_files(path.name for path in files)
    for path in files:
        validate_report(json.loads(path.read_text(encoding="utf-8")), path.name)
    print("Validated clean CodeQL SARIF for C#, Actions and Python; this does not register code scanning alerts.")


if __name__ == "__main__":
    main()
