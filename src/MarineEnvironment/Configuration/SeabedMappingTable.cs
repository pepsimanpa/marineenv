using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MarineEnvironment.Configuration
{
    public sealed class SeabedMappingTable
    {
        public string Id { get; init; } = string.Empty;
        public string? Description { get; init; }
        public List<SeabedMappingRule> Rules { get; init; } = new List<SeabedMappingRule>();
    }

    public sealed class SeabedMappingRule
    {
        public List<string> ShomCodes { get; init; } = new List<string>();
        public string ShomOriginalClassification { get; init; } = string.Empty;
        public string PrimaryClassification { get; init; } = string.Empty;
        public string Seabed { get; init; } = string.Empty;
        public double? MudPercent { get; init; }
        public double? SandPercent { get; init; }
        public double BurialRatePercent { get; init; }
    }

    internal sealed class SeabedMappingLookup
    {
        private readonly Dictionary<string, SeabedMappingRule> _byCode;

        public SeabedMappingLookup(SeabedMappingTable table)
        {
            Table = table;
            _byCode = new Dictionary<string, SeabedMappingRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in table.Rules)
            {
                foreach (var code in rule.ShomCodes)
                    _byCode[code.Trim()] = rule;
            }
        }

        public SeabedMappingTable Table { get; }

        public bool TryGet(string code, out SeabedMappingRule rule)
            => _byCode.TryGetValue(code, out rule!);
    }

    internal static class SeabedMappingTableLoader
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static SeabedMappingLookup Load(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Seabed mapping table was not found.", fullPath);

            var json = File.ReadAllText(fullPath);
            var table = JsonSerializer.Deserialize<SeabedMappingTable>(json, JsonOptions)
                ?? throw new InvalidDataException("Seabed mapping table is empty or invalid.");

            Validate(table, fullPath);
            return new SeabedMappingLookup(table);
        }

        private static void Validate(SeabedMappingTable table, string path)
        {
            if (string.IsNullOrWhiteSpace(table.Id))
                throw new InvalidDataException($"Seabed mapping table '{path}' requires an id.");
            if (table.Rules == null || table.Rules.Count == 0)
                throw new InvalidDataException($"Seabed mapping table '{table.Id}' contains no rules.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < table.Rules.Count; i++)
            {
                var rule = table.Rules[i];
                if (rule.ShomCodes == null || rule.ShomCodes.Count == 0)
                    throw new InvalidDataException($"Seabed mapping table '{table.Id}' rule {i + 1} contains no SHOM codes.");
                if (string.IsNullOrWhiteSpace(rule.ShomOriginalClassification))
                    throw new InvalidDataException($"Seabed mapping table '{table.Id}' rule {i + 1} requires shomOriginalClassification.");
                if (string.IsNullOrWhiteSpace(rule.PrimaryClassification))
                    throw new InvalidDataException($"Seabed mapping table '{table.Id}' rule {i + 1} requires primaryClassification.");
                if (string.IsNullOrWhiteSpace(rule.Seabed))
                    throw new InvalidDataException($"Seabed mapping table '{table.Id}' rule {i + 1} requires seabed.");
                if (rule.BurialRatePercent < 0 || rule.BurialRatePercent > 100)
                    throw new InvalidDataException($"Seabed mapping table '{table.Id}' rule {i + 1} burialRatePercent must be between 0 and 100.");

                if (rule.MudPercent.HasValue != rule.SandPercent.HasValue)
                    throw new InvalidDataException($"Seabed mapping table '{table.Id}' rule {i + 1} must specify both mudPercent and sandPercent, or neither.");
                if (rule.MudPercent.HasValue)
                {
                    if (rule.MudPercent.Value < 0 || rule.MudPercent.Value > 100 ||
                        rule.SandPercent!.Value < 0 || rule.SandPercent.Value > 100)
                        throw new InvalidDataException($"Seabed mapping table '{table.Id}' rule {i + 1} mud/sand percentages must be between 0 and 100.");
                    if (Math.Abs((rule.MudPercent.Value + rule.SandPercent.Value) - 100.0) > 1e-6)
                        throw new InvalidDataException($"Seabed mapping table '{table.Id}' rule {i + 1} mudPercent + sandPercent must equal 100.");
                }

                foreach (var rawCode in rule.ShomCodes)
                {
                    var code = rawCode?.Trim();
                    if (string.IsNullOrWhiteSpace(code))
                        throw new InvalidDataException($"Seabed mapping table '{table.Id}' rule {i + 1} contains an empty SHOM code.");
                    if (!seen.Add(code))
                        throw new InvalidDataException($"Seabed mapping table '{table.Id}' contains duplicate SHOM code '{code}'.");
                }
            }
        }
    }
}
