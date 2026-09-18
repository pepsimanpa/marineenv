# GOCI-II TSS downloader

The recommended internet-side acquisition path is now a **standalone PowerShell script**. Visual Studio, `dotnet build`, and the .NET SDK are not required to run it.

The downloader is intentionally separate from `MarineEnvironment.dll`; the operational/offline machine does not need internet access or an API key.

## Build-free files

- `tools/Goci2Downloader.ps1` — standalone downloader
- `tools/RunGoci2Downloader.cmd` — simple launcher for Windows

Requirements:

- Windows PowerShell 5.1 or newer
- Internet access to NOSC
- NOSC OPEN API ServiceKey
- `curl.exe` is used when available; otherwise PowerShell `Invoke-WebRequest` is used

## NOSC OPEN API used

```text
https://nosc.go.kr/openapi/GK2BNcMedia/search.do
```

Request parameters used:

```text
ServiceKey=<issued API key>
startDate=yyyyMMdd
endDate=yyyyMMdd
slot=13
ResultType=json
```

The script filters the response locally for exact mosaic file names:

```text
GK2B_GOCI2_L2_yyyyMMdd_HHmmss_LA_TSS.nc
```

This excludes `..._LA_S000_TSS.nc` through `..._LA_S011_TSS.nc` slot files.

## Authentication

The key is never written to the repository or manifest.

Preferred:

```powershell
$env:NOSC_SERVICE_KEY = "<issued-key>"
```

If the environment variable is missing, `Goci2Downloader.ps1` prompts for the ServiceKey interactively and keeps it only in memory.

## First test: no download

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Goci2Downloader.ps1 `
  -Output D:\GOCI2\TSS `
  -Keep 5 `
  -DryRun
```

Or from Command Prompt:

```bat
set NOSC_SERVICE_KEY=<issued-key>
tools\RunGoci2Downloader.cmd -Output D:\GOCI2\TSS -Keep 5 -DryRun
```

## Actual download

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Goci2Downloader.ps1 `
  -Output D:\GOCI2\TSS `
  -Keep 5
```

Historical validation date:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Goci2Downloader.ps1 `
  -Output D:\GOCI2\TSS `
  -Keep 5 `
  -AsOf 2026-09-15 `
  -DryRun
```

## Default behavior

```text
current KST date
    ↓
query slot=13 one day at a time, newest to oldest
    ↓
filter exact LA TSS mosaic products
    ↓
stop when newest 5 mosaics are found
    ↓
download missing files to .part
    ↓
validate NetCDF/HDF5 signature
    ↓
rename to final .nc
    ↓
retain newest 5 local mosaic files
    ↓
write goci2-download-manifest.json
```

Defaults:

- keep newest `5` mosaics
- search back up to `7` KST dates
- existing valid `.nc` files are not downloaded again
- `.part` is used during download; incomplete files are not promoted
- old files matching only the exact GOCI-II TSS mosaic naming rule are pruned
- unrelated `.nc` files are never pruned

The API response's `filePath` is tried first. If that does not return a NetCDF payload, the script also tries the Hyrax NetCDF-4 file-out form by appending `.nc4`. A downloaded payload is accepted only when it begins with a classic NetCDF or HDF5/NetCDF-4 signature.

## Offline transfer

After acquisition, transfer the retained files and optionally the manifest to the offline data directory:

```text
Database/GOCI2/TSS/
  GK2B_GOCI2_L2_..._LA_TSS.nc
  GK2B_GOCI2_L2_..._LA_TSS.nc
  GK2B_GOCI2_L2_..._LA_TSS.nc
  ...
  goci2-download-manifest.json
```

The offline MarineEnvironment source can then read these original mosaics at query time and calculate the project-derived turbidity value.

## Existing .NET console prototype

`src/Goci2Downloader` is retained for now as the compiled prototype created during the first implementation. It is **not required** for the PowerShell workflow above. If the script is adopted as the final acquisition method, the .NET console project can be removed later.

## Validation status

The PowerShell script does not require compilation. End-to-end live API/download validation still requires a valid NOSC ServiceKey; start with `-DryRun` before downloading 1 GB-class mosaic files.
