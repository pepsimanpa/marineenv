import math
from datetime import datetime

import matplotlib.pyplot as plt
import numpy as np

from _example_common import JINHAE_20KM, get_config_path
from marineenvironment import MarineEnvironment


config = get_config_path()

with MarineEnvironment() as env:
    env.initialize(config)

    grid = env.query_grid(
        "KHOA_DAILY_CURRENT",
        **JINHAE_20KM,
        when=datetime(2026, 9, 17),
        width=60,
        height=60,
    )

    print("source:", grid["source_id"])
    print("speed min/max:", grid["minimum"], grid["maximum"], grid["unit"])
    print("native current vector count:", len(grid["current_vectors"] or []))

    speeds = np.array(
        [np.nan if value is None else float(value) for value in grid["values"]],
        dtype=float,
    ).reshape(grid["height"], grid["width"])

    latitudes = np.asarray(grid["latitudes"], dtype=float)
    longitudes = np.asarray(grid["longitudes"], dtype=float)

    fig, ax = plt.subplots(num="MarineEnvironment - Current")
    mesh = ax.pcolormesh(
        longitudes,
        latitudes,
        speeds,
        shading="auto",
    )

    colorbar = fig.colorbar(mesh, ax=ax)
    colorbar.set_label(f'Current speed ({grid["unit"] or ""})')

    vectors = grid["current_vectors"] or []
    if vectors:
        vector_lons = np.asarray([float(v["longitude"]) for v in vectors])
        vector_lats = np.asarray([float(v["latitude"]) for v in vectors])
        directions = np.radians(
            np.asarray([float(v["direction"]) for v in vectors])
        )

        # 방향만 보기 쉽도록 동일 길이 화살표로 표시한다.
        # Current direction은 true north 기준 clockwise "toward" 방향이다.
        east = np.sin(directions)
        north = np.cos(directions)

        ax.quiver(
            vector_lons,
            vector_lats,
            east,
            north,
            angles="xy",
            scale_units="xy",
            scale=25,
            width=0.003,
        )

    ax.set_title("Jinhae Bay - KHOA Seasonal Mean Current")
    ax.set_xlabel("Longitude")
    ax.set_ylabel("Latitude")
    ax.grid(True, alpha=0.25)

    mid_lat = float(np.nanmean(latitudes))
    ax.set_aspect(1.0 / math.cos(math.radians(mid_lat)))

    fig.tight_layout()
    plt.show()
