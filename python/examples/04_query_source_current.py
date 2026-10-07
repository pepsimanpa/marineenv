from datetime import datetime

from _example_common import EXAMPLE_LAT, EXAMPLE_LON, get_config_path
from marineenvironment import MarineEnvironment


config = get_config_path()

with MarineEnvironment() as env:
    env.initialize(config)

    result = env.query_source(
        "KHOA_DAILY_CURRENT",
        latitude=EXAMPLE_LAT,
        longitude=EXAMPLE_LON,
        when=datetime(2026, 9, 17),
    )

    current_item = result["source_value"]
    if current_item is None:
        print("해당 위치/계절에 KHOA 해류 데이터가 없습니다.")
    else:
        current = current_item["value"]

        print("source:", result["source_id"])
        print("U (eastward):", current["eastward_velocity"], "m/s")
        print("V (northward):", current["northward_velocity"], "m/s")
        print("speed:", current["speed"], "m/s")
        print("direction:", current["direction"], "deg")

        if "method" not in current:
            print(
                "\nWARNING: loaded MarineEnvironment.dll is older than this example.\n"
                "Rebuild the DLL with:\n"
                "  dotnet build src\\MarineEnvironment\\MarineEnvironment.csproj -c Release"
            )
        else:
            print("method:", current["method"])

            # KHOA는 FES 조화분조 합성이 아니므로 아래 값은 None이 정상이다.
            print("constituent_mode:", current["constituent_mode"])
            print("constituent_count:", current["constituent_count"])
