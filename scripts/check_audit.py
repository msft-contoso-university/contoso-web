"""Fail on incomplete audit evidence or any vulnerable direct/transitive package."""
import json
import sys
from pathlib import Path

report = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
if report.get("errors"):
    raise SystemExit("NuGet audit returned errors; vulnerability status is unknown")
projects = report.get("projects", [])
if not projects:
    raise SystemExit("NuGet audit returned no projects")
findings = []
for project in projects:
    for framework in project.get("frameworks", []):
        for group in ("topLevelPackages", "transitivePackages"):
            for package in framework.get(group, []):
                if package.get("vulnerabilities"):
                    findings.append(package["id"])
if findings:
    raise SystemExit("Vulnerable packages: " + ", ".join(sorted(set(findings))))
print(f"NuGet vulnerability audit completed for {len(projects)} projects; no findings.")
