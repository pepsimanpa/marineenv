using System;
using System.Collections.Generic;
using System.Globalization;

namespace MarineEnvironment.Models
{
    /// <summary>
    /// Raw domestic 'deposit' polygon classification. This type is intentionally
    /// separate from the SHOM SeabedValue / ShomSedimentCatalog contract.
    /// </summary>
    public sealed class KoreaSedimentValue
    {
        public string Code { get; init; } = string.Empty;
        public string OriginalClassification { get; init; } = string.Empty;
        public override string ToString() => Code + " / " + OriginalClassification;
    }

    /// <summary>
    /// Project-defined operational interpretation of one Korean deposit code.
    /// Mud/sand fractions and burial rate are representative mapping values,
    /// NOT measured sediment percentages or measured burial observations.
    /// </summary>
    public sealed class KoreaSedimentDerivedValue
    {
        public string MappingTableId { get; init; } = KoreaSedimentCatalog.MappingTableId;
        public string OriginalCode { get; init; } = string.Empty;
        public string OriginalClassification { get; init; } = string.Empty;
        public string PrimaryClassification { get; init; } = string.Empty;
        public string Seabed { get; init; } = string.Empty;
        public double? MudPercent { get; init; }
        public double? SandPercent { get; init; }
        public double BurialRatePercent { get; init; }

        public string SeabedDisplay => MudPercent.HasValue && SandPercent.HasValue
            ? string.Format(CultureInfo.InvariantCulture,
                "{0} (뻘 {1:0.#}% / 모래 {2:0.#}%)", Seabed, MudPercent.Value, SandPercent.Value)
            : Seabed;
    }

    public sealed class KoreaSedimentDefinition
    {
        public KoreaSedimentDefinition(int index, string code, string originalClassification,
            string primaryClassification, string seabed, double? mudPercent,
            double? sandPercent, double burialRatePercent)
        {
            Index = index;
            Code = code;
            OriginalClassification = originalClassification;
            PrimaryClassification = primaryClassification;
            Seabed = seabed;
            MudPercent = mudPercent;
            SandPercent = sandPercent;
            BurialRatePercent = burialRatePercent;
        }

        public int Index { get; }
        public string Code { get; }
        public string OriginalClassification { get; }
        public string PrimaryClassification { get; }
        public string Seabed { get; }
        public double? MudPercent { get; }
        public double? SandPercent { get; }
        public double BurialRatePercent { get; }

        public KoreaSedimentDerivedValue ToDerived() => new KoreaSedimentDerivedValue
        {
            OriginalCode = Code,
            OriginalClassification = OriginalClassification,
            PrimaryClassification = PrimaryClassification,
            Seabed = Seabed,
            MudPercent = MudPercent,
            SandPercent = SandPercent,
            BurialRatePercent = BurialRatePercent
        };
    }

    /// <summary>
    /// The 22-case project mapping agreed for the domestic shapefile deposit codes.
    /// The minimum non-rock operational seabed category is the project-defined 50/50 class.
    /// Codes are CASE SENSITIVE: 'mS' (muddy sand) != 'sM' (sandy mud).
    /// </summary>
    public static class KoreaSedimentCatalog
    {
        public const string MappingTableId = "KOREA_DEPOSIT_OPERATIONAL_V1";

