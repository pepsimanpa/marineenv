from __future__ import annotations

import re
from datetime import date, datetime
from pathlib import Path
from typing import Any

from ._runtime import load_types


def _snake(name: str) -> str:
    return re.sub(r"(?<!^)(?=[A-Z])", "_", name).lower()


def _dotnet_datetime(value: datetime | date | None) -> Any:
    if value is None:
        return None

    from System import DateTime, DateTimeKind

    if isinstance(value, datetime):
        return DateTime(
            value.year,
            value.month,
            value.day,
            value.hour,
            value.minute,
            value.second,
            value.microsecond // 1000,
            DateTimeKind.Unspecified,
        )

    return DateTime(value.year, value.month, value.day, 0, 0, 0, DateTimeKind.Unspecified)


def _parse_enum(enum_type: Any, value: Any) -> Any:
    if not isinstance(value, str):
        return value

    from System import Enum

    return Enum.Parse(enum_type, value, True)


def _coerce_property_value(prop: Any, value: Any) -> Any:
    """Convert Python primitives to the exact CLR type expected by PropertyInfo.SetValue."""
    if value is None:
        return None

    from System import Boolean, Double, Int16, Int32, Int64, Single, String
    from System import Nullable

    target_type = prop.PropertyType
    nullable_underlying = Nullable.GetUnderlyingType(target_type)
    if nullable_underlying is not None:
        target_type = nullable_underlying

    full_name = str(target_type.FullName or "")

    if full_name == "System.Boolean" and isinstance(value, bool):
        return Boolean(value)
    if full_name == "System.Int16" and isinstance(value, int) and not isinstance(value, bool):
        return Int16(value)
    if full_name == "System.Int32" and isinstance(value, int) and not isinstance(value, bool):
        return Int32(value)
    if full_name == "System.Int64" and isinstance(value, int) and not isinstance(value, bool):
        return Int64(value)
    if full_name == "System.Single" and isinstance(value, (int, float)) and not isinstance(value, bool):
        return Single(value)
    if full_name == "System.Double" and isinstance(value, (int, float)) and not isinstance(value, bool):
        return Double(value)
    if full_name == "System.String" and isinstance(value, str):
        return String(value)

    return value


def _set(obj: Any, **values: Any) -> Any:
    """Set public .NET properties, including C# init-only properties."""
    dotnet_type = obj.GetType()
    for name, value in values.items():
        if value is None:
            continue

        prop = dotnet_type.GetProperty(name)
        if prop is None:
            raise AttributeError(f"{dotnet_type.FullName} has no property {name}")

        prop.SetValue(obj, _coerce_property_value(prop, value))

    return obj


def _to_python(value: Any) -> Any:
    if value is None or isinstance(value, (str, bool, int, float)):
        return value

    from System import DateTime, Enum

    if isinstance(value, DateTime):
        return datetime(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            value.Minute,
            value.Second,
            value.Millisecond * 1000,
        )

    try:
        if value.GetType().IsEnum:
            return str(value)
    except Exception:
        pass

    # IReadOnlyDictionary / IDictionary-like objects.
    try:
        keys = list(value.Keys)
        return {str(key): _to_python(value[key]) for key in keys}
    except Exception:
        pass

    dotnet_type = None
    try:
        dotnet_type = value.GetType()
    except Exception:
        pass

    # CLR arrays must be converted before DTO reflection. Array types can report
    # the element namespace (MarineEnvironment.Models), and reflecting them as
    # DTOs walks properties such as SyncRoot recursively.
    if dotnet_type is not None and dotnet_type.IsArray:
        return [_to_python(item) for item in value]

    # Other .NET collection types (List<T>, IReadOnlyList<T>, etc.) should also
    # become normal Python lists before domain-object reflection.
    if not isinstance(value, (str, bytes, bytearray)):
        try:
            from System.Collections import IEnumerable

            if isinstance(value, IEnumerable):
                return [_to_python(item) for item in value]
        except Exception:
            pass

    # Convert MarineEnvironment DTOs/records recursively.
    if dotnet_type is not None:
        namespace = str(dotnet_type.Namespace or "")
        if namespace.startswith("MarineEnvironment"):
            result: dict[str, Any] = {}
            for prop in dotnet_type.GetProperties():
                if not prop.CanRead or prop.GetIndexParameters().Length != 0:
                    continue
                try:
                    result[_snake(prop.Name)] = _to_python(prop.GetValue(value))
                except Exception:
                    # A failing optional/computed property should not make the
                    # whole result unusable from Python.
                    continue
            return result

    # Python.NET primitive wrappers normally convert automatically. This is a
    # final fallback for uncommon CLR scalar values.
    try:
        if isinstance(value, Enum):
            return str(value)
    except Exception:
        pass

    return value


