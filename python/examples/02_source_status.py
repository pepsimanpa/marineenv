from _example_common import get_config_path
from marineenvironment import MarineEnvironment


config = get_config_path()

with MarineEnvironment() as env:
    env.initialize(config)

    print("=== GetSources() ===")
    for source in env.get_sources():
        print(source["id"], source["type"], source["status"])

    print("\n=== GetSourceStatus() ===")
    source = env.get_source_status("KHOA_DAILY_CURRENT")
    print(source)
