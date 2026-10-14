-- 温湿度监测记录 / SQLite 初始化脚本 v1
-- 目标：SQLite 3.x（含 WAL 支持）
-- 幂等性：全部 CREATE ... IF NOT EXISTS，可重复执行；不删表、不清空数据。
-- 执行方式（二选一）：
--   1) 图形工具：在 DB Browser for SQLite 中打开目标 .db，执行本脚本。
--   2) 命令行：ThermoHygro.Cli.exe db init --provider sqlite --file "D:\SensorData\history.db"
--
-- 逻辑数据定义与 database/sqlserver/001_init.sql 保持一致；
-- 本文件不包含 SQL Server 专有语法（无 GO、无 :setvar）。

PRAGMA journal_mode=WAL;
PRAGMA synchronous=FULL;
PRAGMA foreign_keys=ON;

-- 结构版本：阻止不受支持的高版本写入
CREATE TABLE IF NOT EXISTS SchemaVersion(
  Module TEXT PRIMARY KEY, Version INTEGER NOT NULL,
  ScriptDigest TEXT, AppliedAtUtc TEXT NOT NULL);

-- 采集器与设备：稳定编号，改名/改串口不影响历史归属
CREATE TABLE IF NOT EXISTS Collector(
  CollectorId TEXT PRIMARY KEY, Name TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL);

CREATE TABLE IF NOT EXISTS Device(
  DeviceId TEXT PRIMARY KEY, DisplayName TEXT NOT NULL, Location TEXT,
  ProtocolId TEXT NOT NULL, ProtocolVersion TEXT, CreatedAtUtc TEXT NOT NULL);

CREATE TABLE IF NOT EXISTS AcquisitionSession(
  SessionId TEXT PRIMARY KEY, DeviceId TEXT NOT NULL, CollectorId TEXT NOT NULL,
  StartedAtUtc TEXT NOT NULL, EndedAtUtc TEXT, ConfigSnapshot TEXT, StopReason TEXT);

-- 阈值版本：不可变编号，旧历史不用当前阈值重算
CREATE TABLE IF NOT EXISTS ThresholdVersion(
  VersionId INTEGER PRIMARY KEY, DeviceId TEXT NOT NULL,
  TemperatureLow REAL, TemperatureHigh REAL, HumidityLow REAL, HumidityHigh REAL,
  EffectiveFromUtc TEXT NOT NULL, Source TEXT NOT NULL);

-- 逐次样本日志（默认保留 7 天，仅清理已完成聚合部分）
CREATE TABLE IF NOT EXISTS SampleJournal(
  SampleId INTEGER PRIMARY KEY AUTOINCREMENT, DeviceId TEXT NOT NULL,
  SessionId TEXT, SequenceNo INTEGER NOT NULL,
  TemperatureDegC REAL, TemperatureQuality INTEGER, TemperatureReason TEXT,
  HumidityPercent REAL, HumidityQuality INTEGER, HumidityReason TEXT,
  ThresholdVersionId INTEGER, AttemptTimeUtc TEXT NOT NULL,
  TimeZoneOffsetMinutes INTEGER NOT NULL, Aggregated INTEGER NOT NULL DEFAULT 0);

-- 分钟汇总：总和与有效次数并存，跨小时/日加权不再平均已舍入均值
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
-- 日级表达式索引：供五年等长区间按日分组使用。
-- 262.8 万行实测：无此索引时五年日汇总需临时 B-TREE 分组，耗时 6.5～8.5 秒（超过 5 秒目标）；
-- 有此索引时可按 (DeviceId, 日期) 有序扫描直接分组，实测 1.4～1.7 秒。
-- 查询端在每日粒度时需用 INDEXED BY IX_Minute_Day 指定（优化器在未指定时仍会误选时间索引）。
CREATE INDEX IF NOT EXISTS IX_Minute_Day ON MinuteSummary(DeviceId, substr(WindowStartUtc,1,10));
CREATE INDEX IF NOT EXISTS IX_Event_Device_Time ON MonitorEvent(DeviceId, OccurredAtUtc);
CREATE INDEX IF NOT EXISTS IX_Outbox_Pending ON Outbox(Confirmed, DestinationId);

-- 写入结构版本（重复执行只更新摘要与应用时间，不改已有数据）
-- 版本号只升不降：MAX(...) 保证用更高版本程序建过的库不会被本脚本改回 1，
-- 否则 CLI 的“结构不兼容”退出码（5）永远无法触发。
INSERT INTO SchemaVersion(Module, Version, ScriptDigest, AppliedAtUtc)
VALUES('core', 1, 'sqlite-001', strftime('%Y-%m-%dT%H:%M:%SZ','now'))
ON CONFLICT(Module) DO UPDATE SET Version=MAX(Version, excluded.Version),
  ScriptDigest=excluded.ScriptDigest, AppliedAtUtc=excluded.AppliedAtUtc;