using System;
using System.Collections.Generic;

namespace MarineEnvironment.Models
{
    /// <summary>
    /// Controls how many FES2014 tidal constituents are used when a current value is synthesized.
    /// </summary>
    public enum CurrentConstituentMode
    {
        /// <summary>M2, S2, K1, O1.</summary>
        Major4,

        /// <summary>M2, S2, K1, O1 plus N2 and K2.</summary>
        Major6,

        /// <summary>All 34 constituents supplied by the FES2014 current atlas.</summary>
        Full
    }

    /// <summary>
    /// Vector-current result. Direction convention will be finalized with the FES2014 reader
    /// after the source NetCDF phase convention is verified.
    /// </summary>
    public sealed class CurrentValue
    {
        public double EastwardVelocity { get; init; }
        public double NorthwardVelocity { get; init; }
        public double Speed { get; init; }
        public double Direction { get; init; }
        public CurrentConstituentMode ConstituentMode { get; init; }
        public int ConstituentCount { get; init; }
    }

    public sealed record Fes2014Constituent(string Name, string FileStem);

    /// <summary>
    /// Canonical FES2014 current constituent sets. File stems follow the official
    /// eastward_velocity.yaml / northward_velocity.yaml atlas configuration.
    /// </summary>
    public static class Fes2014Constituents
    {
        private static readonly Fes2014Constituent[] All =
        {
            new Fes2014Constituent("2N2", "2n2"),
            new Fes2014Constituent("Eps2", "eps2"),
            new Fes2014Constituent("J1", "j1"),
            new Fes2014Constituent("K1", "k1"),
            new Fes2014Constituent("K2", "k2"),
            new Fes2014Constituent("L2", "l2"),
            new Fes2014Constituent("Lambda2", "la2"),
            new Fes2014Constituent("M2", "m2"),
            new Fes2014Constituent("M3", "m3"),
            new Fes2014Constituent("M4", "m4"),
            new Fes2014Constituent("M6", "m6"),
            new Fes2014Constituent("M8", "m8"),
            new Fes2014Constituent("MKS2", "mks2"),
            new Fes2014Constituent("MN4", "mn4"),
            new Fes2014Constituent("MS4", "ms4"),
            new Fes2014Constituent("MSf", "msf"),
            new Fes2014Constituent("Mf", "mf"),
            new Fes2014Constituent("Mm", "mm"),
            new Fes2014Constituent("Msqm", "msqm"),
            new Fes2014Constituent("Mtm", "mtm"),
            new Fes2014Constituent("Mu2", "mu2"),
            new Fes2014Constituent("N2", "n2"),
            new Fes2014Constituent("N4", "n4"),
            new Fes2014Constituent("Nu2", "nu2"),
            new Fes2014Constituent("O1", "o1"),
            new Fes2014Constituent("P1", "p1"),
            new Fes2014Constituent("Q1", "q1"),
            new Fes2014Constituent("R2", "r2"),
            new Fes2014Constituent("S1", "s1"),
            new Fes2014Constituent("S2", "s2"),
            new Fes2014Constituent("S4", "s4"),
            new Fes2014Constituent("Sa", "sa"),
            new Fes2014Constituent("Ssa", "ssa"),
            new Fes2014Constituent("T2", "t2")
        };

        private static readonly Fes2014Constituent[] Major4 =
        {
            Find("M2"), Find("S2"), Find("K1"), Find("O1")
        };

        private static readonly Fes2014Constituent[] Major6 =
        {
            Find("M2"), Find("S2"), Find("K1"), Find("O1"), Find("N2"), Find("K2")
        };

        public static IReadOnlyList<Fes2014Constituent> Get(CurrentConstituentMode mode)
        {
            switch (mode)
            {
                case CurrentConstituentMode.Major4:
                    return Major4;
                case CurrentConstituentMode.Major6:
                    return Major6;
                case CurrentConstituentMode.Full:
                    return All;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown current constituent mode.");
            }
        }

        private static Fes2014Constituent Find(string name)
        {
            foreach (var item in All)
            {
                if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
            throw new InvalidOperationException($"FES2014 constituent '{name}' is not registered.");
        }
    }
}
