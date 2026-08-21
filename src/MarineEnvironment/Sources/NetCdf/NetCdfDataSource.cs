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

        var latAxis = ReadAxis(ncid, _option.LatitudeVariable);
        var lonAxis = ReadAxis(ncid, _option.LongitudeVariable);
        var normalizedLon = NormalizeLongitude(query.Longitude, lonAxis);

        var latIndex = FindNearestIndex(latAxis.Values, query.Latitude);
        var lonIndex = FindNearestIndex(lonAxis.Values, normalizedLon);

        Axis? depthAxis = null;
        int? depthIndex = null;
        if (!string.IsNullOrWhiteSpace(_option.DepthVariable))
        {
            depthAxis = ReadAxis(ncid, _option.DepthVariable!);
            depthIndex = FindNearestIndex(depthAxis.Value.Values, query.Depth ?? depthAxis.Value.Values[0]);
        }

        NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varid(ncid, _option.Variable, out var dataVarId), $"Find variable '{_option.Variable}'");
        NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varndims(ncid, dataVarId, out var ndims), $"Read dimensions for '{_option.Variable}'");
        var dimIds = new int[ndims];
        NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_vardimid(ncid, dataVarId, dimIds), $"Read dimension ids for '{_option.Variable}'");

        var indices = new UIntPtr[ndims];
        SetAxisIndex(dimIds, indices, latAxis.DimensionId, latIndex);
        SetAxisIndex(dimIds, indices, lonAxis.DimensionId, lonIndex);
        if (depthAxis is not null && depthIndex is not null)
            SetAxisIndex(dimIds, indices, depthAxis.Value.DimensionId, depthIndex.Value);

        // Any unsupported singleton dimension defaults to index 0. This is suitable for
        // WOA monthly files; FES harmonic synthesis receives a dedicated reader later.
        NetCdfNative.ThrowIfError(NetCdfNative.nc_get_var1_double(ncid, dataVarId, indices, out var raw), $"Read '{_option.Variable}' value");

        if (TryGetAttribute(ncid, dataVarId, "_FillValue", out var fill) && NearlyEqual(raw, fill))
            return null;
        if (TryGetAttribute(ncid, dataVarId, "missing_value", out var missing) && NearlyEqual(raw, missing))
            return null;

        var value = raw;
        if (TryGetAttribute(ncid, dataVarId, "scale_factor", out var scale))
            value *= scale;
        if (TryGetAttribute(ncid, dataVarId, "add_offset", out var offset))
            value += offset;

        var metadata = _option.Metadata?.ToDictionary(x => x.Key, x => (object?)x.Value)
            ?? new Dictionary<string, object?>();
        metadata["file"] = filePath;
        metadata["sampling"] = query.Sampling.ToString();

        return new EnvironmentValue(
            Id,
            Type,
            value,
            _option.Unit,
            latAxis.Values[latIndex],
            lonAxis.Values[lonIndex],
            depthAxis is null || depthIndex is null ? null : depthAxis.Value.Values[depthIndex.Value],
            query.DateTime,
            _option.Variable,
            metadata);
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

    private sealed class NetCdfFile : IDisposable
    {
        public NetCdfFile(int id) => Id = id;
        public int Id { get; }
        public void Dispose() => NetCdfNative.nc_close(Id);
    }
}
