from _example_common import EXAMPLE_LAT, EXAMPLE_LON, get_config_path
from marineenvironment import MarineEnvironment


config = get_config_path()

with MarineEnvironment() as env:
    env.initialize(config)

    result = env.query_source(
        "KOREA_SEDIMENT",
        latitude=EXAMPLE_LAT,
        longitude=EXAMPLE_LON,
    )

    print("=== 원 해저저질 ===")
    print(result["source_value"])

    print("\n=== 직접 파생값 ===")
    for item in result["derived_values"]:
        print(item["value"])
