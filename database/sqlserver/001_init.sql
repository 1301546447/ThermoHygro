-- 温湿度监测记录 / SQL Server 建表及兼容升级脚本 v2
-- 目标：SQL Server 2016 及以上（含 LocalDB / Express）
-- 幂等性：可重复执行，不删表、不清空数据。
-- Navicat：先选中目标连接与数据库，再执行本脚本。
--
-- 本脚本遵守 Navicat / 命令行通用约定：
--   * 不使用 GO（GO 是 SSMS/sqlcmd 的批处理分隔符）
--   * 不使用 :setvar 等仅命令行支持的指令
--   * 所有建表语句通过动态 SQL 执行，避免"同批次编译时表不存在"的错误
--
-- 逻辑数据定义与 database/sqlite/001_init.sql 完全一致。
-- 本脚本只建表与索引，不建库；建库请单独执行 000_create_database.sql。

SET NOCOUNT ON;
-- 持久化计算列与其上的索引要求 QUOTED_IDENTIFIER ON（否则 ALTER TABLE / CREATE INDEX 报
-- "SET options have incorrect settings: 'QUOTED_IDENTIFIER'"）。
-- sqlcmd 默认 ON，但部分客户端（Navicat 等）可能为 OFF，因此显式声明。
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF OBJECT_ID(N'dbo.SchemaVersion', N'U') IS NULL
EXEC(N'CREATE TABLE dbo.SchemaVersion(
  Module NVARCHAR(64) NOT NULL PRIMARY KEY,
  Version INT NOT NULL,
  ScriptDigest NVARCHAR(128) NULL,
  AppliedAtUtc NVARCHAR(40) NOT NULL)');

IF EXISTS (SELECT 1 FROM dbo.SchemaVersion WHERE Module=N'core' AND Version>2)
    THROW 50002, N'数据库版本高于 2，不能使用旧版本初始化或降级。', 1;

IF OBJECT_ID(N'dbo.Collector', N'U') IS NULL
EXEC(N'CREATE TABLE dbo.Collector(
  CollectorId NVARCHAR(64) NOT NULL PRIMARY KEY,
  Name NVARCHAR(200) NOT NULL,
  CreatedAtUtc NVARCHAR(40) NOT NULL)');

IF OBJECT_ID(N'dbo.Device', N'U') IS NULL
EXEC(N'CREATE TABLE dbo.Device(
  DeviceId NVARCHAR(64) NOT NULL PRIMARY KEY,
  DisplayName NVARCHAR(200) NOT NULL,
  Location NVARCHAR(200) NULL,
  ProtocolId NVARCHAR(64) NOT NULL,
  ProtocolVersion NVARCHAR(64) NULL,
  CreatedAtUtc NVARCHAR(40) NOT NULL)');

IF OBJECT_ID(N'dbo.AcquisitionSession', N'U') IS NULL
EXEC(N'CREATE TABLE dbo.AcquisitionSession(
  SessionId NVARCHAR(64) NOT NULL PRIMARY KEY,
  DeviceId NVARCHAR(64) NOT NULL,
  CollectorId NVARCHAR(64) NOT NULL,
  StartedAtUtc NVARCHAR(40) NOT NULL,
  EndedAtUtc NVARCHAR(40) NULL,
  ConfigSnapshot NVARCHAR(MAX) NULL,
  StopReason NVARCHAR(200) NULL)');

IF OBJECT_ID(N'dbo.ThresholdVersion', N'U') IS NULL
EXEC(N'CREATE TABLE dbo.ThresholdVersion(
  VersionId BIGINT NOT NULL PRIMARY KEY,
  DeviceId NVARCHAR(64) NOT NULL,
  TemperatureLow FLOAT NULL,
  TemperatureHigh FLOAT NULL,
  HumidityLow FLOAT NULL,
  HumidityHigh FLOAT NULL,
  EffectiveFromUtc NVARCHAR(40) NOT NULL,
  Source NVARCHAR(200) NOT NULL)');

IF OBJECT_ID(N'dbo.SampleJournal', N'U') IS NULL
EXEC(N'CREATE TABLE dbo.SampleJournal(
  SampleId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
  LocalRecordKey BIGINT NULL,
  DeviceId NVARCHAR(64) NOT NULL,
  SessionId NVARCHAR(64) NULL,
  SequenceNo BIGINT NOT NULL,
  TemperatureDegC FLOAT NULL,
  TemperatureQuality INT NULL,
  TemperatureReason NVARCHAR(200) NULL,
  HumidityPercent FLOAT NULL,
  HumidityQuality INT NULL,
  HumidityReason NVARCHAR(200) NULL,
  ThresholdVersionId BIGINT NULL,
  AttemptTimeUtc NVARCHAR(40) NOT NULL,
  TimeZoneOffsetMinutes INT NOT NULL,
  Aggregated INT NOT NULL CONSTRAINT DF_SampleJournal_Aggregated DEFAULT(0))');

