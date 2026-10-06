# MarineEnvironment DLL API

`MarineEnvironment.dll` is a .NET 5 library. The public entry point is
`MarineEnvironmentManager`.

## 1. Initialize

```csharp
using MarineEnvironment;
using MarineEnvironment.Models;

using var env = new MarineEnvironmentManager();
var init = env.Initialize(@"DB\marineenvironment.json");
```

`Initialize()` without an argument reads `marineenvironment.json` from the
application base directory.

Use `GetSources()` to inspect the configured source status.


## Vertical sign convention

Bathymetry/elevation source values are preserved exactly as stored in each
source product. The DLL does **not** force every bathymetry source to one sign.

Use first-class source configuration to declare how a value should be
interpreted:

```json
{
  "id": "ETOPO1",
  "type": "Bathymetry",
  "verticalConvention": "ElevationPositiveUp"
}
```

```json
{
  "id": "BADA2024_BATHYMETRY",
  "type": "Bathymetry",
  "verticalConvention": "DepthPositiveDown",
  "verticalReference": "MSL"
}
```

`ElevationPositiveUp` means seabed elevations are normally negative and land
elevations are positive. `DepthPositiveDown` means water depth is normally
positive. The declared `verticalConvention` and optional
`verticalReference` are copied automatically into point/grid result
`Metadata`.

This preserves source-native semantics: for example, ETOPO `-100 m` remains
`-100 m`, while a BADA `+100 m` depth remains `+100 m`.

## 2. Query one position

`Query(EnvironmentQuery)` is the recommended integration API. One call returns
both source/database values and project-derived values.

```csharp
var result = env.Query(new EnvironmentQuery
{
    Latitude = 35.0,
    Longitude = 129.0,
    Depth = 10.0,
    DateTime = DateTime.Now
});

foreach (var source in result.SourceValues)
    Console.WriteLine($"SOURCE  {source.Type}: {source.Value} {source.Unit}");

foreach (var derived in result.DerivedValues)
    Console.WriteLine($"DERIVED {derived.Type}: {derived.Value} {derived.Unit}");
```

The result contract is:

```text
EnvironmentQueryResult
  RequestedLatitude
  RequestedLongitude
  RequestedDepth
  RequestedDateTime
  SourceValues[]       // values read from configured source products
  DerivedValues[]      // values calculated by MarineEnvironment in the same call
```

`Values` remains as a compatibility view that concatenates `SourceValues` and
`DerivedValues`. New integrations should use the two explicit collections.

Current derived point results include:

- GOCI-II mean TSS -> turbidity (NTU)
- SHOM source sediment -> configured operational seabed mapping
- ETOPO terrain + Martin porosity -> estimated seabed result

Derived values carry provenance/model information in `Metadata` and are not
presented as directly observed source values.

### GOCI-II example

For a GOCI-II TSS source, the source and derived results are separated:

```text
SourceValues:
  Type     = Tss
  Value    = mean TSS
  Unit     = g/m^3
  Variable = TSS_Mean

DerivedValues:
  Type     = Turbidity
  Value    = derived NTU
  Unit     = NTU
  Variable = DerivedTurbidity
```

The project relation is recorded in derived metadata together with the model,
factor, mean TSS and valid-observation count.

## 3. Query one named source

Use `QuerySource` when only one configured source is required but its direct
derived values should be returned in the same call.

```csharp
var result = env.QuerySource(
    "GOCI2_TSS",
    new EnvironmentQuery
    {
        Latitude = 35.0,
        Longitude = 129.0,
        DateTime = DateTime.Now
    });

var tss = result.SourceValue;
var derived = result.DerivedValues;
```

The older `Query(string sourceId, EnvironmentQuery)` method remains available
and returns only the source value.

Cross-source models such as ETOPO + Martin estimated seabed are produced by the
all-source `Query(EnvironmentQuery)` call.

## 4. Query a raster/grid

`QueryGrid` returns the raster for one configured source.

```csharp
var grid = env.QueryGrid(
    "GOCI2_TSS",
    new GridQuery
    {
        MinLatitude = 32,
        MaxLatitude = 43,
        MinLongitude = 122,
        MaxLongitude = 133,
        DateTime = DateTime.Now,
        Width = 320,
        Height = 220,
        ResolutionMode = GridResolutionMode.Custom
    });

double? value = grid.GetValue(row, column);
```

`GridResult` exposes `Latitudes`, `Longitudes`, `Values`, `Minimum`,
`Maximum`, `Unit`, `Variable`, and source metadata. Grid queries remain
source-raster oriented; the unified derived contract currently applies to point
queries.


## 5. Calculate a seabed-grade analysis grid

`QuerySeabedGradeGrid` divides a rectangular area into analysis cells and
calculates one operational grade (A1..D3) at each cell center.

```csharp
var grades = env.QuerySeabedGradeGrid(new SeabedGradeGridQuery
{
    MinLatitude = 34.0,
    MaxLatitude = 35.0,
    MinLongitude = 128.0,
    MaxLongitude = 129.0,
    GridMode = SeabedGradeGridMode.CellCount,
    Columns = 20,
    Rows = 10,
    ContactDensity = 1,          // default
    Terrain = SeabedTerrain.Flat // default
});

var cell = grades.GetCell(0, 0);
Console.WriteLine($"{cell.Grade} from {cell.SourceId}");
```

Alternatively set `GridMode = CellSizeKilometers` and
`CellSizeKilometers`; the DLL determines rows/columns from the requested
geographic bounds.

Seabed sources are selected per cell using `DataSourceOption.Priority`:
lower numbers are tried first, and a lower-priority source is used only when a
higher-priority source has no usable mapped seabed value at that cell center.
This priority affects the seabed-grade fallback workflow only. Normal
`Query` and `QuerySource` calls still return independent source results.

Recommended configuration is domestic sediment priority 10 and SHOM priority 20.

The grade table uses project-derived seabed/burial values plus two caller inputs:
`ContactDensity` (1..3) and `SeabedTerrain` (Flat/Normal/Rough).
`SeabedGradeGridResult.Cells` preserves the selected source ID, source
priority, original source code/classification, operational mud/sand fraction,
burial rate, grade bucket and final grade. `ToGridResult()` provides an
A1..D3 display raster for the validation Viewer.

## 6. Query concurrency and GOCI-II warm-up

Public `Query`, `QuerySource`, and `QueryGrid` calls are serialized inside
`MarineEnvironmentManager`. This is intentional because the library shares source-reader
state and native NetCDF-C access. Callers may invoke the public API from different threads,
but the manager executes source access one request at a time.

The validation viewer also ignores an additional map click while a point query is already
running so overlapping native reads are not started accidentally.

GOCI-II uses a 2-D curvilinear latitude/longitude grid. The first time a navigation geometry
is encountered, MarineEnvironment builds a coarse geolocation index with strided NetCDF
reads. The index is then:

- shared by all selected GOCI-II mosaic readers in the process,
- persisted in the user's local application-data cache,
- validated against navigation sentinel cells before reuse,
- loaded during later source initialization when a valid persistent cache exists.

The nearest source pixel for a point query is also shared across the selected GOCI-II
mosaics, so latitude/longitude refinement is performed once per queried coordinate rather
than once per mosaic. The original NetCDF files remain unchanged.
