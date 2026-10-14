-- 温湿度监测记录 / SQL Server 建库脚本 v1（独立交付）
-- 说明：建库不属于普通迁移事务，因此单独成一个文件，不包进 001_init.sql。
--
-- 使用前提：
--   * 执行账号需要 CREATE DATABASE 权限。无权限时请让管理员建库，
--     然后只用 001_init.sql 在该库中建表（运行账号不要求管理员权限）。
--   * 默认建议独立库名 ThermoHygro，不与其它业务库混用。
--
-- Navicat：在目标连接（不选库或选 master）上执行本脚本。

SET NOCOUNT ON;

DECLARE @dbName sysname = N'ThermoHygro';
DECLARE @sql nvarchar(max);

IF DB_ID(@dbName) IS NULL
BEGIN
    SET @sql = N'CREATE DATABASE ' + QUOTENAME(@dbName);
    EXEC(@sql);
    PRINT N'已创建数据库 ' + @dbName + N'。请随后在库中执行 001_init.sql 建表。';
END
ELSE
BEGIN
    PRINT N'数据库 ' + @dbName + N' 已存在，未做任何修改。请直接执行 001_init.sql。';
END