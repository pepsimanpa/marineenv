# Independent Korean deposit shapefile source

This source adds **Korea sediment deposits type** without changing the existing
SHOM reader, SHOM catalog, SHOM mapping JSON, or SHOM burial-rate rules.

## Configuration

Place the same-basename `.shp`, `.shx`, `.dbf`, and `.prj` files together,
using the original WGS84 geographic projection. The uploaded dataset also has
a `.cpg` (UTF-8); keep it with the other files. Only the ASCII `deposit`
field is read by this implementation, so no non-ASCII DBF metadata is altered.

```json
{
  "id": "KOREA_SEDIMENT",
  "type": "Seabed",
  "format": "KoreaSediment",
  "enabled": true,
  "path": "../Database/KOREA_SEDIMENT/korea_sediment_deposits_type.shp"
}
```

**Do not** set a SHOM `seabedMappingPath` on the Korean source.
SHOM and Korean source IDs are independent and may both be enabled.

## Point query contract

`manager.Query(query)` returns an independently identified raw record in
`SourceValues` and its project-defined domestic operational interpretation
in `DerivedValues`.

- Source ID: `KOREA_SEDIMENT` (or configured ID).
- Raw `EnvironmentValue.Value`: `KoreaSedimentValue` with original,
  CASE-SENSITIVE `deposit` code and original sediment name.
- Derived `EnvironmentValue.Value`: `KoreaSedimentDerivedValue` with source
  code, original classification, primary category, operational seabed category,
  representative mud/sand fractions (null for rock), and burial-rate percentage.
- `mappingTableId = KOREA_DEPOSIT_OPERATIONAL_V1` identifies the fixed,
  project-approved interpretation. These values are not measured contents
  or actual/validated burial probabilities.
- SHOM still uses `SeabedValue` and `SeabedDerivedValue`. A position
  that falls within both products can yield **two raw and two derived**
  independent results. There is no priority/overlay and no fallback from
  one product to the other. A missing domestic polygon yields no
  domestic result, regardless of SHOM coverage.

`manager.QueryGrid("KOREA_SEDIMENT", gridQuery)` returns category
**indices 1..22**, metadata `classificationScheme=KoreaDeposit`, and
labels containing the raw code, original class, operational class and
burial rate. Viewer colors these classes by the domestic mapping, not
the SHOM official legend.

## Agreed 22-code operational table

Mud/sand fractions below are *project representative fractions*, NOT
measured grain-size fractions from the shapefile. Fractions for
gravel-bearing classifications deliberately omit explicit gravel
rather than claiming a full three-component grain-size analysis.

| Deposit code | Original sediment class | Primary | Operational seabed (mud/sand) | Burial rate |
|---|---|---|---|---:|
| R | Rocky Bottom | 암반 | 암반 (n/a) | 0% |
| G | Gravel | 암반/자갈 | 암반 (n/a) | 0% |
| sG | Sandy Gravel | 암반/자갈 | 암반 (n/a) | 0% |
| msG | Muddy Sandy Gravel | 암반/자갈 | 암반 (n/a) | 0% |
| mG | Muddy Gravel | 암반/자갈 | 암반 (n/a) | 0% |
| S | Sand | 모래 | 뻘·모래 반반 (50/50) | 5% |
| (g)S | Slightly Gravelly Sand | 모래 | 뻘·모래 반반 (50/50) | 5% |
| gS | Gravelly Sand | 모래 | 뻘·모래 반반 (50/50) | 5% |
| cS | Clayey Sand | 뻘·모래 혼합 | 뻘·모래 반반 (50/50) | 5% |
| zS | Silty Sand | 뻘·모래 혼합 | 뻘·모래 반반 (50/50) | 5% |
| mS | Muddy Sand | 뻘·모래 혼합 | 뻘·모래 반반 (50/50) | 5% |
| (g)mS | Slightly Gravelly Muddy Sand | 뻘·모래 혼합 | 뻘·모래 반반 (50/50) | 5% |
| gmS | Gravelly Muddy Sand | 뻘·모래 혼합 | 뻘·모래 반반 (50/50) | 5% |
| sM | Sandy Mud | 뻘 우세 혼합 | 뻘 우세 혼합 (70/30) | 35% |
| (g)sM | Slightly Gravelly Sandy Mud | 뻘 우세 혼합 | 뻘 우세 혼합 (70/30) | 35% |
| sZ | Sandy Silt | 뻘 우세 혼합 | 뻘 우세 혼합 (70/30) | 35% |
| gM | Gravelly Mud | 뻘 우세 혼합 | 뻘 우세 혼합 (70/30) | 35% |
| (g)M | Slightly Gravelly Mud | 뻘 | 뻘 우세 혼합 (80/20) | 65% |
| Z | Silt | 뻘 | 뻘 우세 혼합 (80/20) | 65% |
| M | Mud | 뻘 | 뻘 우세 혼합 (80/20) | 65% |
| sC | Sandy Clay | 뻘 | 뻘 (100/0) | 85% |
| C | Clay | 뻘 | 뻘 (100/0) | 85% |

The original Folk meaning may differ from operational grouping. The project-derived
non-rock scale intentionally starts at 50/50, so even source classes such as
`S`, `(g)S`, and `gS` retain their original sand classification while their
operational output is 50/50. Likewise, `gmS` is sand-major by origin but is
grouped into the same 50/50 operational category. Burial percentages are mapped
by this project-specific table, not calculated from the fraction alone.

## Validation and caveats

The source reader uses a built-in polygon SHP/DBF parser and the
same envelope+point-in-polygon search pattern as the existing SHOM
reader, but it has its own class, index, catalog and output types.

- Only WGS84 geographic-coordinate shapefiles are accepted.
- Case-sensitive lookup prevents confusion of `mS` and `sM`.
- The reader does not repair self-intersecting input polygons.
  The supplied 622-polygon dataset contains two geometrically invalid
  polygons; review their exact boundary behavior if using their regions
  operationally. A robust GIS repair/validation pipeline is a separate
  source-preprocessing decision; input data is not modified here.
- The source does not redistribute/repackage uploaded binary shapefile
  files; point to locally managed DB files in configuration.

The repository's synthetic DB test generates minimal Polygon .shp/.shx/
.dbf/.prj files and checks every agreed classification, grid labels,
case-sensitive lookup, independently coexisting SHOM results and CRS
rejection:

```powershell
dotnet run --project tests/KoreaSedimentSmoke/KoreaSedimentSmoke.csproj -c Release
```
