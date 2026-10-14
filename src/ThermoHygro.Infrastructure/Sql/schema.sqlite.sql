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
-- 日级表达式索引：供五年等长区间按日分组使用。
-- 262.8 万行实测：无此索引时五年日汇总需临时 B-TREE 分组，耗时 6.5～8.5 秒（超过 5 秒目标）；
-- 有此索引时可按 (DeviceId, 日期) 有序扫描直接分组，实测 1.4～1.7 秒。
-- 查询端在每日粒度时需用 INDEXED BY IX_Minute_Day 指定（优化器在未指定时仍会误选时间索引）。
CREATE INDEX IF NOT EXISTS IX_Minute_Day ON MinuteSummary(DeviceId, substr(WindowStartUtc,1,10));
CREATE INDEX IF NOT EXISTS IX_Event_Device_Time ON MonitorEvent(DeviceId, OccurredAtUtc);
CREATE INDEX IF NOT EXISTS IX_Outbox_Pending ON Outbox(Confirmed, DestinationId);

-- 旧库补列（幂等）：确保运行期创建的老库也能获得后续版本新增的列。
-- SQLite 无 ADD COLUMN IF NOT EXISTS，只能查询 pragma 后再决定是否执行。