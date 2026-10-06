from _example_common import JINHAE_20KM, get_config_path
from marineenvironment import MarineEnvironment


config = get_config_path()

with MarineEnvironment() as env:
    env.initialize(config)

    result = env.query_seabed_grade_grid(
        **JINHAE_20KM,
        grid_mode="CellSizeKilometers",
        cell_size_kilometers=2.0,
        contact_density=1,
        terrain="Flat",
    )

    rows = result["rows"]
    columns = result["columns"]

    print("model: SEABED_GRADE_V1")
    print("area grid:", columns, "x", rows)
    print("grade cells:", result["grade_count"])
    print("NoData cells:", result["no_data_count"])
    print(
        "bounds:",
        f'{result["min_latitude"]:.5f}~{result["max_latitude"]:.5f} N,',
        f'{result["min_longitude"]:.5f}~{result["max_longitude"]:.5f} E',
    )

    # Row 0 is the northernmost row in QuerySeabedGradeGrid.
    # "--" means that no seabed grade could be calculated for that cell.
    grade_grid = [["--" for _ in range(columns)] for _ in range(rows)]

    for cell in result["cells"]:
        if cell["grade"]:
            grade_grid[cell["row"]][cell["column"]] = cell["grade"]

    print("\n=== Seabed Grade Grid ===")
    print(f'North ({result["max_latitude"]:.5f} N)')
    print("      " + " ".join(f"{column:>3}" for column in range(columns)))
    print("    +" + "----" * columns)

    for row in range(rows):
        print(f"{row:>3} | " + " ".join(f"{grade:>3}" for grade in grade_grid[row]))

    print(f'South ({result["min_latitude"]:.5f} N)')
    print(
        f'West {result["min_longitude"]:.5f} E'
        + " " * max(2, columns * 4 - 22)
        + f'East {result["max_longitude"]:.5f} E'
    )
    print("Legend: -- = NoData")

    print("\n=== First 20 cell details ===")
    for cell in result["cells"][:20]:
        if cell["grade"]:
            print(
                f'[{cell["row"]},{cell["column"]}] '
                f'grade={cell["grade"]} '
                f'source={cell["source_id"]} '
                f'seabed={cell["seabed"]} '
                f'mud={cell["mud_percent"]} '
                f'sand={cell["sand_percent"]} '
                f'burial={cell["burial_rate_percent"]}'
            )
        else:
            print(
                f'[{cell["row"]},{cell["column"]}] '
                f'grade=-- '
                f'reason={cell["no_data_reason"]}'
            )