        private static readonly KoreaSedimentDefinition[] Definitions =
        {
            // Hard bottoms / gravel grouped operationally as rock (no invented mud/sand split).
            D(1, "R", "Rocky Bottom", "암반", "암반", null, null, 0),
            D(2, "G", "Gravel", "암반/자갈", "암반", null, null, 0),
            D(3, "sG", "Sandy Gravel", "암반/자갈", "암반", null, null, 0),
            D(4, "msG", "Muddy Sandy Gravel", "암반/자갈", "암반", null, null, 0),
            D(5, "mG", "Muddy Gravel", "암반/자갈", "암반", null, null, 0),

            // Sand source classes retain their original/primary meaning, but the project-derived
            // operational seabed scale starts at 50/50. No derived 0/100 category is emitted.
            D(6, "S", "Sand", "모래", "뻘·모래 반반", 50, 50, 5),
            D(7, "(g)S", "Slightly Gravelly Sand", "모래", "뻘·모래 반반", 50, 50, 5),
            D(8, "gS", "Gravelly Sand", "모래", "뻘·모래 반반", 50, 50, 5),

            // The agreed scheme has no 30% mud / 70% sand category. These
            // sand-major mixed types use the operational 50/50 category.
            D(9, "cS", "Clayey Sand", "뻘·모래 혼합", "뻘·모래 반반", 50, 50, 5),
            D(10, "zS", "Silty Sand", "뻘·모래 혼합", "뻘·모래 반반", 50, 50, 5),
            D(11, "mS", "Muddy Sand", "뻘·모래 혼합", "뻘·모래 반반", 50, 50, 5),
            D(12, "(g)mS", "Slightly Gravelly Muddy Sand", "뻘·모래 혼합", "뻘·모래 반반", 50, 50, 5),
            D(13, "gmS", "Gravelly Muddy Sand", "뻘·모래 혼합", "뻘·모래 반반", 50, 50, 5),

            // Mud-dominant mixed types. Fractions are project conventions,
            // not source-measured constituent percentages.
            D(14, "sM", "Sandy Mud", "뻘 우세 혼합", "뻘 우세 혼합", 70, 30, 35),
            D(15, "(g)sM", "Slightly Gravelly Sandy Mud", "뻘 우세 혼합", "뻘 우세 혼합", 70, 30, 35),
            D(16, "sZ", "Sandy Silt", "뻘 우세 혼합", "뻘 우세 혼합", 70, 30, 35),
            D(17, "gM", "Gravelly Mud", "뻘 우세 혼합", "뻘 우세 혼합", 70, 30, 35),

            D(18, "(g)M", "Slightly Gravelly Mud", "뻘", "뻘 우세 혼합", 80, 20, 65),
            D(19, "Z", "Silt", "뻘", "뻘 우세 혼합", 80, 20, 65),
            D(20, "M", "Mud", "뻘", "뻘 우세 혼합", 80, 20, 65),

            // Clay-major class as explicitly agreed (including sandy clay).
            D(21, "sC", "Sandy Clay", "뻘", "뻘", 100, 0, 85),
            D(22, "C", "Clay", "뻘", "뻘", 100, 0, 85)
        };

        private static readonly Dictionary<string, KoreaSedimentDefinition> ByCode = BuildByCode();
        public static IReadOnlyList<KoreaSedimentDefinition> All => Definitions;

        public static bool TryGet(int index, out KoreaSedimentDefinition definition)
        {
            if (index >= 1 && index <= Definitions.Length && Definitions[index - 1].Index == index)
            {
                definition = Definitions[index - 1];
                return true;
            }
            definition = null!;
            return false;
        }

        public static bool TryGet(string? code, out KoreaSedimentDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                definition = null!;
                return false;
            }
            return ByCode.TryGetValue(code.Trim(), out definition!);
        }

        private static KoreaSedimentDefinition D(int index, string code,
            string originalClassification, string primary, string seabed,
            double? mud, double? sand, double burial)
            => new KoreaSedimentDefinition(index, code, originalClassification,
                primary, seabed, mud, sand, burial);

        private static Dictionary<string, KoreaSedimentDefinition> BuildByCode()
        {
            // Ordinal, not OrdinalIgnoreCase: mS and sM have different meanings!
            var result = new Dictionary<string, KoreaSedimentDefinition>(StringComparer.Ordinal);
            foreach (var item in Definitions)
            {
                if (result.ContainsKey(item.Code))
                    throw new InvalidOperationException("Duplicate domestic deposit code: " + item.Code);
                if (item.MudPercent.HasValue != item.SandPercent.HasValue
                    || (item.MudPercent.HasValue
                        && Math.Abs(item.MudPercent.Value + item.SandPercent!.Value - 100) > 1e-8))
                    throw new InvalidOperationException("Invalid domestic representative fraction: " + item.Code);
                result.Add(item.Code, item);
            }
            return result;
        }
    }
}
