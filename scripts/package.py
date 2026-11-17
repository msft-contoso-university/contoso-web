"""Build and package a clean source revision; this script never deploys."""
import hashlib
import json
import os
import re
import subprocess
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main():
    sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    if not re.fullmatch(r"[a-f0-9]{40}", sha):
        raise ValueError("An exact source commit is required")
    if "GITHUB_SHA" in os.environ and os.environ["GITHUB_SHA"] != sha:
        raise ValueError("Checkout SHA does not match workflow SHA")
    if subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip():
        raise ValueError("Refusing to label a dirty working tree as an immutable source candidate")
    output = ROOT / "artifacts" / "publish"
    if output.exists():
        raise ValueError("Remove the old artifacts/publish directory before creating a new candidate")
    subprocess.run(
        ["dotnet", "publish", str(ROOT / "src" / "Contoso.Web" / "Contoso.Web.csproj"),
         "-c", "Release", "--no-build", "--no-restore", "-o", str(output)],
        cwd=ROOT, check=True,
    )
    files = sorted(path for path in output.rglob("*") if path.is_file())
    private_path_forms = set()
    for path in (str(ROOT), str(Path.home())):
        for form in (path, path.replace("\\", "/")):
            private_path_forms.add(form.encode())
            private_path_forms.add(json.dumps(form)[1:-1].encode())
    for path in files:
        if any(form in path.read_bytes() for form in private_path_forms):
            raise ValueError(f"{path.name} embeds a private build path; check deterministic SourceLink settings")
    manifest = {
        "service": "contoso-web",
        "sourceCommit": sha,
        "runtime": "net10.0",
        "frameworkDependent": True,
        "entrypoint": "dotnet Contoso.Web.dll",
        "promotionOwner": "contoso-platform",
        "files": {
            path.relative_to(output).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in files
        },
    }
    manifest_bytes = (json.dumps(manifest, indent=2) + "\n").encode()
    (output / "candidate.json").write_bytes(manifest_bytes)
    target = ROOT / "artifacts" / f"contoso-web-{sha}.zip"
    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(path for path in output.rglob("*") if path.is_file()):
            info = zipfile.ZipInfo(path.relative_to(output).as_posix(), date_time=(2020, 1, 1, 0, 0, 0))
            info.external_attr = 0o100644 << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, path.read_bytes())
    (ROOT / "artifacts" / "candidate.json").write_bytes(manifest_bytes)
    checksum = hashlib.sha256(target.read_bytes()).hexdigest()
    (ROOT / "artifacts" / "SHA256SUMS").write_text(f"{checksum}  {target.name}\n", encoding="utf-8")
    print(f"Candidate source {sha}; ZIP SHA256 {checksum}")


if __name__ == "__main__":
    main()
