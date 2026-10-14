using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ThermoHygro.Core;
using ThermoHygro.Infrastructure;
using ThermoHygro.Protocols;
using ThermoHygro.Reporting;

namespace ThermoHygro.App;

public partial class MainWindow : Window
{
    private readonly AppConfiguration _cfg;
    private ILocalStore? _store;
    private SqlServerRemoteSink? _remoteSink;
    private readonly MultiSensorAcquisitionService _acq;
    private readonly Ecm30Protocol _protocol = new();
    private OverlayWindow? _overlay;
    /// <summary>“取消修改”的回退基准：保存后同步更新，否则取消会退回应用启动时的旧值。</summary>
    private OverlaySettings _overlayBackup;
    private List<MinutePoint> _trend = new();
    /// <summary>逐次样本保留天数（分钟汇总长期保留）。</summary>
    private const int SampleRetentionDays = 7;
    private System.Windows.Threading.DispatcherTimer? _maintenanceTimer;

    public event Action? ExitRequested;
    public event Action? AcquireStateChanged;

    public MainWindow(AppConfiguration cfg, MultiSensorAcquisitionService? acquisition = null)
    {
        _cfg = cfg;
        ConfigStore.NormalizeSensors(_cfg);
        _overlayBackup = cfg.Overlay.Clone();
        _acq = acquisition ?? new MultiSensorAcquisitionService();
        _acq.SelectedSensorId = _cfg.SelectedSensorId;
        InitializeComponent();
        _acq.SnapshotChanged += OnSnapshot;
        InitializeStore();
        PopulateFromConfig();
        PopulateSensorSelectors();
        RefreshHistorySensors();
        StartSensorDisplayTimer();
        RefreshPorts();
        RefreshDisplays();
        StartMaintenanceTimer();
        RefreshLog();
        RefreshStorageStats();
        Closed += OnClosedEvent;

        // 关键：点 X 必须只“隐藏”而不能真正关闭窗口。
        // WPF 窗口一旦关闭就无法再 Show()（实测会抛 InvalidOperationException），
        // 而本程序采用“关闭窗口后驻留托盘继续采集”的模式，
        // 若真关闭则托盘“打开窗口”会毫无反应且异常被静默吞掉。
        Closing += (_, e) =>
        {
            if (_shuttingDown) return;   // 真正退出时放行
            e.Cancel = true;
            Hide();
            AppLog.Info("主窗口已隐藏（采集继续，应用驻留托盘）");
            // 首次隐藏时提示：否则用户以为窗口只是最小化、不知道仍在采集
            try
            {
                if (!_cfg.CloseToTrayHintShown)
                {
                    _cfg.CloseToTrayHintShown = true;
                    ConfigStore.Save(_cfg);
                    _tray?.ShowBalloon("仍在采集",
                        "窗口已隐藏，采集继续进行。可从托盘图标重新打开窗口，或选择“退出”彻底停止。");
                }
            }
            catch { }
        };
    }

    /// <summary>标记进入真正的退出流程，使 Closing 不再拦截。</summary>
    private bool _shuttingDown;

    private void InitializeStore()
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(_cfg.SqliteFilePath)
                ? ConfigStore.DefaultDatabasePath : _cfg.SqliteFilePath;
            _store = new SqliteLocalStore(path);
            _store.InitializeAsync().GetAwaiter().GetResult();
            // 关键：把本地库交给采集服务，否则采集到的样本只显示在界面上、永不入库
            _acq.AttachStore(_store);
            AppLog.Info($"本地库已就绪：{path}");
            // 事件日志同步到界面日志页
            _acq.AppLogBridge = msg => AppLog.Info(msg);
            // 若配置为 SQL Server 模式，启动远端同步（本地写入不受影响）
            if (_cfg.StorageMode == "sqlserver" && !string.IsNullOrWhiteSpace(_cfg.SqlServerConnection))
                StartRemoteSync();
        }
        catch (Exception ex)
        {
            AppLog.Error("初始化本地库失败", ex);
            StorageFeedback.Text = SqliteLocalStore.DescribeStorageError(ex);
        }
    }

    /// <summary>每日维护：清理超期逐次样本（分钟汇总不删）。失败不影响采集。</summary>
    private void StartMaintenanceTimer()
    {
        _maintenanceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromHours(6)
        };
        _maintenanceTimer.Tick += (_, _) => RunMaintenance();
        _maintenanceTimer.Start();
        RunMaintenance();
    }

    private void RunMaintenance()
    {
        if (_store is not SqliteLocalStore s) return;
        try
        {
            var deleted = s.CleanupSamplesAsync(SampleRetentionDays).GetAwaiter().GetResult();
            if (deleted > 0)
            {
                var ev = EventDetector.CleanupEvent(_cfg.Device.DeviceId, deleted, SampleRetentionDays);
                s.AppendEventsAsync(new[] { ev }).GetAwaiter().GetResult();
                AppLog.Info($"清理：删除 {deleted} 条超过 {SampleRetentionDays} 天的逐次样本（分钟汇总保留）。");
            }
        }
        catch (Exception ex) { AppLog.Error("样本清理失败（不影响采集）", ex); }
    }

    /// <summary>启动 SQL Server 远端同步（Outbox）：本地先存，断网不影响本地。</summary>
    private void StartRemoteSync()
    {
        StopRemoteSync();
        var dest = SqlServerRemoteSink.DeriveIdentity(_cfg.SqlServerConnection);
        _remoteSink = new SqlServerRemoteSink(
            (_store as SqliteLocalStore)!.ConnectionString,
            new RemoteSyncOptions { ConnectionString = _cfg.SqlServerConnection, DestinationIdentity = dest });
        _remoteSink.Start();
        _acq.AttachRemote(_remoteSink);
        AppLog.Info($"远端同步已启动（目标：{dest}；密码不写入配置与日志）。");
    }

    private void StopRemoteSync()
    {
        try { _remoteSink?.StopAsync().GetAwaiter().GetResult(); } catch { }
        try { _remoteSink?.Dispose(); } catch { }
        _remoteSink = null;
        _acq.AttachRemote(null);
    }

    private void PopulateFromConfig()
    {
        var s = _cfg.Serial;
        PortCombo.Text = s.PortName;
        SelectComboItem(BaudCombo, s.BaudRate.ToString());
        SelectComboItem(DataBitsCombo, s.DataBits.ToString());
        SelectComboItem(ParityCombo, s.Parity);
        SelectComboItem(StopBitsCombo, s.StopBits);
        SlaveAddrBox.Text = s.SlaveAddress.ToString();
        TimeoutBox.Text = s.ReadTimeoutMs.ToString();
        IntervalBox.Text = s.PollIntervalSeconds.ToString();

        DeviceNameBox.Text = _cfg.Device.DisplayName;
        LocationBox.Text = _cfg.Device.Location;
        ModelText.Text = $"型号：{_protocol.Describe()} · 实物验证状态：{_protocol.VerificationStatus}"
            + "（同为 Modbus 并不意味着寄存器一致，本版本仅支持 ECM-30）";
        DeviceIdText.Text = $"设备编号（稳定，不随名称/串口/电脑名变化）：{_cfg.Device.DeviceId}";

        ModeSqlite.IsChecked = _cfg.StorageMode != "sqlserver";
        ModeSqlServer.IsChecked = _cfg.StorageMode == "sqlserver";
        SqlitePathBox.Text = _cfg.SqliteFilePath;
        SqlServerBox.Text = _cfg.SqlServerConnection;

        var o = _cfg.Overlay;
        OverlayEnabled.IsChecked = o.Enabled;
        OverlayShowTemp.IsChecked = o.ShowTemperature;
        OverlayShowHum.IsChecked = o.ShowHumidity;
        OverlayShowDevice.IsChecked = o.ShowDeviceLabel;
        OverlayShowStatus.IsChecked = o.ShowStatusText;
        OverlayShadow.IsChecked = o.ShadowEnabled;
        OverlayClickThrough.IsChecked = o.ClickThrough;
        SelectComboItemByTag(CornerCombo, o.Corner);
        MarginXBox.Text = o.MarginX.ToString("F0");
        MarginYBox.Text = o.MarginY.ToString("F0");
        FontSizeSlider.Value = Math.Clamp(o.FontSize, 16, 96);
        TempColorBox.Text = o.TemperatureColor;
        HumColorBox.Text = o.HumidityColor;
        TextColorBox.Text = o.TextColor;
        BgOpacityBox.Text = o.BackgroundOpacity.ToString("0.##", CultureInfo.InvariantCulture);
        TextOpacitySlider.Value = Math.Clamp(o.TextOpacity, 0.15, 1);
        UpdateFontSizeLabel();
        UpdateTextOpacityLabel();

        AutoStartAcq.IsChecked = _cfg.AutoStartAcquisition;
        StartWithWindows.IsChecked = _cfg.StartWithWindows;

        TempCardValue.Text = "-- ℃";
        HumCardValue.Text = "-- %RH";
        HeaderStatus.Text = "未开始采集";
        HelpText2.Text = $"协议与默认值：{_protocol.Describe()}；温度寄存器 0x0100、湿度寄存器 0x0105，"
            + "各 1 个寄存器；有符号 16 位大端 × 0.1。本软件不做自动识别物理设备，也不使用模拟数据冒充真实读数。";
    }
}// 串口与设备相关操作

