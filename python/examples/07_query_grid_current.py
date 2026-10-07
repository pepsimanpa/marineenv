from datetime import datetime

from _example_common import EXAMPLE_20KM, get_config_path
from marineenvironment import MarineEnvironment


config = get_config_path()

with MarineEnvironment() as env:
    env.initialize(config)

    grid = env.query_grid(
        "KHOA_DAILY_CURRENT",
        **EXAMPLE_20KM,
        when=datetime(2026, 9, 17),
        width=30,
        height=30,
    )

    print("source:", grid["source_id"])
    print("speed min/max:", grid["minimum"], grid["maximum"], grid["unit"])
    print("native current vector count:", len(grid["current_vectors"] or []))

    for vector in (grid["current_vectors"] or [])[:10]:
        print(
            vector["latitude"],
            vector["longitude"],
            "speed=", vector["speed"],
            "direction=", vector["direction"],
        )
