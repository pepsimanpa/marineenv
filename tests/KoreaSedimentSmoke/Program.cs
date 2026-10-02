using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MarineEnvironment;
using MarineEnvironment.Configuration;
using MarineEnvironment.Models;

namespace KoreaSedimentSmoke
{
    internal static class Program
    {
        private static readonly (string Code, string Name, double? Mud, double? Sand, double Burial)[] Expected =
        {
            ("R", "Rocky Bottom", null, null, 0),
            ("G", "Gravel", null, null, 0),
            ("sG", "Sandy Gravel", null, null, 0),
            ("msG", "Muddy Sandy Gravel", null, null, 0),
            ("mG", "Muddy Gravel", null, null, 0),
            ("S", "Sand", 0, 100, 5),
            ("(g)S", "Slightly Gravelly Sand", 0, 100, 5),
            ("gS", "Gravelly Sand", 0, 100, 5),
            ("cS", "Clayey Sand", 50, 50, 5),
            ("zS", "Silty Sand", 50, 50, 5),
            ("mS", "Muddy Sand", 50, 50, 5),
            ("(g)mS", "Slightly Gravelly Muddy Sand", 50, 50, 5),
            ("gmS", "Gravelly Muddy Sand", 50, 50, 5),
            ("sM", "Sandy Mud", 70, 30, 35),
            ("(g)sM", "Slightly Gravelly Sandy Mud", 70, 30, 35),
            ("sZ", "Sandy Silt", 70, 30, 35),
            ("gM", "Gravelly Mud", 70, 30, 35),
            ("(g)M", "Slightly Gravelly Mud", 80, 20, 65),
            ("Z", "Silt", 80, 20, 65),
            ("M", "Mud", 80, 20, 65),
            ("sC", "Sandy Clay", 100, 0, 85),
            ("C", "Clay", 100, 0, 85)
        };

