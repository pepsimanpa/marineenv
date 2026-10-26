# MarineEnvironment 업체 전달본

이 폴더는 `MarineEnvironment` 핵심 C# 소스코드 없이 Viewer 소스와 컴파일된 DLL을 이용할 수 있도록 구성한 전달본입니다.

## 폴더 구성

```text
deliver-vendor/
├─ MarineEnvironment.Viewer.sln
├─ Viewer/        Viewer C# / WPF 소스
├─ lib/           MarineEnvironment.dll 및 런타임 파일
├─ app/           빌드 완료된 Viewer 실행 파일
├─ config/        marineenvironment.json, SHOM 매핑
├─ db/            해양환경 DB
├─ native/        NetCDF-C / HDF5 x64 네이티브 런타임
└─ README.md
```

`MarineEnvironment` 핵심 구현 소스는 포함하지 않습니다. Viewer 프로젝트는 `lib/MarineEnvironment.dll`을 직접 참조하도록 구성되어 있습니다.

## Viewer 실행

빌드된 실행 파일을 사용할 경우:

```text
app/MarineEnvironment.Viewer.exe
```

Viewer에서 **Browse...**를 눌러 다음 설정 파일을 선택한 뒤 **Load Config**를 실행합니다.

```text
config/marineenvironment.json
```

설정 파일의 DB 경로는 기본적으로 `../db/...` 형태이므로, 본 전달본의 `db/` 구조를 유지하면 됩니다.

## Viewer 소스 빌드

Visual Studio에서:

```text
MarineEnvironment.Viewer.sln
```

을 열거나 명령행에서 다음을 실행합니다.

```powershell
dotnet build .\Viewer\MarineEnvironment.Viewer.csproj -c Release
```

Viewer 프로젝트는 다음 DLL을 직접 참조합니다.

```text
lib/MarineEnvironment.dll
```

핵심 `MarineEnvironment.csproj` 또는 C# 구현 소스는 필요하지 않습니다.

## 주요 DLL 기능

Viewer 소스에서 다음 기능의 실제 사용 예를 확인할 수 있습니다.

- 환경 DB 초기화 및 데이터소스 상태 확인
- 단일 위치 환경정보 조회
- 데이터소스 지정 조회
- 영역 Grid 조회
- 해류 벡터 조회 및 표시
- 해저저질 조회
- 해저등급 Grid 산출 및 표시

## DB 및 네이티브 런타임

`config/marineenvironment.json`에서 활성화된 데이터소스의 DB가 `db/`에 존재해야 합니다.

NetCDF 기반 데이터소스를 사용할 경우 `native/win-x64/`에 프로젝트에서 검증한 x64 NetCDF-C/HDF5 런타임 DLL 세트가 필요합니다. BAT 실행 시 원본 저장소의 `native/win-x64/`가 존재하면 자동으로 복사됩니다.

## 전달본에서 제외되는 항목

다음 개발/검증용 항목은 포함하지 않습니다.

- `src/MarineEnvironment/` 핵심 C# 소스
- `src/KodcDiagnostics/`
- `tests/KhoaSeasonalSmoke/`
- `src/Goci2Downloader/`
- 기타 `tests/`
- `.git/`, `.github/`
- `bin/`, `obj/`, `*.pdb`

## 전달본 생성

원본 저장소 루트에서 다음 파일을 실행합니다.

```bat
build_vendor_delivery.bat
```

기본값은 DB까지 `deliver-vendor/db/`로 복사합니다. DB를 별도 전달하려면 `build_vendor_delivery.bat` 상단의 다음 값을 변경합니다.

```bat
set "COPY_DB=0"
```
