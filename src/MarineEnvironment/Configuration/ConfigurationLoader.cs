using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarineEnvironment.Configuration;

internal static class ConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static MarineEnvironmentOptions Load(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        var fullPath = System.IO.Path.GetFullPath(configPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("MarineEnvironment configuration file was not found.", fullPath);

        var json = File.ReadAllText(fullPath);
        var options = JsonSerializer.Deserialize<MarineEnvironmentOptions>(json, JsonOptions)
            ?? throw new InvalidDataException("MarineEnvironment configuration is empty or invalid.");

        var baseDirectory = System.IO.Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
        options.Sources.ForEach(source => ResolvePath(source, baseDirectory));
        return options;
    }

    private static void ResolvePath(DataSourceOption source, string baseDirectory)
    {
        if (System.IO.Path.IsPathRooted(source.Path))
            return;

        // DataSourceOption is init-only by design, so resolution is handled by the manager
        // when a source is registered. This method intentionally validates only the base path.
        _ = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, source.Path));
    }

    public static string ResolveSourcePath(string sourcePath, string configPath)
    {
        if (System.IO.Path.IsPathRooted(sourcePath))
            return System.IO.Path.GetFullPath(sourcePath);

        var fullConfigPath = System.IO.Path.GetFullPath(configPath);
        var baseDirectory = System.IO.Path.GetDirectoryName(fullConfigPath) ?? Environment.CurrentDirectory;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, sourcePath));
    }
}