public partial class MainWindow
{
    private static void SelectComboItem(ComboBox box, string text)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString() ?? item.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private static void SelectComboItemByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private static string ComboValue(ComboBox box, string fallback)
    {
        if (box.SelectedItem is ComboBoxItem it) return it.Tag?.ToString() ?? it.Content?.ToString() ?? fallback;
        return string.IsNullOrWhiteSpace(box.Text) ? fallback : box.Text.Trim();
    }

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        var names = SerialTransport.GetPortNames();
        var current = PortCombo.Text;
        PortCombo.Items.Clear();
        foreach (var n in names) PortCombo.Items.Add(n);
        if (!string.IsNullOrWhiteSpace(current)) PortCombo.Text = current;
        PortHint.Text = names.Length == 0
            ? "未检测到串口。请检查 USB 转 RS-485 驱动与接线，或手动输入端口名（例如 COM12）。"
            : $"检测到 {names.Length} 个串口：{string.Join("、", names)}。可手动输入列表外的端口名（例如 COM12）。";
    }

    private SerialSettings ReadSerialFromUi() => new()
    {
        PortName = PortCombo.Text?.Trim() ?? "",
        BaudRate = ParseInt(ComboValue(BaudCombo, "9600"), 9600),
        DataBits = ParseInt(ComboValue(DataBitsCombo, "8"), 8),
        Parity = ComboValue(ParityCombo, "None"),
        StopBits = ComboValue(StopBitsCombo, "One"),
        SlaveAddress = ParseInt(SlaveAddrBox.Text, 0),
        ReadTimeoutMs = ParseInt(TimeoutBox.Text, 0),
        PollIntervalSeconds = ParseInt(IntervalBox.Text, 0)
    };

    /// <summary>校验串口参数，返回错误说明（空 = 通过）。</summary>
    private static string ValidateSerial(SerialSettings s) => SensorConfigurationRules.ValidateSerial(s);

    private void SaveSerial_Click(object sender, RoutedEventArgs e)
    {
        var error = SaveSelectedSensorFromUi();
        SerialFeedback.Text = error.Length > 0 ? error : "当前传感器的名称、地址、串口和启用状态已保存。";
        BtnRestoreSerial.Visibility = _previousSensors is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CancelSerial_Click(object sender, RoutedEventArgs e)
    {
        PopulateSerialOnly();
        SerialFeedback.Text = "已取消修改，参数还原为已保存的配置（未改动原配置）。";
    }

    private void PopulateSerialOnly()
    {
        var s = _cfg.Serial;
        PortCombo.Text = s.PortName;
        SelectComboItem(BaudCombo, s.BaudRate.ToString());
        SelectComboItem(DataBitsCombo, s.DataBits.ToString());
        SelectComboItem(ParityCombo, s.Parity);
        SelectComboItem(StopBitsCombo, s.StopBits);
        SlaveAddrBox.Text = s.SlaveAddress.ToString();
        TimeoutBox.Text = s.ReadTimeoutMs.ToString();
        IntervalBox.Text = s.PollIntervalSeconds.ToString();
    }

    private void RestoreSerial_Click(object sender, RoutedEventArgs e)
    {
        if (_previousSensors is null) { SerialFeedback.Text = "没有可恢复的设置。"; return; }
        var previous = _previousSensors.Select(sensor => sensor.Clone()).ToList();
        var selected = _previousSelectedId;
        var error = CommitSensorList(previous, selected);
        SerialFeedback.Text = error.Length > 0 ? error : "已恢复上一份传感器配置，并同步更新保存文件和采集参数。";
    }

    private void TestRead_Click(object sender, RoutedEventArgs e)
    {
        var s = ReadSerialFromUi();
        var err = ValidateSerial(s);
        if (err.Length > 0) { TestResultText.Text = "❌ " + err; return; }

        if (_acq.IsRunning)
        {
            TestResultText.Text = "采集运行中，请先暂停采集再进行测试读取（避免两个请求交错）。";
            return;
        }

        TestResultText.Text = "正在测试读取（分别请求温度与湿度）…";
        try
        {
            var g = _acq.TestReadAsync(s, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            TestResultText.Text = DescribeTestRead(g, s);
            AppLog.Info("测试读取：" + TestResultText.Text.Replace(Environment.NewLine, " | "));
        }
        catch (Exception ex)
        {
            TestResultText.Text = "测试读取异常：" + ex.Message;
            AppLog.Error("测试读取异常", ex);
        }
    }

    /// <summary>分别展示温/湿成功失败与原因；明确"打开串口 ≠ 连接成功"。</summary>
    private static string DescribeTestRead(ReadingGroup g, SerialSettings s)
    {
        string Line(string name, MetricReading r, string unit)
        {
            if (r.Quality is MetricQuality.Good or MetricQuality.OutOfRange)
                return $"  ✅ {name}：{r.Value:F1} {unit}（{r.TimestampUtc?.ToLocalTime():HH:mm:ss}）";
            return $"  ❌ {name}：失败 —— {r.Reason ?? "未读取"}";
        }
        var ok = g.AnySuccess;
        var head = ok
            ? "测试读取结果（打开串口成功不代表已连上传感器，以下为实际读取结果）："
            : $"测试读取结果（串口 {s.PortName} 已打开，但未读取到有效数据）：";
        return head + Environment.NewLine
            + Line("温度", g.Temperature, "℃") + Environment.NewLine
            + Line("湿度", g.Humidity, "%RH");
    }
}public partial class MainWindow
{
    private void StartAcquisition_Click(object sender, RoutedEventArgs e) => StartAcquisition();

    public void StartAcquisition()
    {
        var s = ReadSerialFromUi();
        var err = ConfigStore.SelectedSensor(_cfg).Enabled ? ValidateSerial(s) : "";
        if (err.Length > 0)
        {
            ActionFeedback.Text = "❌ " + err;
            return;
        }
        _cfg.Serial = s;
        ConfigStore.CommitSelectedSensor(_cfg);
        var configurationError = SensorConfigurationRules.Validate(_cfg.Sensors);
        if (configurationError.Length > 0) { ActionFeedback.Text = configurationError; return; }
        _cfg.WizardCompleted = true;
        var saveError = ConfigStore.Save(_cfg);
        if (saveError.Length > 0) { ActionFeedback.Text = saveError; return; }

        var r = _acq.Start(_cfg.Sensors);
        if (r.Length > 0)
        {
            ActionFeedback.Text = (_acq.IsRunning ? "采集已启动，连接失败的传感器将自动重试：" : "启动失败：") + r;
            AppLog.Warn(ActionFeedback.Text);
        }
        else ActionFeedback.Text = $"已开始记录 {_cfg.Sensors.Count(sensor => sensor.Enabled)} 个传感器，保存状态见各传感器记录。";
        AppLog.Info($"开始采集：{s.Describe()}，设备 {_cfg.Device.DisplayName}");
        UpdateOverlayFreshnessWindow();
        AcquireStateChanged?.Invoke();
    }

    private void PauseAcquisition_Click(object sender, RoutedEventArgs e) => PauseAcquisition();

    public void PauseAcquisition()
    {
        _acq.Pause();
        ActionFeedback.Text = "已暂停采集（已接收样本已提交）。";
        AppLog.Info("暂停采集");
        AcquireStateChanged?.Invoke();
    }

    public bool IsAcquiring => _acq.IsRunning;

    private void OnSnapshot(AcquisitionSnapshot snap)
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshSensorOverview();
                if (snap.DeviceId == _cfg.SelectedSensorId)
                {
                    ApplySnapshot(VisibleSnapshot(snap));
                    if (snap.Sequence == 1) RefreshTrend();
                }
            }));
        }
        catch { }
    }

    private void ApplySnapshot(AcquisitionSnapshot snap)
    {
        RenderCard(TempCardValue, TempCardState, TempCardReason, snap.Reading.Temperature, "℃");
        RenderCard(HumCardValue, HumCardState, HumCardReason, snap.Reading.Humidity, "%RH");

        DeviceLine.Text = $"{_cfg.Device.DisplayName}　{(string.IsNullOrWhiteSpace(_cfg.Device.Location) ? "" : "· " + _cfg.Device.Location)}";
        ComLine.Text = $"串口：{_cfg.Serial.Describe()}　型号：{Ecm30Protocol.DisplayName}";
        var lastGood = snap.Reading.Temperature.TimestampUtc ?? snap.Reading.Humidity.TimestampUtc
            ?? snap.Reading.Temperature.LastGoodTimestampUtc ?? snap.Reading.Humidity.LastGoodTimestampUtc;
        LastGoodLine.Text = lastGood.HasValue
            ? $"最后成功读取：{lastGood.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : "尚无成功读取记录";

        StateComm.Text = DescribeCollector(snap.Collector);
        StateComm.Foreground = BrushFor(snap.Collector);
        StateLocal.Text = snap.Storage switch
        {
            StorageState.Ok => "已保存",
            StorageState.Fault => "未保存",
            _ => "未知"
        };
        StateLocal.Foreground = snap.Storage == StorageState.Ok ? OkBrush : BadBrush;
        StateRemote.Text = snap.Remote switch
        {
            RemoteSyncState.Disabled => "未启用",
            RemoteSyncState.Idle => "空闲",
            RemoteSyncState.Pending => "待同步",
            RemoteSyncState.Error => "同步失败",
            _ => "未知"
        };
        StateRemote.Foreground = snap.Remote is RemoteSyncState.Error or RemoteSyncState.Pending ? WarnBrush : NeutralBrush;

        StateDetail.Text = snap.StatusText
            + $"　|　轮次 #{snap.Sequence}　错过时隙 {snap.MissedSlots} 次"
            + (snap.Storage == StorageState.Fault && _store is not null ? $"　|　{_store.LastError}" : "");

        HeaderStatus.Text = $"{DescribeCollector(snap.Collector)} · {DeviceLine.Text}";
        ActionFeedback.Text = snap.StatusText;

        if (_overlay is not null)
        {
            _overlay.FreshnessWindow = _acq.FreshnessWindow;
            _overlay.Update(snap);
        }
    }

    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly Brush BadBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x6C, 0x00));
    private static readonly Brush NeutralBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));

    private static Brush BrushFor(CollectorState s) => s switch
    {
        CollectorState.Acquiring => OkBrush,
        CollectorState.PartiallyMissing => WarnBrush,
        CollectorState.Connecting => WarnBrush,
        CollectorState.Reconnecting => WarnBrush,
        CollectorState.Disconnected => BadBrush,
        CollectorState.StorageFault => BadBrush,
        _ => NeutralBrush
    };

    private static string DescribeCollector(CollectorState s) => s switch
    {
        CollectorState.NotConfigured => "未配置",
        CollectorState.Disconnected => "未连接",
        CollectorState.Connecting => "连接中",
        CollectorState.Acquiring => "采集中",
        CollectorState.PartiallyMissing => "部分指标缺测",
        CollectorState.Reconnecting => "重连中",
        CollectorState.Paused => "已暂停",
        CollectorState.StorageFault => "存储故障",
        _ => "未知"
    };

    /// <summary>渲染读数卡片：失败不沿用旧值冒充本次值；过期显示 --。</summary>
    private static void RenderCard(TextBlock value, TextBlock state, TextBlock reason, MetricReading r, string unit)
    {
        switch (r.Quality)
        {
            case MetricQuality.Good:
                value.Text = $"{r.Value:F1} {unit}";
                value.Foreground = OkBrush;
                state.Text = $"读取成功 · {r.TimestampUtc?.ToLocalTime():HH:mm:ss}";
                reason.Text = "";
                break;
            case MetricQuality.OutOfRange:
                value.Text = $"{r.Value:F1} {unit}";
                value.Foreground = WarnBrush;
                state.Text = "读取成功但超出已配置上下限（属指标状态，不是通信失败）";
                reason.Text = "";
                break;
            case MetricQuality.Failed:
                value.Text = $"-- {unit}";
                value.Foreground = BadBrush;
                state.Text = "本次读取失败（未沿用旧值充当本次值）";
                reason.Text = r.Reason ?? "";
                break;
            case MetricQuality.Stale:
                value.Text = $"-- {unit}";
                value.Foreground = NeutralBrush;
                state.Text = "读数已过期";
                reason.Text = r.LastGoodValue.HasValue
                    ? $"上次有效值 {r.LastGoodValue:F1} {unit}（{r.LastGoodTimestampUtc?.ToLocalTime():HH:mm:ss}）"
                    : (r.Reason ?? "");
                break;
            case MetricQuality.NotSupported:
                value.Text = $"-- {unit}";
                value.Foreground = NeutralBrush;
                state.Text = "协议未提供此项指标";
                reason.Text = "";
                break;
            default:
                value.Text = $"-- {unit}";
                value.Foreground = NeutralBrush;
                state.Text = "尚未读取";
                reason.Text = "";
                break;
        }
    }
}public partial class MainWindow
{
    private void RefreshTrend_Click(object sender, RoutedEventArgs e) => RefreshTrend();

    public void RefreshTrend()
    {
        if (_store is null) { TrendLegend.Text = "本地库未就绪，无法查询趋势。"; return; }
        try
        {
            _trend = ((SqliteLocalStore)_store).QueryRecentMinutesForDeviceAsync(60, _cfg.SelectedSensorId).GetAwaiter().GetResult();
            DrawTrend();
        }
        catch (Exception ex)
        {
            TrendLegend.Text = "趋势查询失败：" + ex.Message;
        }
    }

    /// <summary>绘制近 1 小时趋势：温湿上下两条独立曲线，缺测断开不伪造。</summary>
    private void DrawTrend()
    {
        TrendCanvas.Children.Clear();
        double w = TrendCanvas.ActualWidth <= 0 ? 900 : TrendCanvas.ActualWidth;
        double h = TrendCanvas.ActualHeight <= 0 ? 170 : TrendCanvas.ActualHeight;
        double pad = 34;

        if (_trend.Count == 0)
        {
            TrendLegend.Text = "近 1 小时内暂无分钟汇总数据（尚未采集，或采集刚开始）。";
            return;
        }

        var tVals = _trend.Where(p => p.TemperatureAvg.HasValue).Select(p => p.TemperatureAvg!.Value).ToList();
        var hVals = _trend.Where(p => p.HumidityAvg.HasValue).Select(p => p.HumidityAvg!.Value).ToList();

        var grid = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
        for (int i = 0; i <= 4; i++)
        {
            double y = pad + (h - 2 * pad) * i / 4.0;
            TrendCanvas.Children.Add(new Line
            {
                X1 = pad, Y1 = y, X2 = w - 8, Y2 = y,
                Stroke = grid, StrokeThickness = 1
            });
        }

        // 温度曲线（上半区）
        DrawSeries(tVals, _trend.Select(p => p.TemperatureAvg), pad, 6, h / 2 - 6, Color.FromRgb(0xE5, 0x39, 0x35));
        // 湿度曲线（下半区）
        DrawSeries(hVals, _trend.Select(p => p.HumidityAvg), pad, h / 2 + 4, h / 2 + 14, Color.FromRgb(0x1E, 0x88, 0xE5));

        var first = _trend.First().MinuteStartUtc.ToLocalTime();
        var last = _trend.Last().MinuteStartUtc.ToLocalTime();
        TrendCanvas.Children.Add(new TextBlock
        {
            Text = first.ToString("HH:mm"), FontSize = 11,
            Foreground = NeutralBrush
        });
        TrendLegend.Text = $"区间 {first:MM-dd HH:mm} ～ {last:MM-dd HH:mm}（本地时间）｜分钟点 {_trend.Count} 个"
            + $"｜温度有效均值 {tVals.Count} 点" + (tVals.Count > 0 ? $"，范围 {tVals.Min():F1}～{tVals.Max():F1} ℃" : "")
            + $"｜湿度有效均值 {hVals.Count} 点" + (hVals.Count > 0 ? $"，范围 {hVals.Min():F1}～{hVals.Max():F1} %RH" : "")
            + "｜缺测分钟不连线，不伪造样本";
    }

    private void DrawSeries(List<double> values, IEnumerable<double?> series, double pad, double top, double height, Color color)
    {
        if (values.Count == 0) return;
        double w = TrendCanvas.ActualWidth <= 0 ? 900 : TrendCanvas.ActualWidth;
        double min = values.Min(), max = values.Max();
        if (Math.Abs(max - min) < 0.001) { max += 0.5; min -= 0.5; }

        var pts = series.ToList();
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        };

        for (int i = 0; i < pts.Count; i++)
        {
            if (i > 0 && _trend[i].MinuteStartUtc - _trend[i - 1].MinuteStartUtc > TimeSpan.FromMinutes(1))
            {
                if (line.Points.Count > 0) TrendCanvas.Children.Add(line);
                line = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            }
            var duration = _trend[^1].MinuteStartUtc - _trend[0].MinuteStartUtc;
            double fraction = duration <= TimeSpan.Zero ? 0.5 : (_trend[i].MinuteStartUtc - _trend[0].MinuteStartUtc).TotalSeconds / duration.TotalSeconds;
            double x = pad + (w - pad - 8) * fraction;
            if (!pts[i].HasValue)
            {
                // 缺测：断线，不用 0 或上一值补齐
                if (line.Points.Count > 0)
                {
                    TrendCanvas.Children.Add(line);
                    line = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
                }
                continue;
            }
            double ratio = (pts[i]!.Value - min) / (max - min);
            double y = top + height - ratio * height;
            line.Points.Add(new Point(x, y));
            var point = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(color) };
            Canvas.SetLeft(point, x - 2.5); Canvas.SetTop(point, y - 2.5);
            TrendCanvas.Children.Add(point);
        }
        if (line.Points.Count > 0) TrendCanvas.Children.Add(line);
    }

    /// <summary>当前报表查询（屏幕与导出共用同一份快照，保证三处一致）。</summary>
    private ReportSnapshot? _snapshot;

    private async void QueryHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null) { HistorySummary.Text = "本地库未就绪。"; return; }
        if (_busy) { HistorySummary.Text = "上一次操作还在进行中，请稍候。"; return; }
        _busy = true;
        try
        {
            HistorySummary.Text = "正在查询…";
            // 在后台线程查询并渲染：大区间（数百万行）时避免界面假死
            var s = await TakeSnapshotAsync();
            _snapshot = s;
            RefreshHistoryView(s);
            AppLog.Info($"历史查询：{s.Query.GranularityText}，{s.Points.Count} 个分桶。");
        }
        catch (Exception ex)
        {
            HistorySummary.Text = "查询失败：" + ex.Message;
        }
        finally { _busy = false; }
    }

    /// <summary>防止导出/备份按钮被连点造成并发写入同一文件。</summary>
    private bool _busy;

    /// <summary>把快照渲染到历史页（查询与导出共用，保证屏幕与导出同一份数据）。</summary>
    private void RefreshHistoryView(ReportSnapshot s)
    {
            var view = s.Points.Select(p => new
            {
                分桶开始 = p.FormatLocal(s.Query.Granularity == ReportGranularity.Minute ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd HH:mm"),
                粒度 = s.Query.GranularityText,
                温度均值 = p.TemperatureAvg.HasValue ? p.TemperatureAvg.Value.ToString("F1") : "缺测",
                温度最低 = p.TemperatureMin.HasValue ? p.TemperatureMin.Value.ToString("F1") : "",
                温度最高 = p.TemperatureMax.HasValue ? p.TemperatureMax.Value.ToString("F1") : "",
                湿度均值 = p.HumidityAvg.HasValue ? p.HumidityAvg.Value.ToString("F1") : "缺测",
                温度有效次数 = p.TemperatureValidCount,
                湿度有效次数 = p.HumidityValidCount,
                超限次数 = p.TempOutOfRangeCount + p.HumidityOutOfRangeCount,
                质量 = p.QualityText
            }).ToList();
            HistoryGrid.ItemsSource = view;

            var st = s.Stats;
            HistorySummary.Text = s.Points.Count == 0
                ? $"{s.RangeText} 内没有任何记录（空结果，不伪造正常曲线）。缺测不会显示为 0。"
                : $"{s.RangeText}（{s.Query.GranularityText}）共 {s.Points.Count} 个分桶，事件 {s.Events.Count} 条。"
                  + $"\n温度：均值 {(st.TemperatureAvg.HasValue ? st.TemperatureAvg.Value.ToString("F1") : "无数据")} ℃，"
                  + $"最低 {(st.TemperatureMin.HasValue ? st.TemperatureMin.Value.ToString("F1") : "—")}，"
                  + $"最高 {(st.TemperatureMax.HasValue ? st.TemperatureMax.Value.ToString("F1") : "—")} ℃，有效 {st.TemperatureValidCount} 次"
                  + $"\n湿度：均值 {(st.HumidityAvg.HasValue ? st.HumidityAvg.Value.ToString("F1") : "无数据")} %RH，"
                  + $"最低 {(st.HumidityMin.HasValue ? st.HumidityMin.Value.ToString("F1") : "—")}，"
                  + $"最高 {(st.HumidityMax.HasValue ? st.HumidityMax.Value.ToString("F1") : "—")} %RH，有效 {st.HumidityValidCount} 次"
                  + $"\n整段缺测 {st.GapBucketCount} 个，覆盖率 {st.CoverageRatio:P1}；阈值说明：{(s.ThresholdNote.Length == 0 ? "未配置（旧记录不追溯标红）" : s.ThresholdNote)}"
                  + $"\n数据截止时间：{s.Query.CutoffUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
    }

    /// <summary>按当前选择构造查询（屏幕、CSV、PDF 都用它）。</summary>
    private Task<ReportSnapshot> TakeSnapshotAsync()
    {
        if (_store is null) throw new InvalidOperationException("本地库未就绪。");
        var zone = TimeZoneInfo.Local;
        var today = DateTime.Now.Date;
        var scope = (HistoryScopeCombo?.SelectedIndex) switch
        {
            1 => today.AddDays(-6),
            2 => today,
            3 => new DateTime(today.Year, today.Month, 1),
            _ => today.AddDays(-29)
        };
        // 所有区间都“截止到今天结束”，与所选起点无关
        var end = today.AddDays(1);
        if (HistoryScopeCombo?.SelectedIndex == 4)
        {
            scope = HistoryStartDate.SelectedDate?.Date ?? throw new InvalidOperationException("请选择报表开始日期。");
            end = (HistoryEndDate.SelectedDate?.Date ?? throw new InvalidOperationException("请选择报表结束日期。")).AddDays(1);
            if (scope >= end) throw new InvalidOperationException("开始日期不能晚于结束日期。");
        }
        var days = (end - scope).TotalDays;
        var q = new ReportQuery
        {
            DeviceId = HistorySensorCombo.SelectedValue?.ToString() ?? _cfg.SelectedSensorId,
            StartLocal = scope,
            EndLocalExclusive = end,
            Granularity = ReportQuery.GranularityFor(days),
            TimeZoneId = zone.Id,
            UtcOffsetMinutes = (int)zone.GetUtcOffset(DateTime.Now).TotalMinutes,
            // 截止点固定：屏幕与导出不混入新到达数据
            CutoffUtc = DateTimeOffset.UtcNow
        };
        var query = new SqliteHistoryQuery((_store as SqliteLocalStore)!.DatabasePath);
        // 在界面线程取得选项后再进入后台；WPF 控件不能从 Task.Run 内直接读取。
        return Task.Run(() => query.TakeSnapshotAsync(q));
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { HistorySummary.Text = "上一次操作还在进行中，请稍候（避免同时写入同一文件）。"; return; }
        _busy = true;
        try
        {
            HistorySummary.Text = "正在导出 CSV…";
            var s = await TakeSnapshotAsync();
            _snapshot = s;
            RefreshHistoryView(s);
            if (s.Points.Count == 0 && s.Events.Count == 0)
            {
                HistorySummary.Text = $"{s.RangeText} 内没有记录，已取消导出（不生成空报表，避免看起来像“一切正常”）。";
                return;
            }
            var dir = Path.Combine(ConfigStore.DefaultDirectory, "Exports");
            var result = await Task.Run(() => new CsvExporter().ExportAsync(s, dir).GetAwaiter().GetResult());
            HistorySummary.Text = result.Success
                ? $"✅ CSV 已导出到：{string.Join("；", result.Files)}"
                  + $"\n（UTF-8 带 BOM，中文 Excel 可直接打开；缺测为空值不是 0；文本字段已防公式注入；汇总与事件分表）"
                : (result.Canceled ? "已取消导出（未留下半成品）。" : "导出失败（未产生假成功文件）：" + result.Error);
            if (result.Success) AppLog.Info($"CSV 导出：{string.Join(",", result.Files)}");
        }
        catch (Exception ex)
        {
            HistorySummary.Text = "导出失败（未产生假成功文件）：" + ex.Message;
        }
        finally { _busy = false; }
    }

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { HistorySummary.Text = "上一次操作还在进行中，请稍候（避免同时写入同一文件）。"; return; }
        _busy = true;
        try
        {
            HistorySummary.Text = "正在导出 PDF…";
            var s = await TakeSnapshotAsync();
            _snapshot = s;
            RefreshHistoryView(s);
            var dir = Path.Combine(ConfigStore.DefaultDirectory, "Exports");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"温湿度报表-{DateTime.Now:yyyyMMdd-HHmmss}.pdf");
            var reporter = new PdfReporter();
            ReportFontStatus.Text = "PDF 字体：" + reporter.FontStatus;
            var result = await Task.Run(() => reporter.ExportAsync(s, file).GetAwaiter().GetResult());
            HistorySummary.Text = result.Success
                ? $"✅ PDF 已导出：{result.FilePath}（A4 横向、封面+逐月页、文字可选择复制）"
                : (result.Canceled ? "已取消导出（未留下半成品）。" : "导出失败（未产生假成功文件）：" + result.Error);
            if (result.Success) AppLog.Info("PDF 导出：" + result.FilePath);
        }
        catch (Exception ex)
        {
            HistorySummary.Text = "PDF 导出失败（未产生假成功文件）：" + ex.Message;
        }
        finally { _busy = false; }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { HistorySummary.Text = "上一次操作还在进行中，请稍候。"; return; }
        _busy = true;
        try
        {
            HistorySummary.Text = "正在在线备份…";
            var path = (_store as SqliteLocalStore)!.DatabasePath;
            var svc = new SqliteBackupService(path);
            var (entry, err) = await Task.Run(() => svc.BackupAsync().GetAwaiter().GetResult());
            if (entry is null) { HistorySummary.Text = "备份失败：" + err; return; }
            var cleanup = await Task.Run(() => svc.CleanupAsync().GetAwaiter().GetResult());
            HistorySummary.Text = $"✅ 已在线备份：{entry.FilePath}"
                + $"\n内容：{entry.Note}（使用 SQLite 在线备份接口，不漏 WAL）"
                + $"\n保留策略：{cleanup.Detail}";
            AppLog.Info("备份完成：" + entry.FilePath);
        }
        catch (Exception ex)
        {
            HistorySummary.Text = "备份失败：" + ex.Message;
        }
        finally { _busy = false; }
    }

    private async void VerifyBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { HistorySummary.Text = "上一次操作还在进行中，请稍候。"; return; }
        _busy = true;
        try
        {
            var svc = new SqliteBackupService((_store as SqliteLocalStore)!.DatabasePath);
            var list = await Task.Run(() => svc.ListAsync().GetAwaiter().GetResult());
            if (list.Count == 0) { HistorySummary.Text = "还没有备份。请先点击“备份本地库”。"; return; }
            var latest = list[0];
            var v = await Task.Run(() => svc.VerifyAsync(latest.FilePath).GetAwaiter().GetResult());
            HistorySummary.Text = v.Ok
                ? $"✅ 最新备份校验通过：{Path.GetFileName(latest.FilePath)}"
                  + $"\n样本 {v.SampleCount} / 分钟 {v.MinuteCount} / 事件 {v.EventCount} / 阈值 {v.ThresholdCount}；结构版本 {v.SchemaVersion}"
                : $"❌ 最新备份校验未通过：{v.Detail}（已有备份未被删除）";
        }
        catch (Exception ex)
        {
            HistorySummary.Text = "校验失败：" + ex.Message;
        }
        finally { _busy = false; }
    }
}