class MarineEnvironment:
    """Python-friendly facade over MarineEnvironmentManager."""

    def __init__(self, dll_path: str | Path | None = None) -> None:
        self._types = load_types(dll_path)
        self._manager = self._types["MarineEnvironmentManager"]()
        self._closed = False

    @property
    def raw_manager(self) -> Any:
        """Underlying .NET MarineEnvironmentManager for advanced integrations."""
        self._ensure_open()
        return self._manager

    def initialize(self, config_path: str | Path | None = None) -> dict[str, Any]:
        self._ensure_open()
        result = (
            self._manager.Initialize()
            if config_path is None
            else self._manager.Initialize(str(Path(config_path)))
        )
        return _to_python(result)

    def get_sources(self) -> list[dict[str, Any]]:
        self._ensure_open()
        return _to_python(self._manager.GetSources())

    def get_source_status(self, source_id: str) -> dict[str, Any] | None:
        self._ensure_open()
        return _to_python(self._manager.GetSourceStatus(source_id))

    def load_source(self, option: dict[str, Any]) -> dict[str, Any]:
        """Register or replace one data source from a Python dictionary."""
        self._ensure_open()
        return _to_python(self._manager.LoadSource(self._data_source_option(option)))

    def reload_source(self, option: dict[str, Any]) -> dict[str, Any]:
        """Reload one data source from a Python dictionary."""
        self._ensure_open()
        return _to_python(self._manager.ReloadSource(self._data_source_option(option)))

    def unload_source(self, source_id: str) -> bool:
        self._ensure_open()
        return bool(self._manager.UnloadSource(source_id))

    def query(
        self,
        latitude: float,
        longitude: float,
        depth: float | None = None,
        when: datetime | date | None = None,
    ) -> dict[str, Any]:
        self._ensure_open()
        query = self._environment_query(latitude, longitude, depth, when)
        return _to_python(self._manager.Query(query))

    def query_source(
        self,
        source_id: str,
        latitude: float,
        longitude: float,
        depth: float | None = None,
        when: datetime | date | None = None,
    ) -> dict[str, Any]:
        self._ensure_open()
        query = self._environment_query(latitude, longitude, depth, when)
        return _to_python(self._manager.QuerySource(source_id, query))

    def query_grid(
        self,
        source_id: str,
        *,
        min_latitude: float,
        max_latitude: float,
        min_longitude: float,
        max_longitude: float,
        depth: float | None = None,
        when: datetime | date | None = None,
        width: int = 480,
        height: int = 300,
        resolution_mode: str = "Custom",
    ) -> dict[str, Any]:
        self._ensure_open()

        query = self._types["GridQuery"]()
        values: dict[str, Any] = {
            "MinLatitude": float(min_latitude),
            "MaxLatitude": float(max_latitude),
            "MinLongitude": float(min_longitude),
            "MaxLongitude": float(max_longitude),
            "Width": int(width),
            "Height": int(height),
            "ResolutionMode": _parse_enum(
                self._types["GridResolutionMode"], resolution_mode
            ),
        }
        if depth is not None:
            values["Depth"] = float(depth)
        if when is not None:
            values["DateTime"] = _dotnet_datetime(when)

        _set(query, **values)
        return _to_python(self._manager.QueryGrid(source_id, query))

    def query_seabed_grade_grid(
        self,
        *,
        min_latitude: float,
        max_latitude: float,
        min_longitude: float,
        max_longitude: float,
        grid_mode: str = "CellCount",
        columns: int = 20,
        rows: int = 10,
        cell_size_kilometers: float = 5.0,
        contact_density: int = 1,
        terrain: str = "Flat",
        when: datetime | date | None = None,
    ) -> dict[str, Any]:
        self._ensure_open()

        query = self._types["SeabedGradeGridQuery"]()
        values: dict[str, Any] = {
            "MinLatitude": float(min_latitude),
            "MaxLatitude": float(max_latitude),
            "MinLongitude": float(min_longitude),
            "MaxLongitude": float(max_longitude),
            "GridMode": _parse_enum(
                self._types["SeabedGradeGridMode"], grid_mode
            ),
            "Columns": int(columns),
            "Rows": int(rows),
            "CellSizeKilometers": float(cell_size_kilometers),
            "ContactDensity": int(contact_density),
            "Terrain": _parse_enum(self._types["SeabedTerrain"], terrain),
        }
        if when is not None:
            values["DateTime"] = _dotnet_datetime(when)

        _set(query, **values)
        return _to_python(self._manager.QuerySeabedGradeGrid(query))

    def close(self) -> None:
        if not self._closed:
            self._manager.Dispose()
            self._closed = True

    def __enter__(self) -> "MarineEnvironment":
        self._ensure_open()
        return self

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> None:
        self.close()

    def _ensure_open(self) -> None:
        if self._closed:
            raise RuntimeError("MarineEnvironment client is already closed.")

    def _data_source_option(self, option: dict[str, Any]) -> Any:
        if not isinstance(option, dict):
            raise TypeError("option must be a Python dict")

        aliases = {
            "id": "Id",
            "type": "Type",
            "format": "Format",
            "enabled": "Enabled",
            "path": "Path",
            "file_pattern": "FilePattern",
            "variable": "Variable",
            "latitude_variable": "LatitudeVariable",
            "longitude_variable": "LongitudeVariable",
            "depth_variable": "DepthVariable",
            "time_variable": "TimeVariable",
            "unit": "Unit",
            "vertical_convention": "VerticalConvention",
            "vertical_reference": "VerticalReference",
            "priority": "Priority",
            "max_nearest_distance_km": "MaxNearestDistanceKm",
            "max_temporal_offset_days": "MaxTemporalOffsetDays",
            "attribute_field": "AttributeField",
            "seabed_mapping_path": "SeabedMappingPath",
            "current_constituent_mode": "CurrentConstituentMode",
            "dimension_map": "DimensionMap",
            "metadata": "Metadata",
        }

        values: dict[str, Any] = {}
        for key, value in option.items():
            values[aliases.get(key, key)] = value

        for property_name, enum_name in (
            ("Type", "EnvironmentType"),
            ("Format", "DataSourceFormat"),
            ("VerticalConvention", "VerticalConvention"),
            ("CurrentConstituentMode", "CurrentConstituentMode"),
        ):
            if property_name in values:
                values[property_name] = _parse_enum(
                    self._types[enum_name], values[property_name]
                )

        from System import String
        from System.Collections.Generic import Dictionary

        for property_name in ("DimensionMap", "Metadata"):
            if property_name in values and values[property_name] is not None:
                mapping = Dictionary[String, String]()
                for key, value in values[property_name].items():
                    mapping[str(key)] = str(value)
                values[property_name] = mapping

        data_source = self._types["DataSourceOption"]()
        return _set(data_source, **values)

    def _environment_query(
        self,
        latitude: float,
        longitude: float,
        depth: float | None,
        when: datetime | date | None,
    ) -> Any:
        query = self._types["EnvironmentQuery"]()
        values: dict[str, Any] = {
            "Latitude": float(latitude),
            "Longitude": float(longitude),
        }
        if depth is not None:
            values["Depth"] = float(depth)
        if when is not None:
            values["DateTime"] = _dotnet_datetime(when)
        return _set(query, **values)