        public static void Main()
        {
            var dir = Path.Combine(Path.GetTempPath(), "marineenv-korea-smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                CheckCatalog();
                var locations = Enumerable.Range(0, Expected.Length)
                    .Select(i => (X: 124.0 + (i % 11) * 0.2,
                                  Y: 33.0 + (i / 11) * 0.2))
                    .ToArray();
                var koreaPath = Path.Combine(dir, "korea_sediment_deposits_type");
                WriteShapefile(koreaPath, "deposit", Expected.Select(x => x.Code).ToArray(), locations);

                using var manager = new MarineEnvironmentManager();
                var koreaState = manager.LoadSource(new DataSourceOption
                {
                    Id = "KOREA_SMOKE",
                    Type = EnvironmentType.Seabed,
                    Format = DataSourceFormat.KoreaSediment,
                    Path = koreaPath + ".shp"
                });
                Check(koreaState.Status == SourceStatus.Ready,
                    "Domestic source initialization: " + koreaState.Message);

                for (var i = 0; i < Expected.Length; i++)
                {
                    var expected = Expected[i];
                    var p = locations[i];
                    var query = new EnvironmentQuery
                    {
                        Longitude = p.X + 0.05,
                        Latitude = p.Y + 0.05
                    };
                    var result = manager.QuerySource("KOREA_SMOKE", query);
                    var raw = result.SourceValue?.Value as KoreaSedimentValue;
                    Check(raw != null, "Missing domestic polygon " + expected.Code);
                    Check(raw!.Code == expected.Code, "Wrong code for " + expected.Code);
                    Check(raw.OriginalClassification == expected.Name,
                        "Wrong original classification for " + expected.Code);
                    Check(result.DerivedValues.Count == 1,
                        "Expected ONE Korean derived result for " + expected.Code);
                    var derived = result.DerivedValues[0].Value as KoreaSedimentDerivedValue;
                    Check(derived != null, "Wrong derived type for " + expected.Code);
                    Equal(expected.Mud, derived!.MudPercent, expected.Code + " mud");
                    Equal(expected.Sand, derived.SandPercent, expected.Code + " sand");
                    Equal(expected.Burial, derived.BurialRatePercent, expected.Code + " burial");
                    Check(result.SourceValue!.Metadata!["depositCode"].ToString() == expected.Code,
                        "Original code metadata mismatch " + expected.Code);
                }

                // Distinguish case-sensitive Folk codes mS (5%) and sM (35%).
                Check(KoreaSedimentCatalog.TryGet("mS", out var mS) && mS.BurialRatePercent == 5,
                    "mS code case sensitivity");
                Check(KoreaSedimentCatalog.TryGet("sM", out var sM) && sM.BurialRatePercent == 35,
                    "sM code case sensitivity");
                Check(!KoreaSedimentCatalog.TryGet("ms", out _),
                    "Do not match non-existent ambiguous lowercase code");

                var grid = manager.QueryGrid("KOREA_SMOKE", new GridQuery
                {
                    MinLatitude = 33.02,
                    MaxLatitude = 33.08,
                    MinLongitude = 124.02,
                    MaxLongitude = 124.08,
                    Width = 2,
                    Height = 2
                });
                Check(grid.Values.All(x => x == 1), "Grid should return domestic R index, not a SHOM index");
                Check(grid.Labels!.All(x => x != null && x.Contains("Rocky Bottom") && x.Contains("burial 0%")),
                    "Grid raw/derived labels not exposed");
                Check(grid.Metadata!["classificationScheme"].ToString() == "KoreaDeposit",
                    "Viewer must distinguish domestic grid from SHOM");

                var missing = manager.Query("KOREA_SMOKE",
                    new EnvironmentQuery { Latitude = 33.15, Longitude = 123.50 });
                Check(missing == null, "No polygon should mean no domestic data, not global fallback");

                // Two independent sources at one coordinate: Korean muddy sand
                // 50/50, 5%; SHOM rock, 0%. Neither result overrides the other.
                const int overlapIndex = 10; // mS
                var overlap = locations[overlapIndex];
                var shomPath = Path.Combine(dir, "mock_shom");
                WriteShapefile(shomPath, "typelem", new[] { "NFRoche" }, new[] { overlap });
                var shomMappingPath = Path.Combine(dir, "shom-mapping.json");
                File.WriteAllText(shomMappingPath,
                    "{\"id\":\"SMOKE_SHOM\",\"rules\":[{\"shomCodes\":[\"NFRoche\"],"
                    + "\"shomOriginalClassification\":\"Rock\",\"primaryClassification\":\"암반\","
                    + "\"seabed\":\"암반\",\"burialRatePercent\":0}]}");
                var shomState = manager.LoadSource(new DataSourceOption
                {
                    Id = "SHOM_SMOKE",
                    Type = EnvironmentType.Seabed,
                    Format = DataSourceFormat.ShomSeabed,
                    Path = shomPath + ".shp",
                    SeabedMappingPath = shomMappingPath
                });
                Check(shomState.Status == SourceStatus.Ready,
                    "SHOM mock source initialization: " + shomState.Message);
                var both = manager.Query(new EnvironmentQuery
                {
                    Longitude = overlap.X + 0.05,
                    Latitude = overlap.Y + 0.05
                });
                Check(both.SourceValues.Count(x => x.Type == EnvironmentType.Seabed) == 2,
                    "Expected two INDEPENDENT original seabed values");
                Check(both.SourceValues.Any(x => x.Value is KoreaSedimentValue k && k.Code == "mS"),
                    "Korea raw mS missing");
                Check(both.SourceValues.Any(x => x.Value is SeabedValue s && s.Code == "NFRoche"),
                    "SHOM raw NFRoche missing");
                Check(both.DerivedValues.Any(x => x.SourceId == "KOREA_SMOKE"
                    && x.Value is KoreaSedimentDerivedValue k && k.BurialRatePercent == 5),
                    "Domestic burial mapping missing or overridden");
                Check(both.DerivedValues.Any(x => x.SourceId == "SHOM_SMOKE"
                    && x.Value is SeabedDerivedValue s && s.BurialRatePercent == 0),
                    "Existing SHOM burial mapping missing or overridden");

                var projected = Path.Combine(dir, "invalid-crs");
                WriteShapefile(projected, "deposit", new[] { "R" }, new[] { (125.0, 34.0) });
                File.WriteAllText(projected + ".prj", "PROJCS[\"Test projected dataset\"]");
                var invalid = manager.LoadSource(new DataSourceOption
                {
                    Id = "INVALID_CRS",
                    Type = EnvironmentType.Seabed,
                    Format = DataSourceFormat.KoreaSediment,
                    Path = projected + ".shp"
                });
                Check(invalid.Status != SourceStatus.Ready,
                    "Projected coordinates must not be silently interpreted as WGS84");

                Console.WriteLine("PASS: all 22 Korean deposit codes, operational mud/sand fractions, burial rates, case sensitivity, native grid, no-data, independent SHOM + Korean query, CRS rejection.");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        private static void CheckCatalog()
        {
            Check(KoreaSedimentCatalog.All.Count == Expected.Length, "Expected 22 deposit codes");
            foreach (var e in Expected)
            {
                Check(KoreaSedimentCatalog.TryGet(e.Code, out var d), "Catalog missing " + e.Code);
                Check(d.OriginalClassification == e.Name, "Catalog original classification " + e.Code);
                Equal(e.Mud, d.MudPercent, e.Code + " catalog mud");
                Equal(e.Sand, d.SandPercent, e.Code + " catalog sand");
                Equal(e.Burial, d.BurialRatePercent, e.Code + " catalog burial");
            }
        }

        private static void WriteShapefile(string path, string field, string[] codes,
            (double X, double Y)[] origins)
        {
            if (codes.Length != origins.Length)
                throw new ArgumentException("Codes and square origins mismatch.");

            // A minimal but valid Polygon shapefile, index and ASCII-code DBF;
            // no production Korean or SHOM data is uploaded to the repository.
            const int recordBytes = 128; // Polygon content for one five-vertex ring
            var bbox = (MinX: origins.Min(p => p.X), MinY: origins.Min(p => p.Y),
                MaxX: origins.Max(p => p.X + 0.1), MaxY: origins.Max(p => p.Y + 0.1));

            using (var file = File.Create(path + ".shp"))
            using (var w = new BinaryWriter(file))
            {
                WriteHeader(w, (100 + codes.Length * (8 + recordBytes)) / 2, bbox);
                for (var i = 0; i < codes.Length; i++)
                {
                    WriteBigInt32(w, i + 1);
                    WriteBigInt32(w, recordBytes / 2);
                    w.Write(5); // Polygon
                    var x = origins[i].X;
                    var y = origins[i].Y;
                    w.Write(x);
                    w.Write(y);
                    w.Write(x + 0.1);
                    w.Write(y + 0.1);
                    w.Write(1); // Number of parts
                    w.Write(5); // Five points, closed square
                    w.Write(0); // Part start index
                    foreach (var point in new[]
                    {
                        (X:x, Y:y), (X:x, Y:y + 0.1), (X:x + 0.1, Y:y + 0.1),
                        (X:x + 0.1, Y:y), (X:x, Y:y)
                    })
                    {
                        w.Write(point.X);
                        w.Write(point.Y);
                    }
                }
            }

            using (var file = File.Create(path + ".shx"))
            using (var w = new BinaryWriter(file))
            {
                WriteHeader(w, (100 + codes.Length * 8) / 2, bbox);
                var offsetWords = 50;
                for (var i = 0; i < codes.Length; i++)
                {
                    WriteBigInt32(w, offsetWords);
                    WriteBigInt32(w, recordBytes / 2);
                    offsetWords += 4 + recordBytes / 2;
                }
            }

            using (var file = File.Create(path + ".dbf"))
            using (var w = new BinaryWriter(file))
            {
                w.Write((byte)3);
                w.Write((byte)126); // 2026 - 1900
                w.Write((byte)10);
                w.Write((byte)2);
                w.Write(codes.Length);
                w.Write((ushort)65); // DBF 32-byte header + 32-byte field + terminator
                w.Write((ushort)81); // Deletion flag + 80-byte classification code
                w.Write(new byte[20]);
                var descriptor = new byte[32];
                Encoding.ASCII.GetBytes(field).CopyTo(descriptor, 0);
                descriptor[11] = (byte)'C';
                descriptor[16] = 80;
                w.Write(descriptor);
                w.Write((byte)0x0D);
                foreach (var code in codes)
                {
                    w.Write((byte)0x20);
                    var b = new byte[80];
                    for (var i = 0; i < b.Length; i++) b[i] = 0x20;
                    Encoding.ASCII.GetBytes(code).CopyTo(b, 0);
                    w.Write(b);
                }
                w.Write((byte)0x1A);
            }
            File.WriteAllText(path + ".prj",
                "GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\"]]");
        }

        private static void WriteHeader(BinaryWriter w, int fileWords,
            (double MinX, double MinY, double MaxX, double MaxY) box)
        {
            WriteBigInt32(w, 9994);
            w.Write(new byte[20]);
            WriteBigInt32(w, fileWords);
            w.Write(1000);
            w.Write(5);
            w.Write(box.MinX);
            w.Write(box.MinY);
            w.Write(box.MaxX);
            w.Write(box.MaxY);
            w.Write(0.0);
            w.Write(0.0);
            w.Write(0.0);
            w.Write(0.0);
        }

        private static void WriteBigInt32(BinaryWriter w, int value)
        {
            w.Write(new[] { (byte)(value >> 24), (byte)(value >> 16),
                (byte)(value >> 8), (byte)value });
        }

        private static void Equal(double? expected, double? actual, string message)
        {
            Check(expected.HasValue == actual.HasValue, message + " nullable mismatch");
            if (expected.HasValue)
                Check(Math.Abs(expected.Value - actual!.Value) < 1e-9,
                    message + " expected " + expected + " got " + actual);
        }
        private static void Equal(double expected, double actual, string message)
            => Check(Math.Abs(expected - actual) < 1e-9,
                message + " expected " + expected + " got " + actual);
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
