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

        # Exercise reflection-based CLR property setters with Python int/float values.
        # There are intentionally no sources loaded; reaching the manager-level
        # "source not found" / "no seabed source" error proves query DTO construction
        # and primitive conversion succeeded first.
        try:
            env.query_grid(
                "MISSING",
                min_latitude=35.0,
                max_latitude=35.2,
                min_longitude=128.6,
                max_longitude=128.8,
                width=40,
                height=30,
            )
        except Exception as exc:
            assert "MISSING" in str(exc)
        else:
            raise AssertionError("Expected missing-source QueryGrid failure")

        try:
            env.query_seabed_grade_grid(
                min_latitude=35.0,
                max_latitude=35.2,
                min_longitude=128.6,
                max_longitude=128.8,
                grid_mode="CellSizeKilometers",
                cell_size_kilometers=2.0,
                contact_density=1,
                terrain="Flat",
            )
        except Exception as exc:
            assert "No READY seabed source" in str(exc)
        else:
            raise AssertionError("Expected no-seabed-source grade-grid failure")

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
