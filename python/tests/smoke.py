from marineenvironment import MarineEnvironment


def main() -> None:
    with MarineEnvironment() as env:
        sources = env.get_sources()
        assert sources == [], f"Expected no sources before initialization, got: {sources}"

    print("PASS: Python.NET loaded MarineEnvironment.dll and called GetSources().")


if __name__ == "__main__":
    main()
