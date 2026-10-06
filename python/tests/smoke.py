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

    print(
        "PASS: Python.NET loaded MarineEnvironment.dll, built an EnvironmentQuery, "
        "called Query(), and converted the result to Python values."
    )


if __name__ == "__main__":
    main()
