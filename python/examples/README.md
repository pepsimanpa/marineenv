# MarineEnvironment Python examples

각 파일은 `MarineEnvironment.dll` 공개 기능을 Python에서 호출하는 예제입니다.

예제 좌표는 사용자가 지정한 대략 범위 `33.99~34.34 N, 128.02~128.32 E`의 중심점인
`34.165 N, 128.170 E`를 사용합니다.
영역 예제는 이 중심점을 유지하면서 실제 거리 약 20 km x 20 km가 되도록 조정한
`34.07507~34.25493 N, 128.06131~128.27869 E` 범위를 사용합니다.

패키지를 pip로 설치하지 않아도 저장소 안에서 바로 실행할 수 있도록
각 예제가 `python/src`를 자동으로 찾습니다. 단, Python.NET은 설치되어
있어야 하고 MarineEnvironment.dll은 먼저 Release 또는 Debug로 빌드되어
있어야 합니다.

## 실행

모든 예제는 첫 번째 인수로 실제 `marineenvironment.json` 경로를 받습니다.

```powershell
python python\examples\01_initialize.py "E:\MarineDB\marineenvironment.json"
python python\examples\03_query_all_point.py "E:\MarineDB\marineenvironment.json"
python python\examples\08_query_seabed_grade_20km.py "E:\MarineDB\marineenvironment.json"
```

또는 한 번만 환경변수를 설정할 수 있습니다.

```powershell
$env:MARINEENV_CONFIG="E:\MarineDB\marineenvironment.json"
python python\examples\01_initialize.py
python python\examples\03_query_all_point.py
```

## 파일별 기능

- `01_initialize.py` - Initialize
- `02_source_status.py` - GetSources / GetSourceStatus
- `03_query_all_point.py` - Query: 모든 READY 소스의 한 지점 조회
- `04_query_source_current.py` - QuerySource: KHOA 해류 한 소스 조회
- `05_query_source_seabed.py` - QuerySource: 국내 해저저질 원값 + 직접 파생값
- `06_query_grid_bathymetry.py` - QueryGrid: BADA 수심 영역 조회
- `07_query_grid_current.py` - QueryGrid: KHOA 해류 영역/벡터 조회
- `08_query_seabed_grade_20km.py` - QuerySeabedGradeGrid: 약 20 km x 20 km 해저등급 산출
- `09_source_lifecycle.py` - LoadSource / ReloadSource / UnloadSource

`with MarineEnvironment() as env:` 블록이 끝나면 Dispose가 자동 호출됩니다.
