from __future__ import annotations

import os
import sys
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
PYTHON_SRC = REPO_ROOT / "python" / "src"
if str(PYTHON_SRC) not in sys.path:
    sys.path.insert(0, str(PYTHON_SRC))


def get_config_path() -> str:
    if len(sys.argv) >= 2:
        return str(Path(sys.argv[1]).expanduser().resolve())

    env_path = os.environ.get("MARINEENV_CONFIG")
    if env_path:
        return str(Path(env_path).expanduser().resolve())

    raise SystemExit(
        "marineenvironment.json 경로를 지정하세요.\n"
        "예: python python\\examples\\01_initialize.py "
        "\"E:\\MarineDB\\marineenvironment.json\"\n"
        "또는 MARINEENV_CONFIG 환경변수를 설정할 수 있습니다."
    )


JINHAE_LAT = 35.10
JINHAE_LON = 128.70

# 진해만 부근 약 20 km x 20 km 학습/검증용 근사 범위.
JINHAE_20KM = {
    "min_latitude": 35.01,
    "max_latitude": 35.19,
    "min_longitude": 128.59,
    "max_longitude": 128.81,
}
