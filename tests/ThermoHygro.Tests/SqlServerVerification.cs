using Microsoft.Data.SqlClient;
using ThermoHygro.Core;
using ThermoHygro.Infrastructure;

namespace ThermoHygro.Tests;

/// <summary>只在明确传入 --sqlserver 时运行；创建并清理本次独占的临时数据库。</summary>
public static class SqlServerVerification
{
    public static async Task RunAsync(string root, Action<string, bool, string> check)
    {
        string server = Environment.GetEnvironmentVariable("THERMOHYGRO_SQLSERVER") ?? @"(localdb)\MSSQLLocalDB";
        string database = "ThermoHygroVerify_" + Guid.NewGuid().ToString("N");
        var builder = new SqlConnectionStringBuilder { DataSource = server, InitialCatalog = "master", IntegratedSecurity = true, TrustServerCertificate = true, ConnectTimeout = 10, Pooling = false };
        string master = builder.ConnectionString;
        bool created = false;
        try
        {
            await Exec(master, $"CREATE DATABASE [{database}]"); created = true;
            builder.InitialCatalog = database;
            string target = builder.ConnectionString;
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "database", "sqlserver", "001_init.sql"))) directory = directory.Parent;
            if (directory is null) throw new FileNotFoundException("未找到 SQL Server 初始化脚本。");
            string script = File.ReadAllText(Path.Combine(directory.FullName, "database", "sqlserver", "001_init.sql"));
            string upgrade = File.ReadAllText(Path.Combine(directory.FullName, "database", "sqlserver", "002_multi_sensor_thresholds.sql"));
            string oldScript = script[..script.IndexOf("-- 0.4 → 0.5", StringComparison.Ordinal)]
                .Replace("VersionId BIGINT NOT NULL PRIMARY KEY", "VersionId INT NOT NULL PRIMARY KEY")
                .Replace("ThresholdVersionId BIGINT NULL", "ThresholdVersionId INT NULL");
            await Exec(target, oldScript);
            await Exec(target, "INSERT dbo.ThresholdVersion(VersionId,DeviceId,TemperatureHigh,EffectiveFromUtc,Source) VALUES(19,N'old-device',8,N'2026-01-01T00:00:00Z',N'升级前');");
            await Exec(target, script); await Exec(target, script);
            check("SQL Server 原地升级及重复初始化成功", await Scalar(target, "SELECT Version FROM dbo.SchemaVersion WHERE Module=N'core'") == 2, "使用本次新建的隔离 LocalDB 数据库");
            check("原地升级保留已有阈值数据", await Scalar(target, "SELECT COUNT(*) FROM dbo.ThresholdVersion WHERE VersionId=19 AND TemperatureHigh=8") == 1, "");
            check("四个阈值编号列都升级为 BIGINT", await Scalar(target, "SELECT COUNT(*) FROM sys.columns WHERE (object_id=OBJECT_ID('dbo.ThresholdVersion') AND name='VersionId' OR object_id IN (OBJECT_ID('dbo.SampleJournal'),OBJECT_ID('dbo.MinuteSummary'),OBJECT_ID('dbo.MonitorEvent')) AND name='ThresholdVersionId') AND system_type_id=127") == 4, "");
            await Exec(target, upgrade); await Exec(target, upgrade);
            check("Navicat 格式升级脚本可重复执行", await Scalar(target, "SELECT COUNT(*) FROM dbo.ThresholdVersion WHERE VersionId=19") == 1 && !System.Text.RegularExpressions.Regex.IsMatch(upgrade, @"(?im)^\s*GO\s*$|^\s*:setvar"), "通过 SQL Server 引擎执行，无客户端专用批次分隔符");

            using var local = new SqliteLocalStore(Path.Combine(root, "sqlserver-source.db"));
            await local.InitializeAsync();
            var cold = new DeviceIdentity { DisplayName = "SQL验证冷库" };
            var missing = new DeviceIdentity { DisplayName = "SQL验证缺测设备" };
            var coldVersion = new ThresholdSnapshot { VersionId = ThresholdVersions.Next(), TemperatureHigh = 8 };
            var missingVersion = new ThresholdSnapshot { VersionId = ThresholdVersions.Next(), TemperatureHigh = 30 };
            var good = new ReadingGroup
            {
                AttemptTimeUtc = DateTimeOffset.UtcNow,
                Temperature = new() { Kind = MetricKind.Temperature, Quality = MetricQuality.Good, Value = 4, TimestampUtc = DateTimeOffset.UtcNow },
                Humidity = new() { Kind = MetricKind.Humidity, Quality = MetricQuality.Good, Value = 52, TimestampUtc = DateTimeOffset.UtcNow }
            };
            var failed = new ReadingGroup
            {
                AttemptTimeUtc = DateTimeOffset.UtcNow,
                Temperature = new() { Kind = MetricKind.Temperature, Quality = MetricQuality.Failed, Reason = "模拟无回复" },
                Humidity = new() { Kind = MetricKind.Humidity, Quality = MetricQuality.Failed, Reason = "模拟无回复" }
            };
            check("远端启动前本地已可靠保存多设备样本", (await local.AppendSampleAsync(cold, coldVersion, good)).Saved && (await local.AppendSampleAsync(missing, missingVersion, failed)).Saved, "");
            await local.AppendEventsAsync(new[] { new MonitorEvent { DeviceId = cold.DeviceId, OccurredAtUtc = DateTimeOffset.UtcNow, EventType = "验证事件", ThresholdVersionId = coldVersion.VersionId } });
            using var sink = new SqlServerRemoteSink(local.ConnectionString, new RemoteSyncOptions { ConnectionString = target, DestinationIdentity = SqlServerRemoteSink.DeriveIdentity(target), SyncIntervalSeconds = 1 });
            sink.Start();
            var until = DateTime.UtcNow.AddSeconds(30);
            while (await local.CountPendingAsync() > 0 && DateTime.UtcNow < until) await Task.Delay(200);
            await sink.StopAsync();
            check("本地队列真实同步到 SQL Server", await local.CountPendingAsync() == 0, sink.LastError);
            check("远端多个传感器分别保留独立分钟", await Scalar(target, "SELECT COUNT(*) FROM dbo.MinuteSummary") == 2, "");
            check("远端整轮缺测保持空值而非零", await Scalar(target, "SELECT COUNT(*) FROM dbo.MinuteSummary WHERE TemperatureSum IS NULL AND TemperatureValidCount=0 AND TempMissing>0") == 1, "");
            check("64 位阈值编号及事件完整同步", await Scalar(target, $"SELECT COUNT(*) FROM dbo.ThresholdVersion WHERE VersionId={coldVersion.VersionId}") == 1 && await Scalar(target, $"SELECT COUNT(*) FROM dbo.MonitorEvent WHERE ThresholdVersionId={coldVersion.VersionId}") == 1, "");
            check("传感器名称完整同步", await Scalar(target, "SELECT COUNT(*) FROM dbo.Device WHERE DisplayName IN(N'SQL验证冷库',N'SQL验证缺测设备')") == 2, "");
            await Exec(target, "UPDATE dbo.SchemaVersion SET Version=99 WHERE Module=N'core'");
            bool rejected = false;
            try { await Exec(target, script); } catch (SqlException ex) when (ex.Number == 50002) { rejected = true; }
            check("更高版本数据库被拒绝，数据及版本保留", rejected && await Scalar(target, "SELECT Version FROM dbo.SchemaVersion WHERE Module=N'core'") == 99 && await Scalar(target, "SELECT COUNT(*) FROM dbo.SampleJournal") == 2, "");
        }
        finally
        {
            if (created) await Exec(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
            Console.WriteLine("本次隔离 SQL Server 数据库已清理：" + database);
        }
    }
    private static async Task Exec(string connection, string sql)
    {
        await using var cn = new SqlConnection(connection); await cn.OpenAsync();
        using var command = cn.CreateCommand(); command.CommandTimeout = 60; command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<long> Scalar(string connection, string sql)
    {
        await using var cn = new SqlConnection(connection); await cn.OpenAsync();
        using var command = cn.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
