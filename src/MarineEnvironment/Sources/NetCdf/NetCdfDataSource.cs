using MarineEnvironment.Configuration;
using MarineEnvironment.Models;
using MarineEnvironment.Native;

namespace MarineEnvironment.Sources.NetCdf;

internal sealed class NetCdfDataSource : IEnvironmentDataSource
{
    private readonly DataSourceOption _option;
    private readonly string _resolvedPath;

    public NetCdfDataSource(DataSourceOption option, string resolvedPath)
    {
        _option = option;
        _resolvedPath = resolvedPath;

        if (!option.Enabled)
        {
            Status = SourceStatus.Disabled;
            return;
        }

        try
        {
            var validationFile = ResolveFile(DateTime.Now);
            if (!File.Exists(validationFile))
            {
                Status = SourceStatus.FileNotFound;
                StatusMessage = validationFile;
                return;
            }

            using var file = Open(validationFile);
            ValidateVariable(file.Id, _option.Variable);
            ValidateVariable(file.Id, _option.LatitudeVariable);
            ValidateVariable(file.Id, _option.LongitudeVariable);
            if (!string.IsNullOrWhiteSpace(_option.DepthVariable))
                ValidateVariable(file.Id, _option.DepthVariable!);

            Status = SourceStatus.Ready;
        }
        catch (DllNotFoundException ex)
        {
            Status = SourceStatus.NativeLibraryUnavailable;
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Status = SourceStatus.Error;
            StatusMessage = ex.Message;
        }
    }

    public string Id => _option.Id;
    public EnvironmentType Type => _option.Type;
    public SourceStatus Status { get; private set; } = SourceStatus.NotInitialized;
    public string? StatusMessage { get; private set; }

    public EnvironmentValue? Query(EnvironmentQuery query)
    {
        if (Status != SourceStatus.Ready)
            return null;

        var filePath = ResolveFile(query.DateTime ?? DateTime.Now);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"NetCDF source file for '{Id}' was not found.", filePath);

        using var file = Open(filePath);
        var ncid = file.Id;
        var context = BuildReadContext(ncid, query.Depth);

        var normalizedLon = NormalizeLongitude(query.Longitude, context.LongitudeAxis);
        var latIndex = FindNearestIndex(context.LatitudeAxis.Values, query.Latitude);
        var lonIndex = FindNearestIndex(context.LongitudeAxis.Values, normalizedLon);
        var depthIndex = context.DepthAxis is null
            ? null
            : FindNearestIndex(context.DepthAxis.Value.Values, query.Depth ?? context.DepthAxis.Value.Values[0]);

        var value = ReadValue(ncid, context, latIndex, lonIndex, depthIndex);
        if (value is null)
            return null;

        var metadata = _option.Metadata?.ToDictionary(x => x.Key, x => (object?)x.Value)
            ?? new Dictionary<string, object?>();
        metadata["file"] = filePath;
        metadata["sampling"] = query.Sampling.ToString();

