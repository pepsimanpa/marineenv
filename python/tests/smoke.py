from marineenvironment import MarineEnvironment


def main() -> None:
    with MarineEnvironment() as env:
        sources = env.get_sources()
        assert sources == [], f"Expected no sources before initialization, got: {sources}"

        result = env.query(latitude=35.1, longitude=129.05, depth=50.0)
        assert abs(result["requested_latitude"] - 35.1) < 1e-9
        assert abs(result["requested_longitude"] - 129.05) < 1e-9
        assert abs(result["requested_depth"] - 50.0) < 1e-9
        assert result["source_values"] == []
        assert result["derived_values"] == []

        demo = {
            "id": "PYTHON_SMOKE_DISABLED",
            "type": "Temperature",
            "format": "NetCdf",
            "enabled": False,
            "path": ".",
            "variable": "temperature",
        }

        loaded = env.load_source(demo)
        assert loaded["id"] == "PYTHON_SMOKE_DISABLED"
        assert loaded["status"] == "Disabled"

        reloaded = env.reload_source(demo)
        assert reloaded["status"] == "Disabled"

        assert env.unload_source("PYTHON_SMOKE_DISABLED") is True
        assert env.get_source_status("PYTHON_SMOKE_DISABLED") is None

    print(
        "PASS: Python.NET loaded MarineEnvironment.dll, Query() converted results, "
        "and LoadSource/ReloadSource/UnloadSource worked through the Python wrapper."
    )


if __name__ == "__main__":
    main()
