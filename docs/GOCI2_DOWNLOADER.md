# GOCI-II TSS downloader

`Goci2Downloader` is an **internet-side acquisition utility** for the offline MarineEnvironment deployment.
It discovers recent GOCI-II Level-2 Local Area **TSS mosaic** products from the National Ocean Satellite Center (NOSC), downloads only the newest files, and leaves the resulting `.nc` files ready for controlled transfer to the offline system.

The downloader is intentionally separate from `MarineEnvironment.dll`; the operational/offline machine does not need internet access or an API key.

## NOSC OPEN API used

Official NetCDF information API:

```text
https://nosc.go.kr/openapi/GK2BNcMedia/search.do
```

Request parameters used by this tool:

```text
ServiceKey=<issued API key>
startDate=yyyyMMdd
endDate=yyyyMMdd
slot=13
ResultType=json
```

NOSC documents slot `0..11` as individual GOCI-II slots and slot `13` as the Korean Peninsula whole-area product. The API does not document a `product=TSS` request filter, so the downloader queries slot 13 and filters the response locally for the exact mosaic filename form:

```text
GK2B_GOCI2_L2_yyyyMMdd_HHmmss_LA_TSS.nc
```

This deliberately excludes `..._LA_S000_TSS.nc` through `..._LA_S011_TSS.nc` slot files.

The OPEN API response supplies `fileName`, `filePath`, `product`, and UTC/KST observation times. The downloader first tries the returned `filePath` as the source-file URL. If the Hyrax server does not permit direct source-file access, it also tries the standard Hyrax NetCDF-4 file-out service by appending `.nc4` to the dataset URL. In both cases the payload is accepted only when the file begins with a valid classic NetCDF or HDF5/NetCDF-4 signature.

## Authentication

Do not commit the API key to the repository. Preferred use:

```powershell
$env:NOSC_SERVICE_KEY = "<issued-key>"
dotnet run --project src/Goci2Downloader -- --output D:\GOCI2\TSS --keep 5
```

Or pass the key for a one-off execution:

```powershell
dotnet run --project src/Goci2Downloader -- --service-key <issued-key> --output D:\GOCI2\TSS
```

The key is used only in the OPEN API request and is not written to the manifest.

## Default behavior

```text
current KST date
    ↓
query slot=13 one day at a time, newest to oldest
    ↓
filter exact LA TSS mosaic products
    ↓
stop when 5 newest mosaics are found
    ↓
stream-download missing files
    ↓
validate NetCDF/HDF5 signature
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

## Useful commands

List what would be selected without downloading:

```powershell
dotnet run --project src/Goci2Downloader -- --dry-run
```

Historical API check:

```powershell
dotnet run --project src/Goci2Downloader -- --as-of 2026-09-15 --dry-run
```

Keep only three recent files:

```powershell
dotnet run --project src/Goci2Downloader -- --output D:\GOCI2\TSS --keep 3
```

Download without deleting older local files:

```powershell
dotnet run --project src/Goci2Downloader -- --output D:\GOCI2\TSS --no-prune
```

## Offline transfer

After acquisition, transfer only the retained files and (optionally) the manifest to the offline data directory, for example:

```text
Database/GOCI2/TSS/
  GK2B_GOCI2_L2_..._LA_TSS.nc
  GK2B_GOCI2_L2_..._LA_TSS.nc
  GK2B_GOCI2_L2_..._LA_TSS.nc
  ...
  goci2-download-manifest.json
```

The offline turbidity source can then read these original mosaics at query time, filter invalid TSS pixels, aggregate the available TSS observations, and convert the resulting TSS to the project-derived turbidity value.

## Important limitation

API discovery and compilation can be tested without a NOSC key, but an end-to-end live download requires a valid issued key and current NOSC server access. If NOSC changes its download policy or disables both direct source-file access and the Hyrax NetCDF-4 file-out service, the tool will stop with an explicit non-NetCDF/HTTP error rather than save HTML or an error page as a `.nc` file.
