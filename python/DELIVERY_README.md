# MarineEnvironment Python SDK

이 배포본은 Python 개발자가 C# 소스코드 없이 `MarineEnvironment.dll` 기능을 사용할 수 있도록 구성되어 있습니다.

## 1. 배포 폴더

```text
deliver/
├─ packages/
│  └─ marineenvironment-0.1.0-py3-none-win_amd64.whl
├─ config/
│  ├─ marineenvironment.json
│  └─ shom.seabed.mapping.json
├─ examples/
│  ├─ 01_initialize.py
│  ├─ 02_source_status.py
│  ├─ 03_query_all_point.py
│  ├─ 04_query_source_current.py
│  ├─ 05_query_source_seabed.py
│  ├─ 06_query_grid_bathymetry.py
│  ├─ 07_query_grid_current.py
│  ├─ 08_query_seabed_grade_20km.py
│  ├─ 09_source_lifecycle.py
│  └─ _example_common.py
├─ db/
│  └─ README.txt
└─ README.md
```

`src/` 또는 C# 프로젝트는 필요하지 않습니다. `MarineEnvironment.dll`은 wheel 안에 포함되어 있습니다.

## 2. 요구 환경

- Windows x64
- Python 3.11 이상 (개발/검증 환경은 Python 3.12 x64)
- .NET Runtime
- 사용 데이터소스에 따라 NetCDF-C/HDF5 네이티브 런타임이 필요할 수 있음

Python 확인:

```powershell
python --version
```

.NET Runtime 확인:

```powershell
dotnet --list-runtimes
```

## 3. 설치

인터넷이 가능한 Python 개발 PC에서는 배포 폴더 루트에서 다음과 같이 설치합니다.

```powershell
python -m pip install .\packages\marineenvironment-0.1.0-py3-none-win_amd64.whl
```

wheel의 의존성에 `pythonnet>=3.0,<4`가 선언되어 있으므로, 인터넷 접속이 가능하면 pip가 필요한 Python 패키지를 함께 설치합니다.

설치 확인:

```powershell
python -c "from marineenvironment import MarineEnvironment; print('MarineEnvironment import OK')"
```

## 4. 데이터베이스 배치

기본 `config/marineenvironment.json`은 배포 폴더의 `db/`를 기준으로 상대 경로를 사용합니다.

예:

```text
deliver/
├─ config/
│  └─ marineenvironment.json
└─ db/
   ├─ ETOPO1/
   ├─ BADA/
   ├─ KHOA/
   ├─ KOREA_SEDIMENT/
   ├─ SHOM/
   └─ ...
```

DB가 다른 위치에 있다면 `config/marineenvironment.json`의 각 source `path`를 실제 위치에 맞게 수정합니다.

SHOM 매핑 파일은 기본적으로 다음 파일을 사용합니다.

```text
config/shom.seabed.mapping.json
```

## 5. 가장 간단한 사용법

```python
from marineenvironment import MarineEnvironment

with MarineEnvironment() as env:
    init = env.initialize(r".\config\marineenvironment.json")

    print("initialize:", init["success"])

    result = env.query(
        latitude=34.165,
        longitude=128.170,
        depth=10.0,
    )

    for item in result["source_values"]:
        print(item["source_id"], item["type"], item["value"], item["unit"])
```

## 6. 주요 API

```python
env.initialize(config_path)
env.get_sources()
env.get_source_status(source_id)

env.query(
    latitude=...,
    longitude=...,
    depth=...,
    when=...,
)

env.query_source(
    source_id,
    latitude=...,
    longitude=...,
    depth=...,
    when=...,
)

env.query_grid(
    source_id,
    min_latitude=...,
    max_latitude=...,
    min_longitude=...,
    max_longitude=...,
    width=...,
    height=...,
)

env.query_seabed_grade_grid(
    min_latitude=...,
    max_latitude=...,
    min_longitude=...,
    max_longitude=...,
    grid_mode="CellSizeKilometers",
    cell_size_kilometers=2.0,
    contact_density=1,
    terrain="Flat",
)

env.load_source(option)
env.reload_source(option)
env.unload_source(source_id)
```

모든 반환값은 Python에서 다루기 쉬운 `dict` / `list` 형태로 변환됩니다.

## 7. 예제 실행

배포본의 `examples/`는 저장소의 `python/src`를 참조하지 않습니다.
설치된 wheel의 `marineenvironment` 패키지만 사용합니다.

배포 폴더 루트에서:

```powershell
python .\examples\01_initialize.py
python .\examples\02_source_status.py
python .\examples\03_query_all_point.py
python .\examples\04_query_source_current.py
python .\examples\05_query_source_seabed.py
python .\examples\06_query_grid_bathymetry.py
python .\examples\07_query_grid_current.py
python .\examples\08_query_seabed_grade_20km.py
python .\examples\09_source_lifecycle.py
```

예제는 별도 인자가 없으면 자동으로:

```text
config/marineenvironment.json
```

을 사용합니다.

다른 설정을 사용할 경우 첫 번째 인수로 전달할 수 있습니다.

```powershell
python .\examples\03_query_all_point.py "D:\MarineDB\marineenvironment.json"
```

또는:

```powershell
$env:MARINEENV_CONFIG="D:\MarineDB\marineenvironment.json"
python .\examples\03_query_all_point.py
```

## 8. 해류 값

KHOA 해류 조회 예:

```python
from datetime import datetime
from marineenvironment import MarineEnvironment

with MarineEnvironment() as env:
    env.initialize(r".\config\marineenvironment.json")

    result = env.query_source(
        "KHOA_DAILY_CURRENT",
        latitude=34.165,
        longitude=128.170,
        when=datetime(2026, 9, 17),
    )

    current = result["source_value"]["value"]

    print(current["eastward_velocity"])
    print(current["northward_velocity"])
    print(current["speed"])
    print(current["direction"])
    print(current["method"])
```

KHOA의 `method`는 `CrossYearSeasonalVectorMean`이며, FES 조화분조 합성이 아니므로 `constituent_mode`과 `constituent_count`는 `None`이 정상입니다.

## 9. 주의사항

- 수심 값은 원 데이터의 vertical convention을 그대로 유지합니다.
- `ElevationPositiveUp` 데이터는 해저가 일반적으로 음수입니다.
- `DepthPositiveDown` 데이터는 수심이 일반적으로 양수입니다.
- 데이터소스의 실제 가용 범위를 벗어나면 결과가 없을 수 있습니다.
- 해저등급 Grid는 셀 중심점 기준으로 해저저질 source를 조회합니다.
- KOREA_SEDIMENT와 SHOM 등 여러 해저저질 source가 설정된 경우 해저등급 산출에서는 설정 priority 순으로 사용됩니다.

## 10. 문제 확인

초기화 상태 확인:

```python
from marineenvironment import MarineEnvironment

with MarineEnvironment() as env:
    result = env.initialize(r".\config\marineenvironment.json")

    for source in result["sources"]:
        print(
            source["id"],
            source["status"],
            source["message"],
        )
```

`NativeLibraryUnavailable` 또는 NetCDF 관련 DLL 오류가 발생하면 해당 PC에 승인된 NetCDF-C/HDF5 네이티브 런타임이 배치되어 있는지 확인합니다.
