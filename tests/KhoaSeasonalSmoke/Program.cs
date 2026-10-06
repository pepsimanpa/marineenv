using System;
using System.IO;
using System.Linq;
using System.Text;
using MarineEnvironment;
using MarineEnvironment.Configuration;
using MarineEnvironment.Models;

namespace KhoaSeasonalSmoke
{
    internal static class Program
    {
        private static void Main()
        {
            var directory = Path.Combine(Path.GetTempPath(), "marineenv-khoa-seasonal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                WriteCsv(directory, 2020,
                    "2020-09-17,129,35,200,0",
                    "2020-12-30,130,36,100,0",
                    "2020-02-29,131,37,100,0");
                WriteCsv(directory, 2021,
                    "2021-09-17,129,35,200,180",
                    "2021-01-03,130,36,100,180",
                    "2021-02-28,131,37,300,0");
                WriteCsv(directory, 2022,
                    "2022-09-16,129,35,100,90",
                    "2022-09-18,129,35,100,270",
                    "2022-01-02,130,36,100,90",
                    "2022-03-01,131,37,500,0");
                // A record beyond the +/-7 day seasonal window must not enter the average.
                WriteCsv(directory, 2023, "2023-10-01,129,35,1000,270");
                WriteCsv(directory, 2024, "2024-05-01,129,35,1000,90");
                WriteCsv(directory, 2026, "2026-02-01,129,35,1000,90");

                using var manager = new MarineEnvironmentManager();
                var source = manager.LoadSource(new DataSourceOption
                {
                    Id = "KHOA_TEST",
                    Type = EnvironmentType.Current,
                    Format = DataSourceFormat.KhoaDailyCurrentCsv,
                    Path = directory,
                    MaxTemporalOffsetDays = 7,
                    MaxNearestDistanceKm = 30
                });
                Ensure(source.Status == SourceStatus.Ready, "KHOA did not initialize: " + source.Message);

                var autumn = Get(manager, 35, 129, new DateTime(2026, 9, 17, 9, 0, 0, DateTimeKind.Utc));
                // 2020 north 2 m/s, 2021 south 2 m/s, 2022 east 1 m/s.
                // Sep 16 and Sep 18 are equidistant: the past date (Sep 16) wins.
                var autumnVector = (CurrentValue)autumn.Value!;
                Near(1.0 / 3.0, autumnVector.EastwardVelocity, "autumn mean eastward");
                Near(0.0, autumnVector.NorthwardVelocity, "autumn mean northward");
                Near(1.0 / 3.0, autumnVector.Speed, "autumn mean speed");
                Near(90, autumnVector.Direction, "autumn mean direction");
                Ensure(Convert.ToInt32(autumn.Metadata!["sourceSampleCount"]) == 3, "Expected one sample from each of three source years");
                Ensure((string)autumn.Metadata!["sourceYears"]! == "2020,2021,2022", "Incorrect contributing years");
                Ensure(autumn.DateTime == new DateTime(2026, 9, 17), "Result should have the requested date, not a fabricated observation date");

                // Neither request year nor hour changes which historical records contribute.
                var anotherYear = Get(manager, 35, 129, new DateTime(2035, 9, 17, 23, 55, 0, DateTimeKind.Utc));
                Near(autumnVector.EastwardVelocity, ((CurrentValue)anotherYear.Value!).EastwardVelocity, "year and time ignored");
                Ensure(anotherYear.DateTime == new DateTime(2035, 9, 17), "Result should preserve requested year");

                // Dec 30 and Jan 3 must be considered near Jan 2 across the year boundary.
                var newYear = (CurrentValue)Get(manager, 36, 130, new DateTime(2035, 1, 2)).Value!;
                Near(1.0 / 3.0, newYear.Speed, "New Year boundary mean");
                Near(90, newYear.Direction, "New Year boundary direction");

                // The leap-day request maps to Feb 28 in non-leap source years.
                var leap = (CurrentValue)Get(manager, 37, 131, new DateTime(2028, 2, 29)).Value!;
                Near(3.0, leap.Speed, "leap-day mean");
                Near(0, leap.Direction, "leap-day direction");

                // The display grid and native arrows must expose the same single mean per site.
                var grid = manager.QueryGrid("KHOA_TEST", new GridQuery
                {
                    MinLatitude = 34.99,
                    MaxLatitude = 35.01,
                    MinLongitude = 128.99,
                    MaxLongitude = 129.01,
                    DateTime = new DateTime(2040, 9, 17),
                    Width = 2,
                    Height = 2
                });
                Ensure(grid.Values.All(x => x.HasValue), "Expected a complete local grid");
                Near(1.0 / 3.0, grid.Values[0]!.Value, "grid seasonal speed");
                Ensure(grid.CurrentVectors != null && grid.CurrentVectors.Count == 1, "Expected exactly one site-level mean arrow");
                Ensure(grid.CurrentVectors![0].SourceDate == null, "Mean vector must not claim one observation date");

                var missing = manager.QuerySource("KHOA_TEST", new EnvironmentQuery
                {
                    Latitude = 35,
                    Longitude = 129,
                    DateTime = new DateTime(2035, 7, 1)
                }).SourceValue;
                Ensure(missing == null, "Out-of-window seasonal request should return NoData");

                Console.WriteLine("PASS: cross-year seasonal vector mean, per-year selection, tie, year/time independence, New Year and leap-day handling, grid, NoData.");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static EnvironmentValue Get(MarineEnvironmentManager manager, double lat, double lon, DateTime when)
        {
            return manager.QuerySource("KHOA_TEST", new EnvironmentQuery
            {
                Latitude = lat,
                Longitude = lon,
                DateTime = when
            }).SourceValue ?? throw new InvalidOperationException("Expected current at " + lat + ", " + lon + " on " + when);
        }

        private static void WriteCsv(string directory, int year, params string[] rows)
        {
            var file = Path.Combine(directory, $"해양수산부_지능형해상교통정보_수치조류도_{year}.csv");
            File.WriteAllText(file,
                "검색 시간,지점경도,지점위도,유속(cm_s),유향\n" + string.Join("\n", rows) + "\n",
                new UTF8Encoding(false));
        }

        private static void Near(double expected, double actual, string label)
        {
            if (Math.Abs(expected - actual) > 1e-8)
                throw new InvalidOperationException($"{label}: expected {expected:G17}, actual {actual:G17}");
        }

        private static void Ensure(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