        return new EnvironmentValue(
            Id,
            Type,
            value,
            _option.Unit,
            context.LatitudeAxis.Values[latIndex],
            context.LongitudeAxis.Values[lonIndex],
            context.DepthAxis is null || depthIndex is null ? null : context.DepthAxis.Value.Values[depthIndex.Value],
            query.DateTime,
            _option.Variable,
            metadata);
    }

    public GridResult QueryGrid(GridQuery query)
    {
        if (Status != SourceStatus.Ready)
            throw new InvalidOperationException($"Source '{Id}' is not ready: {Status} - {StatusMessage}");
        if (query.Width is < 2 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(query.Width), "Grid width must be between 2 and 2048.");
        if (query.Height is < 2 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(query.Height), "Grid height must be between 2 and 2048.");
        if (query.MinLatitude >= query.MaxLatitude)
            throw new ArgumentException("MinLatitude must be less than MaxLatitude.", nameof(query));
        if (query.MinLongitude >= query.MaxLongitude)
            throw new ArgumentException("MinLongitude must be less than MaxLongitude.", nameof(query));

        var filePath = ResolveFile(query.DateTime ?? DateTime.Now);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"NetCDF source file for '{Id}' was not found.", filePath);

        using var file = Open(filePath);
        var ncid = file.Id;
        var context = BuildReadContext(ncid, query.Depth);
        var depthIndex = context.DepthAxis is null
            ? null
            : FindNearestIndex(context.DepthAxis.Value.Values, query.Depth ?? context.DepthAxis.Value.Values[0]);

        var outputLatitudes = new double[query.Height];
        var outputLongitudes = new double[query.Width];
        var latIndices = new int[query.Height];
        var lonIndices = new int[query.Width];

        // Viewer rows run north to south, so the first row is MaxLatitude.
        for (var row = 0; row < query.Height; row++)
        {
            var t = row / (double)(query.Height - 1);
            var requested = query.MaxLatitude + ((query.MinLatitude - query.MaxLatitude) * t);
            latIndices[row] = FindNearestIndex(context.LatitudeAxis.Values, requested);
            outputLatitudes[row] = context.LatitudeAxis.Values[latIndices[row]];
        }

        for (var column = 0; column < query.Width; column++)
        {
            var t = column / (double)(query.Width - 1);
            var requested = query.MinLongitude + ((query.MaxLongitude - query.MinLongitude) * t);
            var normalized = NormalizeLongitude(requested, context.LongitudeAxis);
            lonIndices[column] = FindNearestIndex(context.LongitudeAxis.Values, normalized);
            outputLongitudes[column] = context.LongitudeAxis.Values[lonIndices[column]];
        }

        var values = new double?[query.Width * query.Height];
        double? minimum = null;
        double? maximum = null;

        for (var row = 0; row < query.Height; row++)
        {
            for (var column = 0; column < query.Width; column++)
            {
                var value = ReadValue(ncid, context, latIndices[row], lonIndices[column], depthIndex);
                values[(row * query.Width) + column] = value;
                if (value is null)
                    continue;

                minimum = minimum is null ? value : Math.Min(minimum.Value, value.Value);
                maximum = maximum is null ? value : Math.Max(maximum.Value, value.Value);
            }
        }

        var metadata = _option.Metadata?.ToDictionary(x => x.Key, x => (object?)x.Value)
            ?? new Dictionary<string, object?>();
        metadata["file"] = filePath;
        metadata["sampling"] = query.Sampling.ToString();
        metadata["requestedBounds"] = new[]
        {
            query.MinLatitude, query.MaxLatitude, query.MinLongitude, query.MaxLongitude
        };

        return new GridResult
        {
            SourceId = Id,
            Type = Type,
            Width = query.Width,
            Height = query.Height,
            Latitudes = outputLatitudes,
            Longitudes = outputLongitudes,
            Values = values,
            Unit = _option.Unit,
            Depth = context.DepthAxis is null || depthIndex is null ? null : context.DepthAxis.Value.Values[depthIndex.Value],
            DateTime = query.DateTime,
            Variable = _option.Variable,
            Minimum = minimum,
            Maximum = maximum,
            Metadata = metadata
        };
    }

    private ReadContext BuildReadContext(int ncid, double? requestedDepth)
    {
        var latAxis = ReadAxis(ncid, _option.LatitudeVariable);
        var lonAxis = ReadAxis(ncid, _option.LongitudeVariable);

        Axis? depthAxis = null;
        if (!string.IsNullOrWhiteSpace(_option.DepthVariable))
            depthAxis = ReadAxis(ncid, _option.DepthVariable!);

        NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varid(ncid, _option.Variable, out var dataVarId), $"Find variable '{_option.Variable}'");
        NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varndims(ncid, dataVarId, out var ndims), $"Read dimensions for '{_option.Variable}'");
        var dimIds = new int[ndims];
        NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_vardimid(ncid, dataVarId, dimIds), $"Read dimension ids for '{_option.Variable}'");

        return new ReadContext(dataVarId, dimIds, latAxis, lonAxis, depthAxis);
    }

    private static double? ReadValue(int ncid, ReadContext context, int latIndex, int lonIndex, int? depthIndex)
    {
        var indices = new UIntPtr[context.DimensionIds.Length];
        SetAxisIndex(context.DimensionIds, indices, context.LatitudeAxis.DimensionId, latIndex);
        SetAxisIndex(context.DimensionIds, indices, context.LongitudeAxis.DimensionId, lonIndex);
        if (context.DepthAxis is not null && depthIndex is not null)
            SetAxisIndex(context.DimensionIds, indices, context.DepthAxis.Value.DimensionId, depthIndex.Value);

        // Unsupported singleton dimensions (for example WOA monthly time=1) default to zero.
        NetCdfNative.ThrowIfError(NetCdfNative.nc_get_var1_double(ncid, context.DataVariableId, indices, out var raw), "Read NetCDF value");

        if (TryGetAttribute(ncid, context.DataVariableId, "_FillValue", out var fill) && NearlyEqual(raw, fill))
            return null;
        if (TryGetAttribute(ncid, context.DataVariableId, "missing_value", out var missing) && NearlyEqual(raw, missing))
            return null;

        var value = raw;
        if (TryGetAttribute(ncid, context.DataVariableId, "scale_factor", out var scale))
            value *= scale;
        if (TryGetAttribute(ncid, context.DataVariableId, "add_offset", out var offset))
            value += offset;
        return value;
    }

    private string ResolveFile(DateTime time)
    {
        if (string.IsNullOrWhiteSpace(_option.FilePattern))
            return _resolvedPath;

        var fileName = _option.FilePattern.Replace("{MM}", time.Month.ToString("00"), StringComparison.Ordinal);
        return Path.Combine(_resolvedPath, fileName);
    }

    private static Axis ReadAxis(int ncid, string variableName)
    {
        NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varid(ncid, variableName, out var varId), $"Find axis '{variableName}'");
        NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varndims(ncid, varId, out var ndims), $"Read axis dimensions '{variableName}'");
        if (ndims != 1)
            throw new InvalidDataException($"Axis '{variableName}' must be one-dimensional in the generic NetCDF reader.");

        var dimIds = new int[1];
        NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_vardimid(ncid, varId, dimIds), $"Read axis dimension '{variableName}'");
        NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_dimlen(ncid, dimIds[0], out var length), $"Read axis length '{variableName}'");

        var count = checked((int)length.ToUInt64());
        var values = new double[count];
        NetCdfNative.ThrowIfError(NetCdfNative.nc_get_var_double(ncid, varId, values), $"Read axis '{variableName}'");
        return new Axis(dimIds[0], values);
    }

    private static int FindNearestIndex(double[] values, double target)
    {
        if (values.Length == 0)
            throw new InvalidDataException("Coordinate axis is empty.");
        if (values.Length == 1)
            return 0;

        var ascending = values[^1] >= values[0];
        var lo = 0;
        var hi = values.Length - 1;

        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var current = values[mid];
            if (current == target)
                return mid;

            if ((ascending && current < target) || (!ascending && current > target))
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        if (lo <= 0) return 0;
        if (lo >= values.Length) return values.Length - 1;
        return Math.Abs(values[lo] - target) < Math.Abs(values[lo - 1] - target) ? lo : lo - 1;
    }

    private static double NormalizeLongitude(double longitude, Axis axis)
    {
        var min = axis.Values.Min();
        var max = axis.Values.Max();
        if (min >= 0 && max > 180 && longitude < 0)
            return longitude + 360;
        if (min < 0 && max <= 180 && longitude > 180)
            return longitude - 360;
        return longitude;
    }

    private static void SetAxisIndex(int[] variableDimensionIds, UIntPtr[] indices, int axisDimensionId, int axisIndex)
    {
        var position = Array.IndexOf(variableDimensionIds, axisDimensionId);
        if (position >= 0)
            indices[position] = (UIntPtr)(uint)axisIndex;
    }

    private static bool TryGetAttribute(int ncid, int varId, string name, out double value)
        => NetCdfNative.nc_get_att_double(ncid, varId, name, out value) == NetCdfNative.NoError;

    private static bool NearlyEqual(double a, double b)
        => a.Equals(b) || Math.Abs(a - b) <= Math.Max(1e-12, Math.Abs(b) * 1e-12);

    private static void ValidateVariable(int ncid, string variable)
        => NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varid(ncid, variable, out _), $"Find variable '{variable}'");

    private static NetCdfFile Open(string path)
    {
        NetCdfNative.ThrowIfError(NetCdfNative.nc_open(path, NetCdfNative.Nowrite, out var ncid), $"Open NetCDF '{path}'");
        return new NetCdfFile(ncid);
    }

    public void Dispose() { }

    private readonly record struct Axis(int DimensionId, double[] Values);
    private readonly record struct ReadContext(
        int DataVariableId,
        int[] DimensionIds,
        Axis LatitudeAxis,
        Axis LongitudeAxis,
        Axis? DepthAxis);

    private sealed class NetCdfFile : IDisposable
    {
        public NetCdfFile(int id) => Id = id;
        public int Id { get; }
        public void Dispose() => NetCdfNative.nc_close(Id);
    }
}
