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
