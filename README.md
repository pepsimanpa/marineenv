# MarineEnvironment

`MarineEnvironment` is a .NET class library for registering marine-environment data sources and querying source-native environmental data through a common API.

The first development milestone targets **NetCDF (`.nc`) sources only**. Shapefile/SHOM and generated JSON/DAT sources will be added after the NetCDF core is stabilized.

## Projects

- `src/MarineEnvironment` — .NET 8 class library (`MarineEnvironment.dll`)
- `src/MarineEnvironment.Viewer` — .NET 8 WPF validation viewer that renders data only through the DLL public API

## Initial scope

- Configuration-driven source registration (`marineenvironment.json`)
- Runtime source registration/removal/reload API
- Native NetCDF-C access without forcing source data into a common grid
- Source-native nearest-neighbour lookup by latitude/longitude and optional depth/time
- `QueryGrid()` API for visualization/validation
- Monthly file patterns such as WOA23/GDEM (`{MM}`)
- Multiple source results for the same environmental element

Initial source targets:

- ETOPO1 / ETOPO 2022 / GEBCO bathymetry
- WOA23 temperature and salinity
- GDEM-V temperature and salinity
- FES2014a tidal-current NetCDF files (reader/calculation to be completed after inspecting extracted constituent files)

## Validation Viewer

The viewer deliberately does **not** read `.nc` files directly. It loads `marineenvironment.json`, obtains registered-source status from `MarineEnvironment.dll`, and renders a sampled grid through `MarineEnvironmentManager.QueryGrid()`.

Current viewer functions:

- Load `marineenvironment.json`
- Show source ID/type/status
- Enter latitude/longitude view bounds
- Select optional depth and month
- Render a 2-D color raster through `QueryGrid()`
- Show cursor latitude/longitude/value
- Click a raster point to cross-check the same location through the single-point `Query()` API

Release build outputs:

```text
src/MarineEnvironment/bin/Release/net8.0/MarineEnvironment.dll
src/MarineEnvironment.Viewer/bin/Release/net8.0-windows/MarineEnvironment.Viewer.exe
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

Single-point query:

```csharp
using MarineEnvironment;
using MarineEnvironment.Models;

using var marine = new MarineEnvironmentManager();
var init = marine.Initialize("marineenvironment.json");

var values = marine.Query(new EnvironmentQuery
{
    Latitude = 35.10,
    Longitude = 129.05,
    Depth = 50,
    DateTime = new DateTime(2026, 8, 21, 12, 0, 0)
});
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