IF OBJECT_ID(N'dbo.MinuteSummary', N'U') IS NULL
EXEC(N'CREATE TABLE dbo.MinuteSummary(
  SummaryId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
  DeviceId NVARCHAR(64) NOT NULL,
  WindowStartUtc NVARCHAR(40) NOT NULL,
  WindowEndUtc NVARCHAR(40) NOT NULL,
  ThresholdVersionId BIGINT NULL,
  SessionId NVARCHAR(64) NULL,
  TemperatureSum FLOAT NULL,
  TemperatureValidCount INT NOT NULL CONSTRAINT DF_MinuteSummary_TValid DEFAULT(0),
  TemperatureMin FLOAT NULL,
  TemperatureMinAtUtc NVARCHAR(40) NULL,
  TemperatureMax FLOAT NULL,
  TemperatureMaxAtUtc NVARCHAR(40) NULL,
  HumiditySum FLOAT NULL,
  HumidityValidCount INT NOT NULL CONSTRAINT DF_MinuteSummary_HValid DEFAULT(0),
  HumidityMin FLOAT NULL,
  HumidityMinAtUtc NVARCHAR(40) NULL,
  HumidityMax FLOAT NULL,
  HumidityMaxAtUtc NVARCHAR(40) NULL,
  AttemptCount INT NOT NULL CONSTRAINT DF_MinuteSummary_Attempt DEFAULT(0),
  MissedSlots INT NOT NULL CONSTRAINT DF_MinuteSummary_Missed DEFAULT(0),
  TempOutOfRangeCount INT NOT NULL CONSTRAINT DF_MinuteSummary_TOut DEFAULT(0),
  HumidityOutOfRangeCount INT NOT NULL CONSTRAINT DF_MinuteSummary_HOut DEFAULT(0),
  TempMissing INT NOT NULL CONSTRAINT DF_MinuteSummary_TMiss DEFAULT(0),
  HumidityMissing INT NOT NULL CONSTRAINT DF_MinuteSummary_HMiss DEFAULT(0))');

IF OBJECT_ID(N'dbo.MinuteSummary', N'U') IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Minute_Device_Window'
                  AND object_id = OBJECT_ID(N'dbo.MinuteSummary'))
EXEC(N'CREATE UNIQUE INDEX UX_Minute_Device_Window ON dbo.MinuteSummary
  (DeviceId, WindowStartUtc, ThresholdVersionId, SessionId)');

IF OBJECT_ID(N'dbo.MonitorEvent', N'U') IS NULL
EXEC(N'CREATE TABLE dbo.MonitorEvent(
  EventId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
  DeviceId NVARCHAR(64) NOT NULL,
  OccurredAtUtc NVARCHAR(40) NOT NULL,
  EventType NVARCHAR(64) NOT NULL,
  Severity NVARCHAR(40) NULL,
  Context NVARCHAR(MAX) NULL,
  ThresholdVersionId BIGINT NULL)');

-- 兼容旧库：早期版本的 SampleJournal 没有 LocalRecordKey 列（只有自增主键 SampleId），
-- 而下游同步幂等键与唯一索引都依赖它。这里先补列，再建索引，保证旧库可原地升级。
-- （幂等：列已存在则跳过。NVARCHAR 建表时写作 BIGINT，此处保持一致。）
IF OBJECT_ID(N'dbo.SampleJournal', N'U') IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE name = N'LocalRecordKey'
                  AND object_id = OBJECT_ID(N'dbo.SampleJournal'))
EXEC(N'ALTER TABLE dbo.SampleJournal ADD LocalRecordKey BIGINT NULL');

-- 同步幂等键（LocalRecordKey）：允许重复同步时按本地记录号跳过，不产生重复行
IF OBJECT_ID(N'dbo.SampleJournal', N'U') IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Sample_LocalKey'
                  AND object_id = OBJECT_ID(N'dbo.SampleJournal'))
