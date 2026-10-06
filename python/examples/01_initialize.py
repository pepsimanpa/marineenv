from _example_common import get_config_path
from marineenvironment import MarineEnvironment


config = get_config_path()

with MarineEnvironment() as env:
    result = env.initialize(config)

    print("초기화 성공:", result["success"])
    print("\n데이터소스 상태:")
    for source in result["sources"]:
        print(
            f'{source["id"]:24} '
            f'{source["type"]:12} '
            f'{source["status"]:10} '
            f'{source["message"] or ""}'
        )
