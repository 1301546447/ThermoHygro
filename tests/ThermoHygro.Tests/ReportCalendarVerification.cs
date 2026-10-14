using ThermoHygro.Core;
using ThermoHygro.Infrastructure;
using ThermoHygro.Reporting;

namespace ThermoHygro.Tests;

public static class ReportCalendarVerification
{
    public static async Task RunAsync(string root, Action<string, bool, string> check)
    {
        using var store = new SqliteLocalStore(Path.Combine(root, "calendar.db")); await store.InitializeAsync();
        var device = new DeviceIdentity { DisplayName = "本机日界线验证" };
        var th = new ThresholdSnapshot { VersionId = ThresholdVersions.Next() };
        var midnight = DateTime.Today.AddDays(-1);
        async Task Add(DateTime local, double value)
        {
            var utc = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
            var metric = new MetricReading { Kind = MetricKind.Temperature, Quality = MetricQuality.Good, Value = value, TimestampUtc = utc };
            await store.AppendSampleAsync(device, th, new ReadingGroup { AttemptTimeUtc = utc, Temperature = metric });
        }
        await Add(midnight.AddMinutes(-1), 10); await Add(midnight.AddMinutes(1), 20);
        var query = new SqliteHistoryQuery(store.DatabasePath);
        var days = await query.TakeSnapshotAsync(new ReportQuery { DeviceId = device.DeviceId, StartLocal = midnight.AddDays(-1), EndLocalExclusive = midnight.AddDays(1), CutoffUtc = DateTimeOffset.UtcNow, Granularity = ReportGranularity.Day });
        check("每日汇总按本机午夜分为两天", days.Points.Count == 2 && days.Points[0].TemperatureAvg == 10 && days.Points[1].TemperatureAvg == 20 && days.Points.All(point => point.BucketStartLocal.Hour == 0), TimeZoneInfo.Local.Id);
        var minutes = await query.TakeSnapshotAsync(new ReportQuery { DeviceId = device.DeviceId, StartLocal = midnight.AddMinutes(-1), EndLocalExclusive = midnight.AddMinutes(2), CutoffUtc = DateTimeOffset.UtcNow, Granularity = ReportGranularity.Minute });
        check("停采空白分钟明确保留缺测", minutes.Points.Count == 3 && minutes.Points[1].IsGap && minutes.Points[1].AttemptCount == 0 && minutes.Points[1].TemperatureAvg is null, "不会跨过空白分钟假装连续记录");
        check("覆盖率计入停采时段", minutes.Stats.GapBucketCount == 1 && minutes.Stats.CoverageRatio < 1, $"覆盖率 {minutes.Stats.CoverageRatio:P1}");
    }
}
