using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ThermoHygro.Infrastructure;

namespace ThermoHygro.Cli;

/// <summary>
/// 数据库初始化命令行工具（图形向导与脚本语义一致）。
/// 退出码：0 成功/已是目标版本，2 参数错误，3 连接/认证失败，4 权限不足，5 结构不兼容，6 执行失败。
/// 密码只允许交互输入或凭据引用，不得通过命令行传入，也不写入日志。
/// </summary>
public static class Program
{
    private const int ExitOk = 0, ExitArgs = 2, ExitConnect = 3, ExitPermission = 4,
                      ExitSchema = 5, ExitExecute = 6;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return args.Length == 0 ? ExitArgs : ExitOk;
        }

        if (args[0] != "db" || args.Length < 2 || args[1] != "init")
        {
            Console.Error.WriteLine($"未知命令：{string.Join(' ', args)}");
            PrintHelp();
            return ExitArgs;
        }

        var opt = ParseOptions(args.Skip(2).ToArray());
        if (opt is null) return ExitArgs;

        if (!opt.TryGetValue("provider", out var provider))
        {
            Console.Error.WriteLine("缺少必需参数 --provider（sqlite 或 sqlserver）。");
            return ExitArgs;
        }

        bool dryRun = opt.ContainsKey("dry-run");
        return provider.ToLowerInvariant() switch
        {
            "sqlite" => await InitSqliteAsync(opt, dryRun),
            "sqlserver" => await InitSqlServerAsync(opt, dryRun),
            _ => ArgError($"不支持的 provider：{provider}（仅支持 sqlite / sqlserver）")
        };
    }

    private static int ArgError(string msg)
    {
        Console.Error.WriteLine("参数错误：" + msg);
        return ExitArgs;
    }

    private static Dictionary<string, string>? ParseOptions(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) return null;
            var key = a[2..];
            if (key is "dry-run" or "create-database") { d[key] = "true"; continue; }
            if (i + 1 >= args.Length) { Console.Error.WriteLine($"参数 --{key} 缺少取值。"); return null; }
            d[key] = args[++i];
        }

        // 安全：明确拒绝命令行传入密码
        if (d.ContainsKey("password") || d.ContainsKey("pwd"))
        {
            Console.Error.WriteLine("安全拒绝：密码不能通过命令行传入。请使用 --auth windows，或用 --credential 引用已保存凭据。");
            return null;
        }
        return d;
    }

    private static async Task<int> InitSqliteAsync(Dictionary<string, string> opt, bool dryRun)
    {
        var target = opt.TryGetValue("file", out var f) ? f
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThermoHygro", "Data", "history.db");

        var scriptPath = FindScript("sqlite", "001_init.sql");
        Console.WriteLine($"[SQLite] 目标文件：{target}");
        Console.WriteLine($"[SQLite] 建表脚本：{scriptPath ?? "(未找到，将使用内嵌脚本)"}");

        if (dryRun)
        {
            Console.WriteLine($"[dry-run] 仅检查：显示目标路径与目录状态，不创建/不升级任何结构。");
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Console.WriteLine($"[dry-run] 目录不存在，正式执行时会创建：{dir}");
            else if (!string.IsNullOrEmpty(dir))
                Console.WriteLine($"[dry-run] 目录已存在：{dir}");
            Console.WriteLine(File.Exists(target)
                ? "[dry-run] 目标库已存在：正式执行会补齐缺失的表/索引，不删除已有数据。"
                : "[dry-run] 目标库尚不存在：正式执行会新建库与全部表、索引。");
            Console.WriteLine("[dry-run] 完成：未做任何修改。");
            return ExitOk;
        }

        try
        {
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            bool existed = File.Exists(target);
            var script = scriptPath is not null && File.Exists(scriptPath)
                ? await File.ReadAllTextAsync(scriptPath)
                : LoadEmbeddedSqliteSchema(opt);

            await using var conn = new SqliteConnection($"Data Source={target};Pooling=False");
            await conn.OpenAsync();

            // 结构不兼容检查必须在执行脚本之前：脚本本身只升版本号，
            // 但若先执行再检查，就无法在不改动高版本库的前提下安全中止。
            long versionBefore = 0;
            await using (var probe = conn.CreateCommand())
            {
                probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='SchemaVersion' LIMIT 1";
                var hasTable = await probe.ExecuteScalarAsync() is not null;
                if (hasTable)
                {
                    await using var v = conn.CreateCommand();
                    v.CommandText = "SELECT MAX(Version) FROM SchemaVersion";
                    var raw = await v.ExecuteScalarAsync();
                    versionBefore = raw is null || raw is DBNull ? 0 : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                }
            }
            if (versionBefore > 1)
            {
                Console.Error.WriteLine($"结构不兼容：库中版本 {versionBefore} 高于本程序支持版本 1，已停止且未修改数据。");
                return ExitSchema;
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = script;
                await cmd.ExecuteNonQueryAsync();
            }

            long version = versionBefore;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT MAX(Version) FROM SchemaVersion";
                var v = await cmd.ExecuteScalarAsync();
                version = v is null || v is DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }

            if (version < 1)
            {
                Console.Error.WriteLine("执行失败：脚本执行后仍未写入结构版本，请检查脚本内容。");
                return ExitExecute;
            }

            Console.WriteLine(existed
                ? $"库已存在：{target}"
                : $"已创建数据库：{target}");
            Console.WriteLine($"初始化完成：SchemaVersion = {version}（重复执行不会删除已有数据）。");
            return ExitOk;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine("权限不足：" + ex.Message);
            return ExitPermission;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("执行失败：" + ex.Message);
            return ExitExecute;
        }
    }

    private static async Task<int> InitSqlServerAsync(Dictionary<string, string> opt, bool dryRun)
    {
        if (!opt.TryGetValue("server", out var server))
            return ArgError("SQL Server 需要 --server，例如 --server \".\\SQLEXPRESS\"。");
        var database = opt.TryGetValue("database", out var db) ? db : "ThermoHygro";
        var auth = opt.TryGetValue("auth", out var a) ? a.ToLowerInvariant() : "windows";
        bool createDb = opt.ContainsKey("create-database");
        _trustServerCertificate = opt.ContainsKey("trust-server-certificate");

        if (auth is not ("windows" or "sql"))
            return ArgError($"不支持的 --auth 取值：{auth}（仅支持 windows / sql）。");

        Console.WriteLine($"[SQL Server] 服务器：{server}，数据库：{database}，认证：{auth}");

        // 拒绝命令行明文密码（与 sqlite 分支一致：密码不得经命令行传入）
        if (opt.ContainsKey("password") || opt.ContainsKey("pwd"))
            return ArgError("拒绝在命令行传入密码：请改用集成认证（--auth windows），"
                + "或设置环境变量 THERMOHYGRO_SQL_PASSWORD 后在提示符下交互输入。");

        // SQL 认证需要账号口令：只设 IntegratedSecurity=false 的连接必然失败，
        // 因此这里在早期就给出明确原因，而不是让用户看到一句难懂的认证错误。
        string? sqlUser = null, sqlPassword = null;
        if (auth == "sql")
        {
            sqlUser = opt.TryGetValue("user", out var u) ? u : null;
            if (string.IsNullOrWhiteSpace(sqlUser))
                return ArgError("--auth sql 需要 --user 指定 SQL Server 登录名（口令请用环境变量 THERMOHYGRO_SQL_PASSWORD 或交互输入）。");
            sqlPassword = ReadSqlPassword();
            if (string.IsNullOrEmpty(sqlPassword))
                return ArgError("未取到口令。请设置环境变量 THERMOHYGRO_SQL_PASSWORD，"
                    + "或在交互式终端中运行本命令以输入口令（不接受命令行明文密码）。");
        }

        var scriptPath = FindScript("sqlserver", "001_init.sql");
        if (scriptPath is null)
            return ArgError("未找到建表脚本 database/sqlserver/001_init.sql（请在仓库根目录或发布目录内执行）。");

        var cs = BuildConnectionString(server, database, auth, sqlUser, sqlPassword);
        Console.WriteLine($"[SQL Server] 建表脚本：{scriptPath}");

        if (dryRun)
        {
            Console.WriteLine("[dry-run] 将测试连接 → 枚举/校验目标库 → 预览将新增的表与索引；本次不写入。");
            var (ok, detail) = await SqlServerRemoteSink.TestConnectionAsync(cs).ConfigureAwait(false);
            Console.WriteLine(ok ? $"[dry-run] 连接可用：{detail}" : $"[dry-run] 连接不可用：{detail}");
            Console.WriteLine("[dry-run] 完成：未做任何修改。");
            return ok ? ExitOk : ExitConnect;
        }

        try
        {
            // 需要时先建库（建库不能包在迁移事务里）
            if (createDb)
            {
                var master = BuildConnectionString(server, "master", auth, sqlUser, sqlPassword);
                await using var mcn = new Microsoft.Data.SqlClient.SqlConnection(master);
                await mcn.OpenAsync().ConfigureAwait(false);
                await using var c = mcn.CreateCommand();
                var safeDb = database.Replace("'", "''");
                var quotedDb = database.Replace("]", "]]");
                c.CommandText = "IF DB_ID(N'" + safeDb + "') IS NULL CREATE DATABASE [" + quotedDb + "]";
                c.CommandTimeout = 120;
                await c.ExecuteNonQueryAsync().ConfigureAwait(false);
                Console.WriteLine("[SQL Server] 数据库已就绪（已存在则跳过）。");
            }

            var text = await File.ReadAllTextAsync(scriptPath).ConfigureAwait(false);
            int batches = 0;
            await using (var cn = new Microsoft.Data.SqlClient.SqlConnection(cs))
            {
                await cn.OpenAsync().ConfigureAwait(false);

                // 与 SQLite 分支一致：先检查结构版本，版本过高则不动数据直接退出 5
                await using (var probe = cn.CreateCommand())
                {
                    probe.CommandText =
                        "IF OBJECT_ID(N'dbo.SchemaVersion', N'U') IS NULL SELECT 0 " +
                        "ELSE SELECT ISNULL(MAX(Version), 0) FROM dbo.SchemaVersion";
                    var raw = await probe.ExecuteScalarAsync().ConfigureAwait(false);
                    var currentVersion = raw is null || raw is DBNull ? 0L : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                    if (currentVersion > 2)
                    {
                        Console.Error.WriteLine($"结构不兼容：库中版本 {currentVersion} 高于本程序支持版本 2，已停止且未修改数据。");
                        return ExitSchema;
                    }
                }

                foreach (var batch in text.Split(new[] { "\r\nGO\r\n", "\nGO\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (batch.Trim().Length == 0) continue;
                    await using var cmd = cn.CreateCommand();
                    cmd.CommandText = batch;
                    cmd.CommandTimeout = 120;
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    batches++;
                }
                await using var ver = cn.CreateCommand();
                ver.CommandText = "SELECT TOP 1 Version FROM dbo.SchemaVersion ORDER BY AppliedAtUtc DESC";
                var v = Convert.ToString(await ver.ExecuteScalarAsync().ConfigureAwait(false));
                Console.WriteLine($"[SQL Server] 已执行 {batches} 个批次；SchemaVersion = {v ?? "（未知）"}（重复执行不会删除已有数据）。");
            }
            return ExitOk;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (IsAuthFailure(ex))
        {
            Console.Error.WriteLine("连接/认证失败：" + ex.Message);
            return ExitConnect;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine("权限不足：" + ex.Message);
            return ExitPermission;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("执行失败：" + ex.Message);
            return ExitExecute;
        }
    }

    private static bool IsAuthFailure(Microsoft.Data.SqlClient.SqlException ex) =>
        ex.Number is 18456 or 4060 or -2 or 53 or 258;

    /// <summary>构建连接字符串：集成认证为默认；密码不落盘、不写日志。</summary>
    private static string BuildConnectionString(string server, string database, string auth, string? user = null, string? password = null)
    {
        var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            // 默认验证服务器证书；自签证书环境可用 --trust-server-certificate 显式放行
            TrustServerCertificate = _trustServerCertificate,
            ConnectTimeout = 20
        };
        if (auth == "sql")
        {
            // SQL 认证必须有账号口令：只设 IntegratedSecurity=false 会让连接必然失败，
            // 而帮助文本却把它列为可用选项。口令只能来自交互输入或环境变量，不经命令行。
            b.IntegratedSecurity = false;
            b.UserID = user ?? "";
            b.Password = password ?? "";
        }
        else b.IntegratedSecurity = true;
        return b.ConnectionString;
    }

    /// <summary>是否信任服务器证书（--trust-server-certificate 显式开启）。</summary>
    private static bool _trustServerCertificate;

    /// <summary>
    /// 取 SQL 账号口令：优先环境变量 THERMOHYGRO_SQL_PASSWORD，其次交互输入。
    /// 绝不接受命令行明文密码（命令行会进入进程列表与历史记录）。
    /// </summary>
    private static string? ReadSqlPassword()
    {
        var fromEnv = Environment.GetEnvironmentVariable("THERMOHYGRO_SQL_PASSWORD");
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        if (Console.IsInputRedirected) return null;
        Console.Write("请输入 SQL Server 账号口令（输入不回显）：");
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    private static string? FindScript(string folder, string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "database", folder, file);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        温湿度监测记录 —— 数据库初始化工具

        用法：
          ThermoHygro.Cli.exe db init --provider sqlite --file "D:\SensorData\history.db" [--dry-run]
          ThermoHygro.Cli.exe db init --provider sqlserver --server ".\SQLEXPRESS" --database "ThermoHygro" --auth windows [--create-database] [--dry-run]

        参数：
          --provider         sqlite | sqlserver（必需）
          --file             SQLite 目标文件路径（省略时用默认用户数据目录）
          --server           SQL Server 实例名
          --database         目标数据库名（默认 ThermoHygro）
          --auth             windows | sql（默认 windows；sql 需配合 --user）
          --user             SQL 认证的登录名（口令见下方“安全”）
          --create-database  允许创建数据库（建库脚本单独执行）
          --trust-server-certificate  信任服务器证书（自签证书环境才需要）
          --dry-run          只检查并展示计划，不写入任何数据

        安全：密码不得通过命令行传入。
          * 集成认证：--auth windows（默认）
          * SQL 认证：--user 指定账号，口令用环境变量 THERMOHYGRO_SQL_PASSWORD
            或在交互式终端中按提示输入（不回显）。

        退出码：
          0 成功/已是目标版本   2 参数错误   3 连接/认证失败
          4 权限不足            5 结构不兼容  6 执行失败

        说明：
          * 重复执行不会删除表或清空数据；库中结构版本高于本程序时会拒绝执行并返回 5。
          * 运行时账号与初始化账号可分离，日常运行不要求管理员权限。
        """);
    }

    /// <summary>
    /// 找不到 database/sqlite/001_init.sql 时的回退脚本。
    /// 必须与外部脚本保持等价的表结构：早期实现只建 SchemaVersion 一张表，
    /// 用户看到“初始化完成”，运行应用时却因缺表直接失败。
    /// </summary>
    private static string LoadEmbeddedSqliteSchema(Dictionary<string, string> opt)
    {
        Console.WriteLine("[SQLite] 未找到脚本文件，使用内嵌完整结构脚本（与 database/sqlite/001_init.sql 等价）。");
        return EmbeddedSqliteSchema;
    }

    private const string EmbeddedSqliteSchema = """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=FULL;
        PRAGMA foreign_keys=ON;

        CREATE TABLE IF NOT EXISTS SchemaVersion(
          Module TEXT PRIMARY KEY, Version INTEGER NOT NULL,
          ScriptDigest TEXT, AppliedAtUtc TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS Collector(
          CollectorId TEXT PRIMARY KEY, Name TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS Device(
          DeviceId TEXT PRIMARY KEY, DisplayName TEXT NOT NULL, Location TEXT,
          ProtocolId TEXT NOT NULL, ProtocolVersion TEXT, CreatedAtUtc TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS AcquisitionSession(
          SessionId TEXT PRIMARY KEY, DeviceId TEXT NOT NULL, CollectorId TEXT NOT NULL,
          StartedAtUtc TEXT NOT NULL, EndedAtUtc TEXT, ConfigSnapshot TEXT, StopReason TEXT);

        CREATE TABLE IF NOT EXISTS ThresholdVersion(
          VersionId INTEGER PRIMARY KEY, DeviceId TEXT NOT NULL,
          TemperatureLow REAL, TemperatureHigh REAL, HumidityLow REAL, HumidityHigh REAL,
          EffectiveFromUtc TEXT NOT NULL, Source TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS SampleJournal(
          SampleId INTEGER PRIMARY KEY AUTOINCREMENT, DeviceId TEXT NOT NULL,
          SessionId TEXT, SequenceNo INTEGER NOT NULL,
          TemperatureDegC REAL, TemperatureQuality INTEGER, TemperatureReason TEXT,
          HumidityPercent REAL, HumidityQuality INTEGER, HumidityReason TEXT,
          ThresholdVersionId INTEGER, AttemptTimeUtc TEXT NOT NULL,
          TimeZoneOffsetMinutes INTEGER NOT NULL, Aggregated INTEGER NOT NULL DEFAULT 0);

        CREATE TABLE IF NOT EXISTS MinuteSummary(
          SummaryId INTEGER PRIMARY KEY AUTOINCREMENT, DeviceId TEXT NOT NULL,
          WindowStartUtc TEXT NOT NULL, WindowEndUtc TEXT NOT NULL,
          ThresholdVersionId INTEGER, SessionId TEXT,
          TemperatureSum REAL, TemperatureValidCount INTEGER NOT NULL DEFAULT 0,
          TemperatureMin REAL, TemperatureMinAtUtc TEXT,
          TemperatureMax REAL, TemperatureMaxAtUtc TEXT,
          HumiditySum REAL, HumidityValidCount INTEGER NOT NULL DEFAULT 0,
          HumidityMin REAL, HumidityMinAtUtc TEXT,
          HumidityMax REAL, HumidityMaxAtUtc TEXT,
          AttemptCount INTEGER NOT NULL DEFAULT 0, MissedSlots INTEGER NOT NULL DEFAULT 0,
          TempOutOfRangeCount INTEGER NOT NULL DEFAULT 0,
          HumidityOutOfRangeCount INTEGER NOT NULL DEFAULT 0,
          TempMissing INTEGER NOT NULL DEFAULT 0, HumidityMissing INTEGER NOT NULL DEFAULT 0,
          UNIQUE(DeviceId, WindowStartUtc, ThresholdVersionId, SessionId));

        CREATE TABLE IF NOT EXISTS MonitorEvent(
          EventId INTEGER PRIMARY KEY AUTOINCREMENT, DeviceId TEXT NOT NULL,
          OccurredAtUtc TEXT NOT NULL, EventType TEXT NOT NULL, Severity TEXT,
          Context TEXT, ThresholdVersionId INTEGER);

        CREATE TABLE IF NOT EXISTS AggregateCheckpoint(
          CheckpointId INTEGER PRIMARY KEY, DeviceId TEXT,
          LastConsumedSampleId INTEGER NOT NULL DEFAULT 0,
          CurrentWindowStartUtc TEXT, TempAlarmState INTEGER, HumidityAlarmState INTEGER,
          UpdatedAtUtc TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS SyncDestination(
          DestinationId TEXT PRIMARY KEY, Kind TEXT NOT NULL, DestinationIdentity TEXT,
          Enabled INTEGER NOT NULL DEFAULT 1, CreatedAtUtc TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS Outbox(
          OutboxId INTEGER PRIMARY KEY AUTOINCREMENT, DestinationId TEXT,
          RecordType TEXT NOT NULL, RecordKey TEXT NOT NULL, Payload TEXT,
          AttemptCount INTEGER NOT NULL DEFAULT 0, Confirmed INTEGER NOT NULL DEFAULT 0,
          LastError TEXT, CreatedAtUtc TEXT NOT NULL,
          UNIQUE(DestinationId, RecordType, RecordKey));

        CREATE INDEX IF NOT EXISTS IX_Sample_Device_Time ON SampleJournal(DeviceId, AttemptTimeUtc);
        CREATE INDEX IF NOT EXISTS IX_Sample_Agg ON SampleJournal(Aggregated, SampleId);
        CREATE INDEX IF NOT EXISTS IX_Minute_Device_Time ON MinuteSummary(DeviceId, WindowStartUtc);
        CREATE INDEX IF NOT EXISTS IX_Minute_Day ON MinuteSummary(DeviceId, substr(WindowStartUtc,1,10));
        CREATE INDEX IF NOT EXISTS IX_Event_Device_Time ON MonitorEvent(DeviceId, OccurredAtUtc);
        CREATE INDEX IF NOT EXISTS IX_Outbox_Pending ON Outbox(Confirmed, DestinationId);

        INSERT INTO SchemaVersion(Module, Version, ScriptDigest, AppliedAtUtc)
        VALUES('core', 1, 'embedded', strftime('%Y-%m-%dT%H:%M:%SZ','now'))
        ON CONFLICT(Module) DO UPDATE SET Version=MAX(Version, excluded.Version),
          ScriptDigest=excluded.ScriptDigest, AppliedAtUtc=excluded.AppliedAtUtc;
        """;
}