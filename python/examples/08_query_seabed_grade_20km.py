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

    print("model:", result["model_id"])
    print("area grid:", result["columns"], "x", result["rows"])
    print("grade cells:", result["grade_count"])
    print("NoData cells:", result["no_data_count"])

    print("\n처음 20개 셀:")
    for cell in result["cells"][:20]:
        print(
            f'[{cell["row"]},{cell["column"]}] '
            f'grade={cell["grade"]} '
            f'source={cell["source_id"]} '
            f'seabed={cell["seabed"]} '
            f'mud={cell["mud_percent"]} '
            f'sand={cell["sand_percent"]} '
            f'burial={cell["burial_rate_percent"]}'
        )
