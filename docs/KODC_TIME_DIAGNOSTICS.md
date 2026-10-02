# KODC NetCDF time-axis diagnostic (offline)

This console program reads only scalar CT/SA samples from the original KODC NetCDF file.
It does **not** modify the source or the MarineEnvironment query implementation.

It reports:
- the actual `lon / lat / depth / time` dimensions and time-coordinate metadata;
- whether the `time` coordinate contains useful values or missing/fill values;
- representative CT/SA values at **the same 37-slot offset** across all complete 37-record blocks;
- comparisons across up to five automatically selected valid sea points, two depth levels, and five slot offsets.

**Important:** Different values between 37-record blocks establish that they are not simple identical repetitions
at the sampled points; they **do not prove** that blocks correspond to calendar years 1994–2023.
The original product specification or generation code is still needed to assign years reliably.

## 1. Native DLLs

As with the WPF Viewer, put the complete **x64** NetCDF-C runtime (`netcdf.dll` plus all its
dependent DLLs) under `native/win-x64/`. They will be copied next to the diagnostic executable
during the build. Do not add the DLLs to Git.

## 2. Run from the repository root in Windows PowerShell

Replace the path after `--` with your actual KODC file path.

```powershell
dotnet run --project src/KodcDiagnostics/KodcDiagnostics.csproj -c Release -- "D:\\Database\\KODC\\CT_KODC_Climatology_grid_9423.nc" --var CT
```

If your DLLs are already next to the Viewer executable but are not in `native/win-x64`,
copy the **complete matching x64 DLL set** to `native/win-x64` and rebuild. A bare
`netcdf.dll` without its dependent DLLs may fail to load.

For salinity:

```powershell
dotnet run --project src/KodcDiagnostics/KodcDiagnostics.csproj -c Release -- "D:\\Database\\KODC\\SA_KODC_Climatology_grid_9423.nc" --var SA
```

The program prints the report and automatically saves a UTF-8 `kodc-time-diagnostic-*.txt`
in the current working directory. To choose an output path, append `--out "D:\\Temp\\kodc-ct-report.txt"`.

If automatic sampling does not find useful sea points, try a known open-sea latitude/longitude
within the source coverage:

```powershell
dotnet run --project src/KodcDiagnostics/KodcDiagnostics.csproj -c Release -- "D:\\Database\\KODC\\CT_KODC_Climatology_grid_9423.nc" --var CT --lat 35.5 --lon 126.5
```

The nearest source-grid point is used. It may still be NoData; choose another sea location
if the report contains no valid values.

## 3. What to send back

Send the generated `kodc-time-diagnostic-*.txt` report. Focus on:
- `Time attributes`, `Finite/non-fill time coordinate count`;
- the `Representative values` table (indices `0, 37, 74, ...` when slot 0 is usable);
- the last `Summary` and `Finding` lines.

If the time coordinate is entirely invalid and blocks differ, we **still must not label them
1994, 1995, ...** without further producer documentation. The current MarineEnvironment KODC
reader assumes the first 37 slots are climatology; this diagnostic gathers evidence before
changing that temporal interpretation.


## 4. 전체 검증 및 37개 시점 축소 파일 생성

원본을 먼저 보존하세요. PowerShell에서 저장소 최상위 폴더로 이동한 뒤 아래 두 검사를 실행합니다.
각 명령은 lon × lat × depth × 37 × (30-1)개 비교를 전부 검사합니다.
복호화한 DOUBLE 값의 비트 단위 동등성을 검사하므로 유효값뿐 아니라 NaN/결측도 포함합니다.

~~~powershell
git fetch origin
git switch feature/kodc-time-diagnostics
git pull origin feature/kodc-time-diagnostics

dotnet run --project src/KodcDiagnostics/KodcDiagnostics.csproj -c Release -- "E:\marineenv\db\국내기관\CT_KODC_Climatology_grid_9423.nc" --var CT --full-check
dotnet run --project src/KodcDiagnostics/KodcDiagnostics.csproj -c Release -- "E:\marineenv\db\국내기관\SA_KODC_Climatology_grid_9423.nc" --var SA --full-check
~~~

각 결과 파일 kodc-full-validation-CT-*.txt / SA-*.txt에서 다음을 확인합니다.

~~~text
Different values (bitwise): 0
Result: PASS - all 37-slot blocks are bitwise identical.
~~~

**CT, SA 양쪽 모두 PASS라면** 아래 두 명령으로 각각 별도의 NetCDF4 축소 파일을 생성할 수 있습니다.
이 명령들은 검증을 다시 수행한 다음 통과했을 때만 생성하므로, 바로 실행해도 안전합니다.

~~~powershell
dotnet run --project src/KodcDiagnostics/KodcDiagnostics.csproj -c Release -- "E:\marineenv\db\국내기관\CT_KODC_Climatology_grid_9423.nc" --var CT --compact "E:\marineenv\db\국내기관\CT_KODC_Climatology_grid_9423_37.nc"

dotnet run --project src/KodcDiagnostics/KodcDiagnostics.csproj -c Release -- "E:\marineenv\db\국내기관\SA_KODC_Climatology_grid_9423.nc" --var SA --compact "E:\marineenv\db\국내기관\SA_KODC_Climatology_grid_9423_37.nc"
~~~

프로그램은 다음 조건을 모두 검사합니다.

1. 모든 원본 데이터의 37개 주기가 첫 주기와 **비트 단위로 동일**한지 검사.
2. 원본 time 좌표에 유효 날짜가 없는지 검사. 시간 좌표에 유효값이 있으면 자동 축소 중단.
3. lon/lat/depth/time 배열 및 원본 관련 속성 보존. 기존의 무효 time 배열을 인위적인 날짜로 대체하지 않음.
4. 축소 NetCDF4 파일을 임시 이름으로 생성한 뒤 닫고 재열기.
5. 모든 축 좌표와 첫 37개 기간의 모든 CT 또는 SA 데이터를 원본과 재비교.
6. 검증 성공 시에만 최종 경로로 이동. 기존 원본/목적 파일 절대 덮어쓰지 않음.

검증이 통과하면:

~~~text
COMPLETE AUDIT RESULT
Different values (bitwise): 0
Result: PASS - all 37-slot blocks are bitwise identical.
COMPACT READ-BACK VALIDATION: PASS
Compact file created: ...
Original size (bytes): ...
Compact size (bytes): ...
~~~

첫 37개 시점의 격자 값은 121 × 97 × 14 × 37 = **6,079,766개**입니다.
실제 파일 용량 절감률은 기존 NetCDF 압축 여부에 따라 다릅니다.
main의 KODC 조회 로직은 변경하지 않았습니다. 새 파일을 실제 설정에 연결하기 전
원본과 축소 파일을 Viewer에서 같은 날짜/위치로 조회하는 종단 간 시험을 권장합니다.
