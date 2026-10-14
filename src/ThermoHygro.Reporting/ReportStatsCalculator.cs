using System;
using System.Collections.Generic;
using ThermoHygro.Core;

namespace ThermoHygro.Reporting;

/// <summary>
/// 区间统计：跨桶按"总和 ÷ 有效次数"加权，绝不对已舍入的均值再次平均；
/// 缺测桶不计入有效次数，也不以 0 参与计算。
/// </summary>
public static class ReportStatsCalculator
{
    public static ReportStats From(IReadOnlyList<HistoryPoint> points, int eventCount, TimeZoneInfo zone)
    {
        double tSum = 0, hSum = 0;
        long tValid = 0, hValid = 0;
        long gaps = 0, partial = 0, attempts = 0, missed = 0;
        long tOut = 0, hOut = 0, tMiss = 0, hMiss = 0;
        double? tMin = null, tMax = null, hMin = null, hMax = null;
        DateTimeOffset? tMinAt = null, tMaxAt = null, hMinAt = null, hMaxAt = null;

        foreach (var p in points)
        {
            if (p.IsGap) gaps++;
            if (p.IsPartialGap) partial++;

            attempts += p.AttemptCount;
            missed += p.MissedSlots;
            tOut += p.TempOutOfRangeCount;
            hOut += p.HumidityOutOfRangeCount;
            tMiss += p.TempMissing;
            hMiss += p.HumidityMissing;

            tValid += p.TemperatureValidCount;
            hValid += p.HumidityValidCount;

            if (p.TemperatureAvg.HasValue) tSum += p.TemperatureAvg.Value * p.TemperatureValidCount;
            if (p.HumidityAvg.HasValue) hSum += p.HumidityAvg.Value * p.HumidityValidCount;

            if (p.TemperatureMin.HasValue && (tMin is null || p.TemperatureMin.Value < tMin.Value))
            { tMin = p.TemperatureMin; tMinAt = p.TemperatureMinAtUtc; }
            if (p.TemperatureMax.HasValue && (tMax is null || p.TemperatureMax.Value > tMax.Value))
            { tMax = p.TemperatureMax; tMaxAt = p.TemperatureMaxAtUtc; }
            if (p.HumidityMin.HasValue && (hMin is null || p.HumidityMin.Value < hMin.Value))
            { hMin = p.HumidityMin; hMinAt = p.HumidityMinAtUtc; }
            if (p.HumidityMax.HasValue && (hMax is null || p.HumidityMax.Value > hMax.Value))
            { hMax = p.HumidityMax; hMaxAt = p.HumidityMaxAtUtc; }
        }

        return new ReportStats
        {
            TemperatureAvg = tValid > 0 ? tSum / tValid : null,
            TemperatureMin = tMin,
            TemperatureMax = tMax,
            TemperatureMinAtUtc = tMinAt,
            TemperatureMaxAtUtc = tMaxAt,
            TemperatureValidCount = tValid,
            HumidityAvg = hValid > 0 ? hSum / hValid : null,
            HumidityMin = hMin,
            HumidityMax = hMax,
            HumidityMinAtUtc = hMinAt,
            HumidityMaxAtUtc = hMaxAt,
            HumidityValidCount = hValid,
            BucketCount = points.Count,
            GapBucketCount = gaps,
            PartialGapBucketCount = partial,
            AttemptCount = attempts,
            MissedSlots = missed,
            TempOutOfRangeCount = tOut,
            HumidityOutOfRangeCount = hOut,
            TempMissing = tMiss,
            HumidityMissing = hMiss,
            EventCount = eventCount,
            CoverageRatio = points.Count == 0 ? 0 : (points.Count - gaps) / (double)points.Count
        };
    }
}