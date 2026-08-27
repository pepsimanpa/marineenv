using System;
using System.Collections.Generic;
using System.Globalization;

namespace MarineEnvironment.Models
{
    public enum ShomSedimentClass
    {
        Rock,
        Cobbles,
        Gravel,
        Sand,
        FineSand,
        Silt,
        Clay,
        Mud,
        Coral,
        PolymetallicNodules,
        Unknown
    }

    public sealed class SeabedDerivedValue
    {
        public string MappingTableId { get; init; } = string.Empty;
        public string ShomOriginalClassification { get; init; } = string.Empty;
        public string PrimaryClassification { get; init; } = string.Empty;
        public string Seabed { get; init; } = string.Empty;
        public double? MudPercent { get; init; }
        public double? SandPercent { get; init; }
        public double BurialRatePercent { get; init; }

        public string SeabedDisplay
        {
            get
            {
                if (!MudPercent.HasValue || !SandPercent.HasValue)
                    return Seabed;

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} ({1:0.#}% / {2:0.#}%)",
                    Seabed,
                    MudPercent.Value,
                    SandPercent.Value);
            }
        }
    }

    public sealed class SeabedValue
    {
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public ShomSedimentClass SedimentClass { get; init; }
        public int? SourceMapNumber { get; init; }

        /// <summary>
        /// Optional user-derived values calculated from a configurable mapping table.
        /// Raw SHOM values remain available independently in Code/Name/SedimentClass.
        /// </summary>
        public SeabedDerivedValue? Derived { get; init; }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(Name) ? Code : $"{Code} / {Name}";
        }
    }

    public sealed class ShomSedimentDefinition
    {
        public ShomSedimentDefinition(int index, string code, string name, ShomSedimentClass sedimentClass, byte red, byte green, byte blue)
        {
            Index = index;
            Code = code;
            Name = name;
            SedimentClass = sedimentClass;
            Red = red;
            Green = green;
            Blue = blue;
        }

        public int Index { get; }
        public string Code { get; }
        public string Name { get; }
        public ShomSedimentClass SedimentClass { get; }
        public byte Red { get; }
        public byte Green { get; }
        public byte Blue { get; }
    }

    /// <summary>
    /// SHOM worldwide sediment-map typelem catalog and official legend colors.
    /// The raw SHOM code is preserved; project-specific operational reclassification
    /// can be layered on top without losing source semantics.
    /// </summary>
    public static class ShomSedimentCatalog
    {
        private static readonly ShomSedimentDefinition[] Definitions =
        {
            D(1, "NFRoche", "Rock", ShomSedimentClass.Rock, 104,104,104),
            D(2, "NFC", "Cobbles", ShomSedimentClass.Cobbles, 153,88,23),
            D(3, "NFCG", "Cobbles & gravel", ShomSedimentClass.Cobbles, 203,117,31),
            D(4, "NFCS", "Cobbles & sand", ShomSedimentClass.Cobbles, 226,148,70),
            D(5, "NFCV", "Muddy cobbles", ShomSedimentClass.Cobbles, 234,175,113),
            D(6, "NFGC", "Gravel & cobbles", ShomSedimentClass.Gravel, 224,154,14),
            D(7, "NFG", "Gravel", ShomSedimentClass.Gravel, 255,170,0),
            D(8, "NFGS", "Sandy gravel", ShomSedimentClass.Gravel, 255,167,127),
            D(9, "NFGSV", "Muddy sandy gravel", ShomSedimentClass.Gravel, 255,204,127),
            D(10, "NFGVS", "Gravel & sandy mud", ShomSedimentClass.Gravel, 255,219,175),
            D(11, "NFGV", "Muddy gravel", ShomSedimentClass.Gravel, 255,235,190),
            D(12, "NFSC", "Sand & cobbles", ShomSedimentClass.Sand, 168,168,0),
            D(13, "NFSG", "Gravelly sand", ShomSedimentClass.Sand, 186,204,0),
            D(14, "NFSGV", "Muddy gravelly sand", ShomSedimentClass.Sand, 168,222,0),
            D(15, "NFS", "Sand", ShomSedimentClass.Sand, 255,255,0),
            D(16, "NFSSi", "Sand & silt", ShomSedimentClass.Sand, 255,255,150),
            D(17, "NFSVG", "Sand & gravelly mud", ShomSedimentClass.Sand, 235,250,0),
            D(18, "NFSV", "Muddy sand", ShomSedimentClass.Sand, 245,250,92),
            D(19, "NFSFC", "Fine sand & cobbles", ShomSedimentClass.FineSand, 48,220,190),
            D(20, "NFSFG", "Fine sand & gravel", ShomSedimentClass.FineSand, 150,220,190),
            D(21, "NFSF", "Fine sand", ShomSedimentClass.FineSand, 255,255,190),
            D(22, "NFSFSi", "Fine sand & silt", ShomSedimentClass.FineSand, 190,255,232),
            D(23, "NFSFV", "Muddy fine sand", ShomSedimentClass.FineSand, 138,255,180),
            D(24, "NFVC", "Mud & cobbles", ShomSedimentClass.Mud, 0,169,230),
            D(25, "NFVG", "Mud & gravel", ShomSedimentClass.Mud, 0,200,230),
            D(26, "NFVSG", "Mud, sand & gravel", ShomSedimentClass.Mud, 0,240,230),
            D(27, "NFVS", "Sandy mud", ShomSedimentClass.Mud, 153,232,240),
            D(28, "NFVSF", "Mud & fine sand", ShomSedimentClass.Mud, 200,235,240),
            D(29, "NFV", "Mud", ShomSedimentClass.Mud, 200,255,255),
            D(30, "NFSi", "Silt", ShomSedimentClass.Silt, 115,178,255),
            D(31, "NFSiA", "Clayey silt", ShomSedimentClass.Silt, 122,150,255),
            D(32, "NFASi", "Silty clay", ShomSedimentClass.Clay, 170,180,255),
            D(33, "NFA", "Clay", ShomSedimentClass.Clay, 200,200,255),
            D(34, "NFNoP", "Polymetallic nodules", ShomSedimentClass.PolymetallicNodules, 255,190,232),
            D(35, "NFCo", "Coral", ShomSedimentClass.Coral, 255,115,223)
        };

        private static readonly Dictionary<string, ShomSedimentDefinition> ByCode = BuildByCode();
        private static readonly Dictionary<int, ShomSedimentDefinition> ByIndex = BuildByIndex();

        public static IReadOnlyList<ShomSedimentDefinition> All => Definitions;

        public static bool TryGet(string? code, out ShomSedimentDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                definition = null!;
                return false;
            }
            return ByCode.TryGetValue(code.Trim(), out definition!);
        }

        public static bool TryGet(int index, out ShomSedimentDefinition definition)
        {
            return ByIndex.TryGetValue(index, out definition!);
        }

        private static ShomSedimentDefinition D(int index, string code, string name, ShomSedimentClass sedimentClass, byte r, byte g, byte b)
            => new ShomSedimentDefinition(index, code, name, sedimentClass, r, g, b);

        private static Dictionary<string, ShomSedimentDefinition> BuildByCode()
        {
            var result = new Dictionary<string, ShomSedimentDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Definitions)
                result[item.Code] = item;
            return result;
        }

        private static Dictionary<int, ShomSedimentDefinition> BuildByIndex()
        {
            var result = new Dictionary<int, ShomSedimentDefinition>();
            foreach (var item in Definitions)
                result[item.Index] = item;
            return result;
        }
    }
}
