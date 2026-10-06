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


JINHAE_LAT = 34.85
JINHAE_LON = 129.00

# 진해만보다 남쪽의 열린 남해 해역 약 20 km x 20 km 학습/검증용 근사 범위.
# 해안선/육지 셀이 과도하게 포함되지 않도록 기존 범위보다 남동쪽으로 이동했다.
JINHAE_20KM = {
    "min_latitude": 34.76,
    "max_latitude": 34.94,
    "min_longitude": 128.89,
    "max_longitude": 129.11,
}
