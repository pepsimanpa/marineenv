from __future__ import annotations

import os
import sys
from pathlib import Path
from typing import Any


_DLL_NAME = "MarineEnvironment.dll"
_loaded_dll: Path | None = None
_types: dict[str, Any] | None = None


def _candidate_dlls() -> list[Path]:
    package_dir = Path(__file__).resolve().parent
    candidates = [package_dir / "lib" / _DLL_NAME]

    # Editable install from this repository:
    # repo/python/src/marineenvironment/_runtime.py
    parents = Path(__file__).resolve().parents
    if len(parents) > 3:
        repo_root = parents[3]
        candidates.extend(
            [
                repo_root / "src" / "MarineEnvironment" / "bin" / "Release" / "net5.0" / _DLL_NAME,
                repo_root / "src" / "MarineEnvironment" / "bin" / "Debug" / "net5.0" / _DLL_NAME,
            ]
        )

    return candidates


def resolve_dll(dll_path: str | os.PathLike[str] | None = None) -> Path:
    if dll_path is not None:
        candidate = Path(dll_path).expanduser().resolve()
        if candidate.is_dir():
            candidate = candidate / _DLL_NAME
        if not candidate.is_file():
            raise FileNotFoundError(f"MarineEnvironment.dll not found: {candidate}")
        return candidate

    env_dir = os.environ.get("MARINEENV_DLL_DIR")
    if env_dir:
        candidate = Path(env_dir).expanduser().resolve()
        if candidate.is_dir():
            candidate = candidate / _DLL_NAME
        if candidate.is_file():
            return candidate
        raise FileNotFoundError(
            f"MARINEENV_DLL_DIR does not contain {_DLL_NAME}: {candidate}"
        )

    for candidate in _candidate_dlls():
        if candidate.is_file():
            return candidate

    searched = "\n".join(f"  - {path}" for path in _candidate_dlls())
    raise FileNotFoundError(
        "MarineEnvironment.dll was not found. Build the .NET project, pass "
        "dll_path, or set MARINEENV_DLL_DIR.\nSearched:\n" + searched
    )


def load_types(dll_path: str | os.PathLike[str] | None = None) -> dict[str, Any]:
    global _loaded_dll, _types

    resolved = resolve_dll(dll_path)

    if _types is not None:
        if resolved != _loaded_dll:
            raise RuntimeError(
                "MarineEnvironment.dll is already loaded from "
                f"{_loaded_dll}; Python.NET cannot switch the loaded assembly "
                "inside the same Python process."
            )
        return _types

    from pythonnet import get_runtime_info, load

    if get_runtime_info() is None:
        # The DLL targets .NET 5. Python.NET's CoreCLR loader supports .NET Core
        # runtimes and must be selected before importing clr.
        load("coreclr")

    import clr

    dll_dir = str(resolved.parent)
    if dll_dir not in sys.path:
        sys.path.insert(0, dll_dir)

    clr.AddReference("MarineEnvironment")

    from MarineEnvironment import MarineEnvironmentManager
    from MarineEnvironment.Models import (
        EnvironmentQuery,
        GridQuery,
        GridResolutionMode,
        SeabedGradeGridMode,
        SeabedGradeGridQuery,
        SeabedTerrain,
    )

    _loaded_dll = resolved
    _types = {
        "MarineEnvironmentManager": MarineEnvironmentManager,
        "EnvironmentQuery": EnvironmentQuery,
        "GridQuery": GridQuery,
        "GridResolutionMode": GridResolutionMode,
        "SeabedGradeGridMode": SeabedGradeGridMode,
        "SeabedGradeGridQuery": SeabedGradeGridQuery,
        "SeabedTerrain": SeabedTerrain,
    }
    return _types