EXEC(N'CREATE UNIQUE INDEX UX_Sample_LocalKey ON dbo.SampleJournal(LocalRecordKey)
  WHERE LocalRecordKey IS NOT NULL');

IF OBJECT_ID(N'dbo.AggregateCheckpoint', N'U') IS NULL
EXEC(N'CREATE TABLE dbo.AggregateCheckpoint(
  CheckpointId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
  DeviceId NVARCHAR(64) NULL,
  LastConsumedSampleId BIGINT NOT NULL CONSTRAINT DF_Checkpoint_Last DEFAULT(0),
  CurrentWindowStartUtc NVARCHAR(40) NULL,
  TempAlarmState INT NULL,
  HumidityAlarmState INT NULL,
  UpdatedAtUtc NVARCHAR(40) NOT NULL)');

IF OBJECT_ID(N'dbo.SyncDestination', N'U') IS NULL
EXEC(N'CREATE TABLE dbo.SyncDestination(
  DestinationId NVARCHAR(64) NOT NULL PRIMARY KEY,
  Kind NVARCHAR(64) NOT NULL,
  DestinationIdentity NVARCHAR(200) NULL,
  Enabled INT NOT NULL CONSTRAINT DF_SyncDest_Enabled DEFAULT(1),
  CreatedAtUtc NVARCHAR(40) NOT NULL)');

IF OBJECT_ID(N'dbo.Outbox', N'U') IS NULL
EXEC(N'CREATE TABLE dbo.Outbox(
  OutboxId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
  DestinationId NVARCHAR(64) NULL,
  RecordType NVARCHAR(64) NOT NULL,
  RecordKey NVARCHAR(128) NOT NULL,
  Payload NVARCHAR(MAX) NULL,
  AttemptCount INT NOT NULL CONSTRAINT DF_Outbox_Attempt DEFAULT(0),
  Confirmed INT NOT NULL CONSTRAINT DF_Outbox_Confirmed DEFAULT(0),
  LastError NVARCHAR(400) NULL,
  CreatedAtUtc NVARCHAR(40) NOT NULL)');

IF OBJECT_ID(N'dbo.Outbox', N'U') IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Outbox_Dedup'
                  AND object_id = OBJECT_ID(N'dbo.Outbox'))
EXEC(N'CREATE UNIQUE INDEX UX_Outbox_Dedup ON dbo.Outbox
  (DestinationId, RecordType, RecordKey)');

-- 查询索引：按需创建，避免重复
IF OBJECT_ID(N'dbo.SampleJournal', N'U') IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Sample_Device_Time'
                  AND object_id = OBJECT_ID(N'dbo.SampleJournal'))
EXEC(N'CREATE INDEX IX_Sample_Device_Time ON dbo.SampleJournal(DeviceId, AttemptTimeUtc)');

IF OBJECT_ID(N'dbo.SampleJournal', N'U') IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Sample_Agg'
                  AND object_id = OBJECT_ID(N'dbo.SampleJournal'))
EXEC(N'CREATE INDEX IX_Sample_Agg ON dbo.SampleJournal(Aggregated, SampleId)');

IF OBJECT_ID(N'dbo.MinuteSummary', N'U') IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Minute_Device_Time'
                  AND object_id = OBJECT_ID(N'dbo.MinuteSummary'))
EXEC(N'CREATE INDEX IX_Minute_Device_Time ON dbo.MinuteSummary(DeviceId, WindowStartUtc)');

-- 日级索引：供五年等长区间按日分组使用。
-- 与 SQLite 侧同理：按日分组的查询若只能走时间索引，需额外的排序/散列聚合；
-- 此处用持久化计算列（前 10 位即 yyyy-MM-dd）建索引，使按日分组可直接有序扫描。
-- 计算列与索引均幂等（列/索引已存在则跳过），可重复执行。
IF OBJECT_ID(N'dbo.MinuteSummary', N'U') IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE name = N'WindowStartDay'
                  AND object_id = OBJECT_ID(N'dbo.MinuteSummary'))
EXEC(N'ALTER TABLE dbo.MinuteSummary ADD WindowStartDay AS (LEFT(WindowStartUtc,10)) PERSISTED');

IF OBJECT_ID(N'dbo.MinuteSummary', N'U') IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Minute_Day'
                  AND object_id = OBJECT_ID(N'dbo.MinuteSummary'))
EXEC(N'CREATE INDEX IX_Minute_Day ON dbo.MinuteSummary(DeviceId, WindowStartDay)');

IF OBJECT_ID(N'dbo.MonitorEvent', N'U') IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Event_Device_Time'
                  AND object_id = OBJECT_ID(N'dbo.MonitorEvent'))
EXEC(N'CREATE INDEX IX_Event_Device_Time ON dbo.MonitorEvent(DeviceId, OccurredAtUtc)');

IF OBJECT_ID(N'dbo.Outbox', N'U') IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Outbox_Pending'
                  AND object_id = OBJECT_ID(N'dbo.Outbox'))
EXEC(N'CREATE INDEX IX_Outbox_Pending ON dbo.Outbox(Confirmed, DestinationId)');

