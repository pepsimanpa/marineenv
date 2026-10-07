from _example_common import EXAMPLE_20KM, get_config_path
from marineenvironment import MarineEnvironment


config = get_config_path()

with MarineEnvironment() as env:
    env.initialize(config)

    grid = env.query_grid(
        "BADA2024_BATHYMETRY",
        **EXAMPLE_20KM,
        width=40,
        height=40,
        resolution_mode="Custom",
    )

    print("source:", grid["source_id"])
    print("grid:", grid["width"], "x", grid["height"])
    print("unit:", grid["unit"])
    print("min/max:", grid["minimum"], grid["maximum"])

    center_row = grid["height"] // 2
    center_col = grid["width"] // 2
    center_index = center_row * grid["width"] + center_col
    print("center value:", grid["values"][center_index])

    metadata = grid.get("metadata") or {}
    print(
        "vertical:",
        metadata.get("verticalConvention")
        or metadata.get("vertical_convention"),
        metadata.get("verticalReference")
        or metadata.get("vertical_reference"),
    )
