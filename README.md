# MarineEnvironment

`MarineEnvironment` is a .NET class library for registering marine-environment data sources and querying source-native environmental data through a common API.

The first development milestone targets **NetCDF (`.nc`) sources only**. Shapefile/SHOM and generated JSON/DAT sources will be added after the NetCDF core is stabilized.

## Initial scope

- Configuration-driven source registration (`marineenvironment.json`)
- Runtime source registration/removal/reload API
- Native NetCDF-C access without forcing source data into a common grid
- Source-native nearest-neighbour lookup by latitude/longitude and optional depth/time
- Monthly file patterns such as WOA23/GDEM (`{MM}`)
- Multiple source results for the same environmental element

Initial source targets:

- ETOPO1 / ETOPO 2022 / GEBCO bathymetry
- WOA23 temperature and salinity
- GDEM-V temperature and salinity
- FES2014a tidal-current NetCDF files (reader/calculation to be completed after inspecting extracted constituent files)

## Runtime dependency

The managed library uses the stable NetCDF-C API through P/Invoke. The target application must deploy a compatible NetCDF-C native runtime (`netcdf.dll` on Windows, with its HDF5 dependencies when NetCDF4 files are used) where the process can load it.

## Planned public API

```csharp
using MarineEnvironment;

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

See `examples/marineenvironment.example.json` for the configuration shape.
