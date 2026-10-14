# 温湿度监测记录 ThermoHygro

独立的 Windows 温湿度采集和报表工具。当前版本 **0.5.0**，支持 ECM-30 传感器、本机数据库、多个传感器名称和地址维护、历史报表及桌面悬浮显示。

采用 MIT 开源许可证。中文报表字体另按 SIL 开放字体许可证分发；第三方组件见 [许可证说明](docs/第三方许可证说明.md)。

## 使用

1. 解压 `ThermoHygro-0.5.0-win-x64.zip` 到固定目录，双击 `ThermoHygro.exe`。程序包含所需运行环境，无需安装开发工具、数据库服务器或中文报表字体。
2. 首次向导设置一个传感器，选择串口、波特率、实物地址并测试读取。默认本机保存即可独立记录。
3. 在“设备与串口”增加更多传感器，填写各自名称、地址、位置和上下限，勾选启用后保存。
4. 点击“开始记录”采集全部已启用的传感器。切换查看对象不会停止其他传感器。
5. 在“历史报表”选择传感器和日期范围，查询或导出 CSV、PDF。关闭主窗口后程序留在托盘，选择“退出”才停止采集。

完整操作和升级步骤见 [用户手册](docs/用户手册.md)、[0.5.0 更新说明](docs/0.5.0更新说明.md)。

## 多传感器

| 场景 | 设置方式 |
|---|---|
| 同一串口接多台传感器 | 共用波特率、数据位、校验位和停止位，各传感器使用不同地址 |
| 不同串口接不同位置 | 每个传感器单独选择串口，不同串口可以使用相同地址 |
| 修改名称、位置或地址 | 保存后应用，固定编号不变，历史继续归属原传感器 |
| 更换实物传感器 | 点击“更换实物，建立新编号”，原历史仍在历史设备中 |
| 暂停某个点位 | 取消启用并保存，其他传感器继续记录 |
| 移除配置 | 只移除采集配置，原历史仍可查询 |

软件选择要读取的地址，不负责修改实物地址。传感器地址需要事先用厂家的设置方式配置。串口收发会按顺序等待；多个点位或较长超时会使实际采集周期变长。

## 功能

- 各传感器独立记录温度、湿度、有效次数、极值、缺测和超限事件；读不到的数值保持空值。
- 修改上下限产生新版本，原记录仍按采集时的上下限保留。
- 本机 SQLite 数据库自动初始化，支持在线备份、校验和恢复，不依赖一体机或业务数据库。
- 按传感器查询今天、本月、近 7/30 天或自定义日期；分钟、小时、日汇总自动选择合适粒度。
- CSV 导出适合中文 Excel；PDF 包含中文文字、统计、曲线、单点及缺测说明。
- 托盘运行、透明悬浮显示、登录 Windows 启动及启动后自动采集。自动启动默认关闭，用户自行开启。
- 可选 SQL Server 同步，本机先保存，连接恢复后补传。数据库密码不写入明文配置，使用 SQL 密码登录时需在重启后重新填写密码。

配置、数据、日志、报表和备份默认位于 `%LocalAppData%\ThermoHygro`，更新程序目录不会清除这些文件。高级使用者可通过环境变量 `THERMOHYGRO_HOME` 设置独立数据目录。

## 开发和验证

源码需要 Windows x64 和 .NET 10 SDK（软件开发工具包）。

```powershell
dotnet restore ThermoHygro.sln
dotnet build ThermoHygro.sln -c Release --no-restore
dotnet run --project tests/ThermoHygro.Tests -c Release --no-build
powershell -ExecutionPolicy Bypass -File scripts/Publish.ps1
```

其他验证入口：

```powershell
# 程序自检，不需要传感器
ThermoHygro.exe --self-check

# 实际窗口和按钮验证，使用隔离目录及模拟通信
ThermoHygro.exe --ui-check "D:\Temp\ThermoHygroUiVerification"

# 必须先有一对虚拟串口；对端为模拟器
ThermoHygro.exe --serial-check COM20 COM21 multi

# 只在测试用 SQL Server 上运行：创建并删除本次独占临时库
dotnet run --project tests/ThermoHygro.Tests -c Release -- --sqlserver
```

验证证据与边界见 [交付复核报告](docs/交付复核报告.md)。本地和模拟验证不代替实际传感器接线、现场电脑、Windows 重新登录或长期运行验收。

当前传感器型号仅支持 ECM-30。协议说明见 [ECM-30](protocols/ecm30/README.md)，扩展方式见 [贡献指南](CONTRIBUTING.md)。
