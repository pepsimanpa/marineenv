from _example_common import REPO_ROOT, get_config_path
from marineenvironment import MarineEnvironment


config = get_config_path()

demo_option = {
    "id": "PYTHON_DEMO_DISABLED",
    "type": "Temperature",
    "format": "NetCdf",
    "enabled": False,
    "path": str(REPO_ROOT),
    "variable": "temperature",
    "unit": "degC",
    "metadata": {
        "purpose": "Python LoadSource/ReloadSource/UnloadSource example"
    },
}

with MarineEnvironment() as env:
    env.initialize(config)

    print("=== LoadSource ===")
    print(env.load_source(demo_option))

    print("\n=== GetSourceStatus ===")
    print(env.get_source_status("PYTHON_DEMO_DISABLED"))

    print("\n=== ReloadSource ===")
    demo_option["priority"] = 50
    print(env.reload_source(demo_option))

    print("\n=== UnloadSource ===")
    print("removed:", env.unload_source("PYTHON_DEMO_DISABLED"))
    print("after unload:", env.get_source_status("PYTHON_DEMO_DISABLED"))
