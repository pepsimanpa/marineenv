# marineenvironment Python package

Python wrapper for the repository's existing `MarineEnvironment.dll`.

The package does not reimplement the marine-environment algorithms in Python.
It loads the same .NET assembly through Python.NET and converts public query
results to Python dictionaries/lists.

## Development install

Build the .NET library first:

```powershell
dotnet build ..\src\MarineEnvironment\MarineEnvironment.csproj -c Release
python -m pip install -e .
```

Then:

```python
from marineenvironment import MarineEnvironment

with MarineEnvironment() as env:
    init = env.initialize(r"D:\MarineEnv\marineenvironment.json")

    result = env.query(
        latitude=35.10,
        longitude=129.05,
        depth=50,
    )

    for value in result["source_values"]:
        print(value["source_id"], value["type"], value["value"], value["unit"])
```

## DLL discovery

The wrapper looks for `MarineEnvironment.dll` in this order:

1. the explicit `dll_path` passed to `MarineEnvironment(...)`,
2. `MARINEENV_DLL_DIR`,
3. a bundled `marineenvironment/lib/MarineEnvironment.dll`,
4. this repository's Release/Debug build output.

For a separately deployed package, either bundle the DLL with
`build_package.py` or set `MARINEENV_DLL_DIR`.

## Build a wheel

From the `python` directory:

```powershell
python -m pip install build
python build_package.py
```

The script builds `MarineEnvironment.dll`, copies the managed DLL and related
build files into the Python package, and creates a wheel under `python/dist`.

The original marine database files and NetCDF-C/HDF5 native runtime are not
embedded in the wheel. Deploy those runtime inputs separately, as with the C#
integration.

## Main wrapper methods

- `initialize(config_path=None)`
- `get_sources()`
- `get_source_status(source_id)`
- `load_source(option)`
- `reload_source(option)`
- `unload_source(source_id)`
- `query(latitude, longitude, depth=None, when=None)`
- `query_source(source_id, latitude, longitude, depth=None, when=None)`
- `query_grid(...)`
- `query_seabed_grade_grid(...)`
- `close()`

`raw_manager` is available when an advanced caller needs direct access to the
underlying .NET `MarineEnvironmentManager`.


## Function-by-function examples

Runnable examples are under `python/examples/`. They use the common test point
`34.165 N, 128.170 E` and an approximately 20 km x 20 km South Sea area.

When run from this repository, the examples automatically use
`config/marineenvironment.json` unless a config path argument or
`MARINEENV_CONFIG` overrides it:

```powershell
python python\examples\01_initialize.py
python python\examples\03_query_all_point.py
python python\examples\08_query_seabed_grade_20km.py
```

See `python/examples/README.md` for the complete list.