-- 写入结构版本（重复执行只更新摘要与应用时间）
-- 版本号只升不降：用更高版本程序建过的库不能被本脚本改回 1，
-- 否则 CLI 的“结构不兼容”退出码（5）永远无法触发。
IF EXISTS (SELECT 1 FROM dbo.SchemaVersion WHERE Module = N'core')
  UPDATE dbo.SchemaVersion
    SET Version = CASE WHEN Version > 1 THEN Version ELSE 1 END,
        ScriptDigest = N'sqlserver-001',
        AppliedAtUtc = CONVERT(NVARCHAR(40), SYSUTCDATETIME(), 126) + N'Z'
  WHERE Module = N'core';
ELSE
  INSERT INTO dbo.SchemaVersion(Module, Version, ScriptDigest, AppliedAtUtc)
  VALUES(N'core', 1, N'sqlserver-001', CONVERT(NVARCHAR(40), SYSUTCDATETIME(), 126) + N'Z');

PRINT N'001_init.sql 执行完成：表与索引已就绪（重复执行不会删除已有数据）。';
-- 0.4 → 0.5：多传感器使用独立的 64 位阈值版本编号。
-- Navicat：选中既有 ThermoHygro 数据库，备份后执行整个脚本。
-- 可重复执行；不删除表、不清空数据；不使用客户端专用的批次分隔指令。
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

IF OBJECT_ID(N'dbo.SchemaVersion', N'U') IS NULL
    THROW 50001, N'未初始化的数据库，请先执行 001_init.sql。', 1;
IF EXISTS (SELECT 1 FROM dbo.SchemaVersion WHERE Module=N'core' AND Version>2)
    THROW 50002, N'数据库版本高于 2，请使用对应版本程序，不能降级。', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    -- 主键使用固定长度摘要；可读的服务器/数据库名称单独保存，允许较长名称。
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.SyncDestination') AND name=N'DestinationIdentity' AND max_length<2048)
        EXEC(N'ALTER TABLE dbo.SyncDestination ALTER COLUMN DestinationIdentity NVARCHAR(1024) NULL');
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.ThresholdVersion') AND name=N'VersionId' AND system_type_id=56)
    BEGIN
        DECLARE @thresholdPk sysname;
        DECLARE @thresholdAlter nvarchar(max);
        SELECT @thresholdPk=name FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.ThresholdVersion') AND type=N'PK';
        IF @thresholdPk IS NOT NULL
        BEGIN
            SET @thresholdAlter=N'ALTER TABLE dbo.ThresholdVersion DROP CONSTRAINT ' + QUOTENAME(@thresholdPk);
            EXEC sys.sp_executesql @thresholdAlter;
        END;
        EXEC(N'ALTER TABLE dbo.ThresholdVersion ALTER COLUMN VersionId BIGINT NOT NULL');
        SET @thresholdPk=COALESCE(@thresholdPk,N'PK_ThresholdVersion');
        SET @thresholdAlter=N'ALTER TABLE dbo.ThresholdVersion ADD CONSTRAINT ' + QUOTENAME(@thresholdPk) + N' PRIMARY KEY(VersionId)';
        EXEC sys.sp_executesql @thresholdAlter;
    END;
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.SampleJournal') AND name=N'ThresholdVersionId' AND system_type_id=56)
        EXEC(N'ALTER TABLE dbo.SampleJournal ALTER COLUMN ThresholdVersionId BIGINT NULL');
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.MinuteSummary') AND name=N'ThresholdVersionId' AND system_type_id=56)
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.MinuteSummary') AND name=N'UX_Minute_Device_Window')
            EXEC(N'DROP INDEX UX_Minute_Device_Window ON dbo.MinuteSummary');
        EXEC(N'ALTER TABLE dbo.MinuteSummary ALTER COLUMN ThresholdVersionId BIGINT NULL');
        EXEC(N'CREATE UNIQUE INDEX UX_Minute_Device_Window ON dbo.MinuteSummary(DeviceId,WindowStartUtc,ThresholdVersionId,SessionId)');
    END;
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.MonitorEvent') AND name=N'ThresholdVersionId' AND system_type_id=56)
        EXEC(N'ALTER TABLE dbo.MonitorEvent ALTER COLUMN ThresholdVersionId BIGINT NULL');
    UPDATE dbo.SchemaVersion SET Version=2,ScriptDigest=N'multi-sensor-002',
        AppliedAtUtc=CONVERT(NVARCHAR(40),SYSUTCDATETIME(),126)+N'Z' WHERE Module=N'core';
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
PRINT N'多传感器阈值版本升级完成，原数据已保留。';