public partial class MainWindow
{
    private void StorageMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _cfg.StorageMode = ModeSqlServer.IsChecked == true ? "sqlserver" : "sqlite";
    }

    private void ApplyStorage_Click(object sender, RoutedEventArgs e)
    {
        var mode = ModeSqlServer.IsChecked == true ? "sqlserver" : "sqlite";
        var path = string.IsNullOrWhiteSpace(SqlitePathBox.Text) ? ConfigStore.DefaultDatabasePath : SqlitePathBox.Text.Trim();
        if (!Path.IsPathFullyQualified(path))
        {
            StorageFeedback.Text = "❌ 本地数据库必须是绝对路径（例如 D:\\SensorData\\history.db）。";
            return;
        }
        // 先建新库并验证可用，成功后再关闭旧库。
        // 若先 Dispose 旧库再建新库，新库失败时会导致 _store 为空（采集中断且旧库已关闭）。
        SqliteLocalStore? next;
        try
        {
            next = new SqliteLocalStore(path);
            next.InitializeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            StorageFeedback.Text = "❌ " + SqliteLocalStore.DescribeStorageError(ex)
                + "\n原数据库仍在使用，采集未受影响。";
            return;   // 不动旧库
        }

        // 新库已就绪：若路径与当前一致则无需切换；否则先停远端同步再关旧库
        var samePath = _store is SqliteLocalStore cur
            && string.Equals(cur.DatabasePath, path, StringComparison.OrdinalIgnoreCase);
        if (!samePath)
        {
            StopRemoteSync();
            (_store as IDisposable)?.Dispose();
            _store = next;
            // 采集服务持有旧库引用：不重新绑定的话，切换后样本仍写入已关闭的旧库
            _acq.AttachStore(_store);
        }
        else
        {
            (next as IDisposable)?.Dispose();   // 同一路径：保留原实例
        }

        _cfg.StorageMode = mode;
        _cfg.SqliteFilePath = path;
        _cfg.SqlServerConnection = SqlServerBox.Text?.Trim() ?? "";
        var err = ConfigStore.Save(_cfg);

        // 按新配置启动或停止远端同步（本地写入始终先行）
        if (mode == "sqlserver" && _cfg.SqlServerConnection.Length > 0 && _store is not null)
        {
            StartRemoteSync();
            var (ok, detail) = SqlServerRemoteSink.TestConnectionAsync(_cfg.SqlServerConnection).GetAwaiter().GetResult();
            var note2 = ok
                ? "已选择“同时保存到 SQL Server”，连接可用：" + detail + "。本机 SQLite 仍持续留存，数据先写本机、再向远端补传。"
                : "已选择“同时保存到 SQL Server”，但当前连接不可用：" + detail + "。本地记录不受影响，界面会如实显示同步失败而不会显示为已同步。";
            StorageFeedback.Text = err.Length > 0 ? "⚠ 存储已切换但配置保存失败：" + err : "✅ " + note2;
        }
        else
        {
            StopRemoteSync();
            var note = mode == "sqlserver"
                ? "已选择“同时保存到 SQL Server”，但连接字符串为空。本地记录正常，远端同步未启动。"
                : "已选择本机保存（推荐）。远端同步已停止。";
            StorageFeedback.Text = err.Length > 0 ? "⚠ 存储已切换但配置保存失败：" + err : "✅ " + note;
        }
        RefreshStorageStats();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(_cfg.SqliteFilePath);
        if (string.IsNullOrEmpty(dir)) dir = ConfigStore.DefaultDirectory;
        OpenFolder(dir);
    }

    private void RefreshStorage_Click(object sender, RoutedEventArgs e) => RefreshStorageStats();

    /// <summary>刷新监控事件列表（最近 100 条，倒序）。</summary>
    private void RefreshEvents_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not SqliteLocalStore s) { EventSummary.Text = "本地库未就绪。"; return; }
        try
        {
            var list = s.QueryRecentEventsAsync(100).GetAwaiter().GetResult();
            EventGrid.ItemsSource = list.Select(x => new
            {
                时间 = x.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                类型 = x.EventType,
                级别 = x.Severity,
                详情 = x.Context ?? ""
            }).ToList();
            var warn = list.Count(x => x.Severity != MonitorEventSeverity.Info);
            EventSummary.Text = list.Count == 0
                ? "暂无事件。超限、缺测、断线、存储故障等状态变化会记录在这里。"
                : $"最近 {list.Count} 条事件（其中非信息级 {warn} 条）。事件只记录状态变化，持续同一状态不会重复记录。";
        }
        catch (Exception ex) { EventSummary.Text = "读取事件失败：" + ex.Message; }
    }

    private void RefreshStorageStats()
    {
        if (_store is null) { StorageStats.Text = "本地库未就绪。"; return; }
        try
        {
            var samples = _store.CountSamplesAsync().GetAwaiter().GetResult();
            var minutes = _store.CountMinutesAsync().GetAwaiter().GetResult();
            var sizeText = "";
            try
            {
                var fi = new FileInfo(_cfg.SqliteFilePath);
                if (fi.Exists) sizeText = $"｜文件大小 {fi.Length / 1024.0:F0} KB";
            }
            catch { }
            StorageStats.Text = $"本地库：{_cfg.SqliteFilePath}{sizeText}\n"
                + $"逐次样本 {samples} 条｜分钟汇总 {minutes} 条｜状态：{( _store.State == StorageState.Ok ? "正常" : _store.LastError)}";
            if (_cfg.StorageMode != "sqlserver")
                RemoteStats.Text = "远端同步：未选择 SQL Server（仅本机保存）。";
            else if (_remoteSink is null)
                RemoteStats.Text = "远端同步：已选择 SQL Server 但未启动（请检查连接字符串后重新应用）。";
            else
            {
                var pend = (_store as SqliteLocalStore)?.CountPendingAsync().GetAwaiter().GetResult() ?? 0;
                var st = _remoteSink.State switch
                {
                    RemoteSyncState.Idle => "空闲（无待传）",
                    RemoteSyncState.Pending => "待同步",
                    RemoteSyncState.Error => "同步失败（本地仍持续记录）",
                    _ => "未启用"
                };
                RemoteStats.Text = $"远端同步：{st}｜待传 {pend} 项｜已传输 {_remoteSink.TransferredCount} 条"
                    + (_remoteSink.LastError.Length > 0 ? $"\n失败原因：{_remoteSink.LastError}" : "")
                    + "\n说明：数据先写本机，断网不影响本地记录；恢复后自动补传，重放不会重复。";
            }
        }
        catch (Exception ex)
        {
            StorageStats.Text = "统计失败：" + ex.Message;
        }
    }

    private void RefreshDisplays()
    {
        var screens = ScreenLocator.Enumerate();
        DisplayCombo.Items.Clear();
        foreach (var s in screens) DisplayCombo.Items.Add(s.DeviceName);
        var want = string.IsNullOrWhiteSpace(_cfg.Overlay.DisplayDeviceName)
            ? screens.FirstOrDefault(s => s.IsPrimary)?.DeviceName
            : _cfg.Overlay.DisplayDeviceName;
        if (want is not null)
        {
            DisplayCombo.SelectedItem = want;
        }
    }

    private void UpdateFontSizeLabel() =>
        FontSizeLabel.Text = $"字号：{FontSizeSlider.Value:F0}（范围 16～96）";

    private void UpdateTextOpacityLabel() =>
        TextOpacityLabel.Text = $"文字不透明度：{TextOpacitySlider.Value:P0}（背景与文字透明度分开设置）";

    private void FontSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateFontSizeLabel();
        ApplyOverlayPreview();
    }

    private void Overlay_SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTextOpacityLabel();
        ApplyOverlayPreview();
    }

    private void Overlay_Changed(object sender, RoutedEventArgs e) => ApplyOverlayPreview();
    private void Overlay_TextChanged(object sender, TextChangedEventArgs e) => ApplyOverlayPreview();
    private void Overlay_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyOverlayPreview();

    private OverlaySettings BuildOverlayFromUi()
    {
        var o = _cfg.Overlay.Clone();
        o.Enabled = OverlayEnabled.IsChecked == true;
        o.ShowTemperature = OverlayShowTemp.IsChecked == true;
        o.ShowHumidity = OverlayShowHum.IsChecked == true;
        o.ShowDeviceLabel = OverlayShowDevice.IsChecked == true;
        o.ShowStatusText = OverlayShowStatus.IsChecked == true;
        o.ShadowEnabled = OverlayShadow.IsChecked == true;
        o.ClickThrough = OverlayClickThrough.IsChecked == true;
        o.Corner = (CornerCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "BottomRight";
        o.DisplayDeviceName = DisplayCombo.SelectedItem?.ToString() ?? "";
        o.MarginX = ParseDouble(MarginXBox.Text, 24);
        o.MarginY = ParseDouble(MarginYBox.Text, 24);
        o.FontSize = Math.Clamp(FontSizeSlider.Value, 16, 96);
        o.TemperatureColor = TempColorBox.Text?.Trim() ?? "#E53935";
        o.HumidityColor = HumColorBox.Text?.Trim() ?? "#1E88E5";
        o.TextColor = TextColorBox.Text?.Trim() ?? "#FFFFFF";
        o.BackgroundOpacity = Math.Clamp(ParseDouble(BgOpacityBox.Text, 0), 0, 1);
        o.TextOpacity = Math.Clamp(TextOpacitySlider.Value, 0.15, 1);
        return o;
    }

    /// <summary>预览即时生效：立即应用到悬浮窗，但不写配置（取消可还原）。</summary>
    private void ApplyOverlayPreview()
    {
        if (!IsLoaded) return;
        var o = BuildOverlayFromUi();
        if (o.Enabled)
        {
            EnsureOverlay();
            _overlay!.FreshnessWindow = _acq.FreshnessWindow;
            _overlay.ApplySettings(o, persist: false);
            _overlay.Show();
            _tray?.SetOverlayVisible(true);
        }
        else if (_overlay is not null)
        {
            _overlay.Hide();
            _tray?.SetOverlayVisible(false);
        }
        OverlayFeedback.Text = "预览已更新（尚未保存；点“保存悬浮设置”写入配置）。";
    }

    private TrayManager? _tray;
    public void AttachTray(TrayManager tray)
    {
        _tray = tray;
        tray.ClickThroughChanged += v =>
        {
            OverlayClickThrough.IsChecked = v;
            var o = BuildOverlayFromUi();
            o.ClickThrough = v;
            _overlay?.ApplySettings(o);
            _cfg.Overlay.ClickThrough = v;
            ConfigStore.SaveOverlay(_cfg.Overlay);
            OverlayFeedback.Text = v ? "已开启鼠标穿透（托盘可随时关闭）。" : "已关闭鼠标穿透，可拖动悬浮窗调整位置。";
        };
        tray.ResetPositionRequested += () =>
        {
            var o = BuildOverlayFromUi();
            o.DisplayDeviceName = "";
            o.Corner = "BottomRight";
            o.MarginX = 24; o.MarginY = 24;
            _overlay?.ApplySettings(o);
            _overlay?.RepositionSafe();
            _cfg.Overlay.DisplayDeviceName = "";
            _cfg.Overlay.Corner = "BottomRight";
            _cfg.Overlay.MarginX = 24; _cfg.Overlay.MarginY = 24;
            ConfigStore.SaveOverlay(_cfg.Overlay);
            OverlayFeedback.Text = "已将悬浮窗恢复到主屏右下角可见区域。";
        };
    }

    public void EnsureOverlay()
    {
        if (_overlay is not null) return;
        _overlay = new OverlayWindow();
        _overlay.FreshnessWindow = _acq.FreshnessWindow;
        _overlay.ApplySettings(_cfg.Overlay);
        // 拖动后保存新位置：否则用户调整的悬浮窗位置在重启后会丢失
        _overlay.DragPositionChanged += (corner, mx, my) =>
        {
            _cfg.Overlay.Corner = corner;
            _cfg.Overlay.MarginX = mx;
            _cfg.Overlay.MarginY = my;
            var err = ConfigStore.SaveOverlay(_cfg.Overlay);
            AppLog.Info(err.Length > 0
                ? $"悬浮窗位置已变更但保存失败：{err}"
                : $"悬浮窗位置已保存：{corner}（边距 {mx:F0}, {my:F0}）");
        };
        _overlay.Show();
        _overlay.ApplyClickThrough();
        _overlay.Update(_acq.Current);
    }

    public void SetOverlayVisible(bool visible)
    {
        if (visible)
        {
            EnsureOverlay();
            _overlay?.Show();
        }
        else _overlay?.Hide();
    }

    public bool IsOverlayVisible => _overlay?.IsVisible == true;

    private void SaveOverlay_Click(object sender, RoutedEventArgs e)
    {
        var o = BuildOverlayFromUi();
        var err = ConfigStore.SaveOverlay(o);
        if (err.Length > 0) { OverlayFeedback.Text = "❌ " + err; return; }
        _cfg.Overlay = o;
        _overlayBackup = o.Clone();   // 更新取消回退基准，使“取消”还原到刚保存的设置
        OverlayFeedback.Text = "✅ 悬浮设置已保存，重启后保持。";
        if (o.Enabled) { EnsureOverlay(); _overlay!.ApplySettings(o); _tray?.SetOverlayVisible(true); }
        else { _overlay?.Hide(); _tray?.SetOverlayVisible(false); }
        _tray?.SetClickThroughChecked(o.ClickThrough);
    }

    private void CancelOverlay_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Overlay = _overlayBackup.Clone();
        LoadOverlayUi(_cfg.Overlay);
        _overlay?.ApplySettings(_cfg.Overlay);
        OverlayFeedback.Text = "已取消修改，悬浮设置还原为上次保存的值（配置未改动）。";
    }

    private void ResetOverlay_Click(object sender, RoutedEventArgs e)
    {
        var def = new OverlaySettings();
        _cfg.Overlay = def;
        LoadOverlayUi(def);
        if (def.Enabled) { EnsureOverlay(); _overlay!.ApplySettings(def); }
        OverlayFeedback.Text = "已恢复默认：透明背景、不透明数字、右下角、字号 32、鼠标穿透开启。点“保存悬浮设置”写入配置。";
    }

    private void RepositionOverlay_Click(object sender, RoutedEventArgs e)
    {
        EnsureOverlay();
        _overlay!.RepositionSafe();
        OverlayFeedback.Text = "已按当前设置与工作区重新定位（避开任务栏）。";
    }

    private void LoadOverlayUi(OverlaySettings o)
    {
        OverlayEnabled.IsChecked = o.Enabled;
        OverlayShowTemp.IsChecked = o.ShowTemperature;
        OverlayShowHum.IsChecked = o.ShowHumidity;
        OverlayShowDevice.IsChecked = o.ShowDeviceLabel;
        OverlayShowStatus.IsChecked = o.ShowStatusText;
        OverlayShadow.IsChecked = o.ShadowEnabled;
        OverlayClickThrough.IsChecked = o.ClickThrough;
        SelectComboItemByTag(CornerCombo, o.Corner);
        MarginXBox.Text = o.MarginX.ToString("F0");
        MarginYBox.Text = o.MarginY.ToString("F0");
        FontSizeSlider.Value = Math.Clamp(o.FontSize, 16, 96);
        TempColorBox.Text = o.TemperatureColor;
        HumColorBox.Text = o.HumidityColor;
        TextColorBox.Text = o.TextColor;
        BgOpacityBox.Text = o.BackgroundOpacity.ToString("0.##", CultureInfo.InvariantCulture);
        TextOpacitySlider.Value = Math.Clamp(o.TextOpacity, 0.15, 1);
    }
}public partial class MainWindow
{
    private void SaveDevice_Click(object sender, RoutedEventArgs e)
    {
        var error = SaveSelectedSensorFromUi();
        DeviceFeedback.Text = error.Length > 0 ? error : "当前传感器信息已保存，固定编号和历史归属保持连续。";
    }

    private void NewDevice_Click(object sender, RoutedEventArgs e)
    {
        var list = _cfg.Sensors.Select(sensor => sensor.Clone()).ToList();
        var sensor = list.First(item => item.Device.DeviceId == _cfg.SelectedSensorId);
        sensor.Device = new DeviceIdentity { DisplayName = sensor.Device.DisplayName, Location = sensor.Device.Location };
        sensor.Thresholds = ThresholdVersions.Copy(sensor.Thresholds, ThresholdVersions.Next());
        var error = CommitSensorList(list, sensor.Device.DeviceId);
        DeviceFeedback.Text = error.Length > 0 ? error : "已为替换的实物建立新编号；原传感器可在历史设备中查询。";
    }

    /// <summary>最小化到托盘（采集继续，不退出）。</summary>
    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        AppLog.Info("已最小化到托盘（采集继续运行）。双击托盘图标可重新打开。");
    }

    /// <summary>
    /// 退出程序：先停止采集并等待本轮已接收样本写入完成，再释放串口与本地库。
    /// 若有未同步到远端的记录，先告知用户而不是静默丢弃。
    /// </summary>
    private void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        var snap = _acq.Current;
        var pendingNote = "";
        try
        {
            var pending = (_store as SqliteLocalStore)?.CountPendingAsync().GetAwaiter().GetResult() ?? 0;
            if (pending > 0 && _cfg.StorageMode == "sqlserver")
                pendingNote = $"\n\n注意：还有 {pending} 项待传记录尚未同步到 SQL Server。\n本机数据不会丢失，下次启动后会继续补传。";
        }
        catch { }

        var answer = MessageBox.Show(
            "确定要退出吗？\n\n退出会停止采集并释放串口。本轮已接收的样本会先尝试保存，尚未同步到 SQL Server 的记录会在下次启动后继续补传。"
            + pendingNote,
            "退出温湿度监测", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        AppLog.Info($"用户请求退出（当前状态：{snap.Collector} / 存储 {snap.Storage}）。");
        ExitRequested?.Invoke();
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e) => RefreshLog();

    private void RefreshLog()
    {
        LogText.Text = AppLog.Tail(300);
        LogText.ScrollToEnd();
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(AppLog.CurrentFile) ?? ConfigStore.DefaultDirectory;
        OpenFolder(dir);
    }

    private static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("打开目录失败：" + ex.Message);
        }
    }

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _cfg.AutoStartAcquisition = AutoStartAcq.IsChecked == true;
        _cfg.StartWithWindows = StartWithWindows.IsChecked == true;
        var err = ConfigStore.Save(_cfg);
        if (err.Length > 0) { AppLog.Warn(err); return; }
        var startupError = ApplyStartWithWindows(_cfg.StartWithWindows);
        if (startupError.Length > 0) { ActionFeedback.Text = startupError; AppLog.Warn(startupError); }
    }

    /// <summary>登录启动：写入 HKCU Run 项；默认关闭，由用户显式选择。</summary>
    public static string ApplyStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return "无法访问启动项注册表键。";
            const string Name = "ThermoHygro";
            if (enabled)
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (exe.Length == 0) return "无法确定程序路径，未设置登录启动。";
                key.SetValue(Name, "\"" + exe + "\"");
                using var approved = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
                if (approved?.GetValue(Name) is byte[] state && state.Length > 0 && state[0] is 3 or 7)
                    return "登录启动路径已登记，但 Windows 已禁用该启动项。请在任务管理器的启动应用中启用，并重新登录验证。";
                return "";
            }
            key.DeleteValue(Name, throwOnMissingValue: false);
            return "";
        }
        catch (Exception ex)
        {
            return "设置登录启动失败：" + ex.Message;
        }
    }

    private void UpdateOverlayFreshnessWindow()
    {
        if (_overlay is not null) _overlay.FreshnessWindow = _acq.FreshnessWindow;
    }

    private void OnClosedEvent(object? sender, EventArgs e)
    {
        // 正常路径不会真关闭（Closing 已改为隐藏）；此事件仅在真正退出时触发
        AppLog.Info(_shuttingDown
            ? "主窗口已关闭（应用退出中）"
            : "主窗口已关闭（采集继续，应用驻留托盘）");
    }

    /// <summary>真正退出：停止采集、提交已接收样本、释放串口。</summary>
    public string ShutdownForExit()
    {
        _shuttingDown = true;
        try
        {
            _maintenanceTimer?.Stop();
            _sensorDisplayTimer?.Stop();
            _maintenanceTimer = null;
            StopRemoteSync();
            _acq.StopAsync().GetAwaiter().GetResult();
            RunMaintenance();
            (_store as IDisposable)?.Dispose();
            _store = null;
            _overlay?.Shutdown();
            AppLog.Info("已退出：采集已停止、串口已释放、本地库已关闭");
            return "";
        }
        catch (Exception ex)
        {
            AppLog.Error("退出时发生异常", ex);
            return "退出时发生异常：" + ex.Message;
        }
    }

    public void ShowTrendTab() => MainTabs.SelectedIndex = 0;

    public void SelectTab(int index)
    {
        if (index >= 0 && index < MainTabs.Items.Count) MainTabs.SelectedIndex = index;
    }

    public void ShowWelcomeHint()
    {
        ActionFeedback.Text = _cfg.ConfigurationReadError.Length > 0 ? _cfg.ConfigurationReadError
            : "提示：维护各传感器后先测试读取，再点击开始记录；多传感器均可持续记录。";
    }
}
