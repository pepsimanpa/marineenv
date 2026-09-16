# MarineEnvironment

`MarineEnvironment` is a .NET class library for registering marine-environment data sources and querying source-native environmental data through a common API.

The library supports generic NetCDF sources plus verified special-source readers such as FES2014a tidal currents and SHOM seabed sediment polygons.

## Projects

- `src/MarineEnvironment` — .NET 5 class library (`MarineEnvironment.dll`)
- `src/MarineEnvironment.Viewer` — .NET 5 WPF validation viewer that renders data only through the DLL public API

## Current scope

- Configuration-driven source registration (`marineenvironment.json`)
- Runtime source registration/removal/reload API
- Native NetCDF-C access without forcing source data into a common grid
- Source-native nearest-neighbour lookup by latitude/longitude and optional depth/time
- Queries outside a NetCDF source's spatial coverage return NoData instead of repeating the nearest edge cell
- `QueryGrid()` API for visualization/validation
- Monthly file patterns such as WOA23/GDEM (`{MM}`)
- Multiple source results for the same environmental element
- FES2014a tidal-current harmonic synthesis
- SHOM categorical seabed sediment polygons and optional user-defined operational mapping
- Martin et al. (2015) global seafloor sediment porosity grid
- GOCI-II Level-2 Local Area mosaic TSS input with query-time derived turbidity

Current source targets include:

- ETOPO1 / ETOPO 2022 / GEBCO bathymetry
- WOA23 temperature and salinity
- GDEM-V temperature and salinity
- FES2014a tidal currents
- GOCI-II TSS-derived turbidity
- Martin et al. (2015) global seafloor sediment porosity
- SHOM worldwide seabed sediment map

## GOCI-II TSS-derived turbidity

The GOCI-II source is designed for an offline workflow. Original `..._LA_TSS.nc` mosaic files remain on disk; the library does **not** generate a separate averaged database.

At query time it:

1. selects up to five LA mosaic files (all files when the directory contains 3-5 validation samples),
2. reads the TSS value for the requested point/cell from each file,
3. excludes pixels flagged as Cloud/Ice, Land, AC_Fail, or TSS_Fail,
4. calculates the arithmetic mean of the remaining TSS observations,
5. derives turbidity with the project-selected relation `Turbidity_NTU = 0.3671 * TSS_mg/L`.

The arithmetic-mean approach is consistent with the project use case and is also aligned with a KIOST technical-report workflow that formed representative GOCI-II TSS values by averaging multiple observations. Our initial validation intentionally uses only 3-5 downloaded mosaics, so it must be described as a sampled representative value rather than a full monthly/annual climatology.

GOCI-II TSS is `g/m^3`, which is numerically equal to `mg/L`. The resulting NTU is therefore a **derived project value**, not a direct GOCI-II turbidity observation. Metadata records the selected/used files, TSS samples, mean TSS, quality filtering, conversion factor, and the site-specific scope of the selected Gomso Bay relation.

Example configuration:

```json
{
  "id": "GOCI2_TURBIDITY",
  "type": "Turbidity",
  "format": "Goci2Tss",
  "enabled": true,
  "path": "../Database/GOCI2/TSS",
  "variable": "TSS",
  "latitudeVariable": "latitude",
  "longitudeVariable": "longitude",
  "unit": "NTU"
}
```

For the initial software validation, place 3-5 original LA mosaic TSS files in the configured directory. If more than five are present, the five observations nearest to the requested time are used; without a requested time, the latest five are used.

## Martin et al. (2015) global seafloor porosity

The supporting-information file `grl53425-sup-0002-supinfo.grd` was verified as a GMT-generated NetCDF4/HDF5 grid and can be read by the generic `NetCdfDataSource`; no dedicated `.grd` parser is required.

Verified structure:

```text
x: 4320 float64 values, -179.958333... to 179.958333...
y: 2160 float64 values,  -89.958333... to  89.958333...
z: 2160 x 4320 float32 porosity values
spacing: 0.083333333... degree = 5 arc-minute
registration: pixel registered (GMT node_offset = 1)
NoData: NaN
observed z range: 26.143 to 93.249
```

Example configuration:

```json
{
  "id": "MARTIN2015_POROSITY",
  "type": "Porosity",
  "format": "NetCdf",
  "enabled": true,
  "path": "D:/MarineDB/Porosity/Martin2015/grl53425-sup-0002-supinfo.grd",
  "variable": "z",
  "latitudeVariable": "y",
  "longitudeVariable": "x",
  "unit": "%",
  "metadata": {
    "dataset": "Martin et al. (2015) global seafloor sediment porosity",
    "grid": "5 arc-minute pixel-registered",
    "fileFormat": "GMT NetCDF4/HDF5"
  }
}
```

The generic NetCDF reader treats non-finite values (`NaN`, `Infinity`) as NoData so the porosity grid's NaN fill cells are not returned as valid point/grid values. Because this is NetCDF4/HDF5, the deployed NetCDF-C runtime must include its HDF5 dependencies.

## FES2014a tidal-current constituent modes

FES2014a currents are supplied separately as eastward and northward velocity atlases. Each component contains 34 tidal constituent NetCDF files. `MarineEnvironment` defines three selectable synthesis modes:

- `Major4` — M2, S2, K1, O1
- `Major6` — M2, S2, K1, O1, N2, K2
- `Full` — all 34 FES2014a current constituents

The mode is configured with `currentConstituentMode` on the current source. The default is `Major4`.

```json
{
  "id": "FES2014A_CURRENT",
  "type": "Current",
  "path": "D:/MarineDB/FES2014a",
  "currentConstituentMode": "Major4"
}
```

## Validation Viewer

The viewer deliberately does **not** read source files directly. It loads `marineenvironment.json`, obtains registered-source status from `MarineEnvironment.dll`, and renders a sampled grid through `MarineEnvironmentManager.QueryGrid()`.

Current viewer functions:

- Load `marineenvironment.json`
- Show source ID/type/status
- Enter latitude/longitude view bounds
- Select optional depth and month
- Render one selected source as a 2-D color raster through `QueryGrid()`
- Show cursor latitude/longitude/value
- Zoom/pan the rendered raster and show a dynamic geographic scale bar
- Click a raster point to query **all READY sources** through the multi-source `Query()` API
- Show returned source/type/value/unit/native-grid coordinate information in a table

Release build outputs:

```text
src/MarineEnvironment/bin/Release/net5.0/MarineEnvironment.dll
src/MarineEnvironment.Viewer/bin/Release/net5.0-windows/MarineEnvironment.Viewer.exe
```

The viewer is intended as a DLL validation tool rather than a general-purpose NetCDF browser such as Panoply.

## Configuration path rules

`path` in `marineenvironment.json` accepts both absolute and relative paths.

- **Absolute path**: used as specified.
- **Relative path**: resolved against the directory containing `marineenvironment.json`, not against the process working directory.
- Both file paths and directory paths are supported, depending on the source definition.

Relative-path example:

```json
{
  "id": "ETOPO1",
  "type": "Bathymetry",
  "path": "./Database/ETOPO1/ETOPO1_Ice_g_gmt4.nc"
}
```

Windows absolute-path example:

```json
{
  "id": "ETOPO1",
  "type": "Bathymetry",
  "path": "D:\\MarineDB\\ETOPO1\\ETOPO1_Ice_g_gmt4.nc"
}
```

Forward slashes can also be used on Windows if preferred:

```json
{
  "path": "D:/MarineDB/ETOPO1/ETOPO1_Ice_g_gmt4.nc"
}
```

## Runtime dependency

The managed library uses the stable NetCDF-C API through P/Invoke. The target application must deploy a compatible NetCDF-C native runtime (`netcdf.dll` on Windows, with its HDF5 dependencies when NetCDF4 files are used) where the process can load it.

## Public API examples

Multi-source point query:

```csharp
using MarineEnvironment;
using MarineEnvironment.Models;

using var marine = new MarineEnvironmentManager();
var init = marine.Initialize("marineenvironment.json");

var result = marine.Query(new EnvironmentQuery
{
    Latitude = 35.10,
    Longitude = 129.05,
    Depth = 50,
    DateTime = new DateTime(2026, 8, 21, 12, 0, 0)
});

foreach (var value in result.Values)
{
    Console.WriteLine($"{value.Type} / {value.SourceId} / {value.Value} {value.Unit}");
}
```

Grid query for visualization:

```csharp
var grid = marine.QueryGrid("ETOPO1", new GridQuery
{
    MinLatitude = 32,
    MaxLatitude = 43,
    MinLongitude = 122,
    MaxLongitude = 133,
    Width = 320,
    Height = 220
});
```

See `examples/marineenvironment.example.json` for the current configuration shape.
