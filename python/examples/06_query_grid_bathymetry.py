import math

import matplotlib.pyplot as plt
import numpy as np

from _example_common import JINHAE_20KM, get_config_path
from marineenvironment import MarineEnvironment


config = get_config_path()

with MarineEnvironment() as env:
    env.initialize(config)

    grid = env.query_grid(
        "BADA2024_BATHYMETRY",
        **JINHAE_20KM,
        width=80,
        height=80,
        resolution_mode="Custom",
    )

    print("source:", grid["source_id"])
    print("grid:", grid["width"], "x", grid["height"])
    print("unit:", grid["unit"])
    print("min/max:", grid["minimum"], grid["maximum"])

    values = np.array(
        [np.nan if value is None else float(value) for value in grid["values"]],
        dtype=float,
    ).reshape(grid["height"], grid["width"])

    latitudes = np.asarray(grid["latitudes"], dtype=float)
    longitudes = np.asarray(grid["longitudes"], dtype=float)

    fig, ax = plt.subplots(num="MarineEnvironment - Bathymetry")
    mesh = ax.pcolormesh(
        longitudes,
        latitudes,
        values,
        shading="auto",
    )

    colorbar = fig.colorbar(mesh, ax=ax)
    colorbar.set_label(f'Bathymetry ({grid["unit"] or ""})')

    ax.set_title("Jinhae Bay - BADA2024 Bathymetry")
    ax.set_xlabel("Longitude")
    ax.set_ylabel("Latitude")
    ax.grid(True, alpha=0.25)

    # 위/경도 비율을 실제 거리감에 가깝게 보정한다.
    mid_lat = float(np.nanmean(latitudes))
    ax.set_aspect(1.0 / math.cos(math.radians(mid_lat)))

    fig.tight_layout()
    plt.show()
