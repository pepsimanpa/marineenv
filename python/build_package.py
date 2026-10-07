from __future__ import annotations

import base64
import csv
import hashlib
import io
import shutil
import subprocess
import zipfile
from pathlib import Path


PYTHON_DIR = Path(__file__).resolve().parent
REPO_ROOT = PYTHON_DIR.parent
PROJECT = REPO_ROOT / "src" / "MarineEnvironment" / "MarineEnvironment.csproj"
OUTPUT = REPO_ROOT / "src" / "MarineEnvironment" / "bin" / "Release" / "net5.0"
PACKAGE_DIR = PYTHON_DIR / "src" / "marineenvironment"
PACKAGE_LIB = PACKAGE_DIR / "lib"

DIST_NAME = "marineenvironment"
VERSION = "0.1.0"
WHEEL_TAG = "py3-none-win_amd64"
DIST_INFO = f"{DIST_NAME}-{VERSION}.dist-info"


def run(*args: str, cwd: Path | None = None) -> None:
    subprocess.run(args, cwd=cwd, check=True)


def _record_hash(data: bytes) -> str:
    digest = hashlib.sha256(data).digest()
    encoded = base64.urlsafe_b64encode(digest).rstrip(b"=").decode("ascii")
    return f"sha256={encoded}"


def _wheel_files() -> dict[str, bytes]:
    files: dict[str, bytes] = {}

    for path in PACKAGE_DIR.rglob("*"):
        if not path.is_file():
            continue
        if path.name == ".gitkeep" or path.suffix == ".pyc" or "__pycache__" in path.parts:
            continue

        archive_path = path.relative_to(PYTHON_DIR / "src").as_posix()
        files[archive_path] = path.read_bytes()

    metadata = f"""Metadata-Version: 2.1
Name: {DIST_NAME}
Version: {VERSION}
Summary: Python wrapper for MarineEnvironment.dll
Requires-Python: >=3.11
Requires-Dist: pythonnet (>=3.0,<4)

""".encode("utf-8")

    wheel = f"""Wheel-Version: 1.0
Generator: MarineEnvironment offline packager
Root-Is-Purelib: false
Tag: {WHEEL_TAG}

""".encode("utf-8")

    files[f"{DIST_INFO}/METADATA"] = metadata
    files[f"{DIST_INFO}/WHEEL"] = wheel
    return files


def build_wheel() -> Path:
    dist_dir = PYTHON_DIR / "dist"
    dist_dir.mkdir(parents=True, exist_ok=True)

    wheel_path = dist_dir / f"{DIST_NAME}-{VERSION}-{WHEEL_TAG}.whl"
    if wheel_path.exists():
        wheel_path.unlink()

    files = _wheel_files()
    record_rows: list[list[str]] = []

    with zipfile.ZipFile(wheel_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for archive_path in sorted(files):
            data = files[archive_path]
            archive.writestr(archive_path, data)
            record_rows.append([archive_path, _record_hash(data), str(len(data))])

        record_path = f"{DIST_INFO}/RECORD"
        output = io.StringIO(newline="")
        writer = csv.writer(output, lineterminator="\n")
        writer.writerows(record_rows)
        writer.writerow([record_path, "", ""])
        archive.writestr(record_path, output.getvalue().encode("utf-8"))

    return wheel_path


def main() -> None:
    run("dotnet", "build", str(PROJECT), "-c", "Release")

    PACKAGE_LIB.mkdir(parents=True, exist_ok=True)
    for child in PACKAGE_LIB.iterdir():
        if child.name == ".gitkeep":
            continue
        if child.is_dir():
            shutil.rmtree(child)
        else:
            child.unlink()

    copied = 0
    for pattern in ("*.dll", "*.json", "*.xml"):
        for source in OUTPUT.glob(pattern):
            shutil.copy2(source, PACKAGE_LIB / source.name)
            copied += 1

    dll = PACKAGE_LIB / "MarineEnvironment.dll"
    if not dll.is_file():
        raise FileNotFoundError(f"Expected build output was not found: {dll}")

    print(f"Copied {copied} runtime file(s) to {PACKAGE_LIB}")

    wheel = build_wheel()
    print("Built offline wheel:")
    print(f"  {wheel}")


if __name__ == "__main__":
    main()
