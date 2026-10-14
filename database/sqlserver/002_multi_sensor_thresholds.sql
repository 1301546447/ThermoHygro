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
