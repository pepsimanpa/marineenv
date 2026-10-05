# KHOA CSV: cross-year seasonal current mean

The KHOA numerical tidal-current CSV has **dates, not observation hours**. This source
therefore exposes a **seasonal historical reference current**; it must not be presented
as a tidal prediction at the requested instant. FES2014a remains the time-dependent
harmonic tidal-current source.

## Point-query contract

- `EnvironmentQuery.DateTime`: only **month and day** are used for KHOA source
  selection. The requested year and time-of-day are ignored. KHOA does **not** convert
  UTC or local time, because the CSV itself has no time-of-day or time-zone metadata.
  Callers must supply their intended civil calendar date.
- All available yearly CSV files are read. For **each physical point in each source
  file year**, select at most one record whose month/day is within
  `maxTemporalOffsetDays` of the requested month/day (default ±7 days).
  Prefer the nearest day, then the preceding date on an equal-distance tie.
- The selection crosses the December/January boundary. A Feb 29 request is
  evaluated against Feb 28 in non-leap source years.
- Points from different source years are matched using the existing 5 m
  coordinate-identity tolerance. A source year with no record in the window
  does not contribute to that point's average.
- Convert each selected speed/direction to eastward and northward components.
  **Average each component equally across contributing years**, and convert
  that single mean vector back to speed/direction. Do **not** calculate an
  arithmetic mean of direction angles or use all nearby days as unequal weights.
- `Query` returns **one** `EnvironmentValue` containing **one** `CurrentValue`
  per requested position. `QueryGrid` displays the identical point-level
  means in its raster and source-native current arrows.
- A canceled-out (zero) mean vector has speed 0 and direction 0 as a
  technical placeholder; the direction has no physical meaning at zero speed.
- If the spatially nearest valid composite point exceeds `maxNearestDistanceKm`
  (default 30 km), or no source years supply a point in the seasonal window,
  the point query returns no data.

The source reader currently assumes that KHOA CSV direction is a current-flow
**toward** bearing clockwise from true north. Confirm this convention against
authoritative source metadata before treating averaged directions as validated
physical observations.

## Provenance, interpretation, and performance

- The returned `EnvironmentValue.DateTime` is the **requested date** rather than
  an invented observation date; source records can span multiple years.
- Point metadata includes `sourceSampleCount`, `sourceYears`,
  `sourceDateMinimum`, `sourceDateMaximum`, and
  `maximumTemporalOffsetDaysUsed`. The common source metadata includes
  `temporalMode=PerPointCrossYearSeasonalVectorMean`,
  `contributingYearCount`, and cross-point coverage/offset statistics.
- Native grid arrows have null `SourceDate` and `TemporalOffsetDays`, because
  the mean has no single observation date.
- Each uncached seasonal request examines all available yearly files. The
  existing composite cache holds up to six requested dates; the year-index
  LRU holds up to eight annual files to avoid repeatedly parsing a typical
  six-file archive. Large archives should be performance-tested.

A seasonal U/V mean does **not** preserve tidal phase. In regions with reversing
currents, a low mean speed can coexist with strong instantaneous currents.
Do not substitute this product for FES2014 tidal predictions or use it as
an observed flow at the requested timestamp.

## Automated synthetic CSV check

The no-external-package console smoke test creates tiny temporary annual CSVs,
calls the public DLL interfaces, and checks seasonal averaging, one record
per year, past-first tie behavior, year/time independence, New Year and leap-day
handling, raster/vector agreement, and NoData:

```powershell
dotnet run --project tests/KhoaSeasonalSmoke/KhoaSeasonalSmoke.csproj -c Release
```

It never modifies production KHOA CSV files.
