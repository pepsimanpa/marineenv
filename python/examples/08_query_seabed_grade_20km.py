import math

import matplotlib.pyplot as plt
import numpy as np

from _example_common import JINHAE_20KM, get_config_path
from marineenvironment import MarineEnvironment


config = get_config_path()

with MarineEnvironment() as env:
    env.initialize(config)

    result = env.query_seabed_grade_grid(
        **JINHAE_20KM,
        grid_mode="CellSizeKilometers",
        cell_size_kilometers=2.0,
        contact_density=1,
        terrain="Flat",
    )

    print("model:", result["model_id"])
    print("area grid:", result["columns"], "x", result["rows"])
    print("grade cells:", result["grade_count"])
    print("NoData cells:", result["no_data_count"])

    rows = result["rows"]
    columns = result["columns"]

    values = np.full((rows, columns), np.nan, dtype=float)
    labels = [["" for _ in range(columns)] for _ in range(rows)]

    for cell in result["cells"]:
        row = cell["row"]
        column = cell["column"]

        if cell["grade_index"] is not None:
            values[row, column] = float(cell["grade_index"])
            labels[row][column] = cell["grade"] or ""

    fig, ax = plt.subplots(num="MarineEnvironment - Seabed Grade")

    cmap = plt.get_cmap("tab20", 12).copy()
    cmap.set_bad("lightgray")

    image = ax.imshow(
        values,
        origin="upper",
        extent=[
            result["min_longitude"],
            result["max_longitude"],
            result["min_latitude"],
            result["max_latitude"],
        ],
        interpolation="nearest",
        vmin=0.5,
        vmax=12.5,
    )
    image.set_cmap(cmap)

    grade_names = [
        "A1", "A2", "A3",
        "B1", "B2", "B3",
        "C1", "C2", "C3",
        "D1", "D2", "D3",
    ]

    colorbar = fig.colorbar(image, ax=ax, ticks=range(1, 13))
    colorbar.ax.set_yticklabels(grade_names)
    colorbar.set_label("Seabed grade")

    for cell in result["cells"]:
        grade = cell["grade"]
        text = grade if grade else "-"
        ax.text(
            cell["center_longitude"],
            cell["center_latitude"],
            text,
            ha="center",
            va="center",
            fontsize=8,
        )

    ax.set_title(
        "Jinhae Bay - Seabed Grade "
        f'({result["terrain"]}, contact density {result["contact_density"]})'
    )
    ax.set_xlabel("Longitude")
    ax.set_ylabel("Latitude")
    ax.grid(False)

    mid_lat = (result["min_latitude"] + result["max_latitude"]) / 2.0
    ax.set_aspect(1.0 / math.cos(math.radians(mid_lat)))

    fig.tight_layout()
    plt.show()
