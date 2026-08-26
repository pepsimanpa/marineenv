using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarineEnvironment.Configuration
{
    internal static class ConfigurationLoader
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static MarineEnvironmentOptions Load(string configPath)
        {
            if (string.IsNullOrWhiteSpace(configPath))
                throw new ArgumentException("Configuration path is required.", nameof(configPath));

            var fullPath = Path.GetFullPath(configPath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("MarineEnvironment configuration file was not found.", fullPath);

            var json = File.ReadAllText(fullPath);
            var options = JsonSerializer.Deserialize<MarineEnvironmentOptions>(json, JsonOptions)
                ?? throw new InvalidDataException("MarineEnvironment configuration is empty or invalid.");

            var baseDirectory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
            foreach (var source in options.Sources)
                ResolvePath(source, baseDirectory);
            return options;
        }

        private static void ResolvePath(DataSourceOption source, string baseDirectory)
        {
            if (Path.IsPathRooted(source.Path))
                return;

            _ = Path.GetFullPath(Path.Combine(baseDirectory, source.Path));
        }

        public static string ResolveSourcePath(string sourcePath, string configPath)
        {
            if (Path.IsPathRooted(sourcePath))
                return Path.GetFullPath(sourcePath);

            var fullConfigPath = Path.GetFullPath(configPath);
            var baseDirectory = Path.GetDirectoryName(fullConfigPath) ?? Environment.CurrentDirectory;
            return Path.GetFullPath(Path.Combine(baseDirectory, sourcePath));
        }
    }
}
