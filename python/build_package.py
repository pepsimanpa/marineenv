from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path


PYTHON_DIR = Path(__file__).resolve().parent
REPO_ROOT = PYTHON_DIR.parent
PROJECT = REPO_ROOT / "src" / "MarineEnvironment" / "MarineEnvironment.csproj"
OUTPUT = REPO_ROOT / "src" / "MarineEnvironment" / "bin" / "Release" / "net5.0"
PACKAGE_LIB = PYTHON_DIR / "src" / "marineenvironment" / "lib"


def run(*args: str, cwd: Path | None = None) -> None:
    subprocess.run(args, cwd=cwd, check=True)


def main() -> None:
    run("dotnet", "build", str(PROJECT), "-c", "Release")

    PACKAGE_LIB.mkdir(parents=True, exist_ok=True)
    for child in PACKAGE_LIB.iterdir():
        if child.name != ".gitkeep":
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

    run(sys.executable, "-m", "build", cwd=PYTHON_DIR)


if __name__ == "__main__":
    main()
