# Seabed grade grid

The seabed-grade API is a project-derived analysis workflow layered on top of
independent seabed database sources.

## Source priority

Add `priority` to seabed source entries in `marineenvironment.json`.
Lower numbers have higher priority. Recommended values:

```json
{ "id": "KOREA_SEDIMENT", "type": "Seabed", "priority": 10, ... }
{ "id": "SHOM_SEABED",    "type": "Seabed", "priority": 20, ... }
```

At each analysis-cell center the DLL queries READY seabed layers in priority
order. If the domestic layer has a mapped value it is used. Otherwise the DLL
falls back to SHOM. If neither layer has a mapped value the cell is NoData.

Priority does not change the ordinary point-query contract: `Query` still
returns all independently available source and derived values.

## User inputs and grid

The Viewer and DLL accept either:

- cell count: Columns x Rows, or
- approximate cell size in kilometers; the DLL converts the area extent to
  Columns x Rows and evenly divides the requested rectangle.

Contact density and terrain are area-wide user inputs. Defaults are:

- ContactDensity = 1
- Terrain = Flat

## Grade lookup

The operational grade lookup is:

| Terrain | 50/50, burial <10% | 70/30, 10~20% | 70/30, 20~50% | 80/20, 50~75% | 100/0, >=75% | Rock, <10% |
|---|---|---|---|---|---|---|
| Flat | A | B | B | C | D | B |
| Normal | B | B | C | C | D | C |
| Rough | C | C | C | C | D | C |

The contact-density value 1/2/3 is appended to the letter, producing A1..D3.

The 10/20/50/75 boundaries are implemented as half-open intervals
10<=x<20, 20<=x<50, 50<=x<75, and x>=75. Existing project burial mappings
(5, 15, 35, 65, 85, and rock 0) therefore map unambiguously.

The result preserves the cell bounds/center, selected source and priority,
source classification, project operational sediment fraction, burial rate,
terrain, density, lookup bucket, and final grade.
