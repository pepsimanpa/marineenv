from datetime import datetime

from _example_common import JINHAE_LAT, JINHAE_LON, get_config_path
from marineenvironment import MarineEnvironment


config = get_config_path()

with MarineEnvironment() as env:
    env.initialize(config)

    result = env.query(
        latitude=JINHAE_LAT,
        longitude=JINHAE_LON,
        depth=10.0,
        when=datetime(2026, 9, 17, 12, 0),
    )

    print("요청 좌표:", result["requested_latitude"], result["requested_longitude"])

    print("\n=== SourceValues ===")
    for item in result["source_values"]:
        print(
            f'{item["source_id"]:24} '
            f'{item["type"]:12} '
            f'value={item["value"]} '
            f'unit={item["unit"]}'
        )

        if item["type"] == "Bathymetry":
            metadata = item.get("metadata") or {}
            print(
                "  vertical:",
                metadata.get("verticalConvention")
                or metadata.get("vertical_convention"),
                metadata.get("verticalReference")
                or metadata.get("vertical_reference"),
            )

    print("\n=== DerivedValues ===")
    for item in result["derived_values"]:
        print(item["source_id"], item["type"], item["value"], item["unit"])
