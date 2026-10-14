using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ThermoHygro.Core;
using ThermoHygro.Infrastructure;
using ThermoHygro.Protocols;

namespace ThermoHygro.App;

/// <summary>
/// 首次使用向导：选型号 → 串口 → 测试读取 → 保存方式 → 命名与阈值 → 开始记录。
/// 打开串口 ≠ 连接成功；测试读取分别展示温/湿的成功失败与原因。
/// </summary>
public partial class SetupWizard : Window
{
    private readonly MainWindow _main;
    private readonly AppConfiguration _cfg;
    private readonly Ecm30Protocol _protocol = new();
    private int _step = 1;
    private const int Steps = 6;
    private ReadingGroup? _lastTest;

    public SetupWizard(MainWindow main, AppConfiguration cfg)
    {
        _main = main;
        _cfg = cfg;
        InitializeComponent();
        ModelList.SelectedIndex = 0;
        ModelVerifyText.Text = $"实物验证状态：{_protocol.VerificationStatus}。"
            + "协议事实（寄存器、功能码、样例报文）来自既有实现与协议文献，未做实物对照。";
        PortCombo.Text = string.IsNullOrWhiteSpace(_cfg.Serial.PortName) ? "" : _cfg.Serial.PortName;
        SqlitePathBox.Text = string.IsNullOrWhiteSpace(_cfg.SqliteFilePath) ? ConfigStore.DefaultDatabasePath : _cfg.SqliteFilePath;
        DeviceNameBox.Text = _cfg.Device.DisplayName;
        LocationBox.Text = _cfg.Device.Location;
        RefreshPorts();
        Render();
    }

    private void Render()
    {
        StepIndicator.Text = $"第 {_step} / {Steps} 步";
        Page1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Page2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Page3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Page4.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;
        Page5.Visibility = _step == 5 ? Visibility.Visible : Visibility.Collapsed;
        Page6.Visibility = _step == 6 ? Visibility.Visible : Visibility.Collapsed;
        BtnBack.IsEnabled = _step > 1;
        BtnNext.Content = _step >= Steps ? "完成" : "下一步";
        FootHint.Text = _step switch
        {
            1 => "只有 ECM-30 是真实可选项。",
            2 => "默认 9600 8N1、地址 1，与实物必须一致。",
            3 => "打开串口成功不代表已连上传感器。",
            4 => "本机 SQLite 是可靠记录的主体。",
            5 => "阈值留空表示不启用超限判定。",
            _ => "完成后写入配置并（可选）开始采集。"
        };
        if (_step == 6) BuildSummary();
    }

    private void Model_Changed(object sender, SelectionChangedEventArgs e) { }
    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        var names = SerialTransport.GetPortNames();
        var cur = PortCombo.Text;
        PortCombo.Items.Clear();
        foreach (var n in names) PortCombo.Items.Add(n);
        if (!string.IsNullOrWhiteSpace(cur)) PortCombo.Text = cur;
        PortHint.Text = names.Length == 0
            ? "未检测到串口。请检查 USB 转 RS-485 驱动与接线，或手动输入端口名（例如 COM12）。"
            : $"检测到 {names.Length} 个串口：{string.Join("、", names)}。也可手动输入列表外的端口名。";
        PortCombo.IsEditable = true;
    }

    private static string ComboValue(ComboBox box, string fallback)
    {
        if (box.SelectedItem is ComboBoxItem it) return it.Tag?.ToString() ?? it.Content?.ToString() ?? fallback;
        return string.IsNullOrWhiteSpace(box.Text) ? fallback : box.Text.Trim();
    }

    private static int ParseInt(string s, int fb) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fb;

    private SerialSettings BuildSettings() => new()
    {
        PortName = PortCombo.Text?.Trim() ?? "",
        BaudRate = ParseInt(ComboValue(BaudCombo, "9600"), 9600),
        DataBits = ParseInt(ComboValue(DataBitsCombo, "8"), 8),
        Parity = ComboValue(ParityCombo, "None"),
        StopBits = ComboValue(StopBitsCombo, "One"),
        SlaveAddress = ParseInt(AddrBox.Text, 0),
        ReadTimeoutMs = ParseInt(TimeoutBox.Text, 0),
        PollIntervalSeconds = ParseInt(IntervalBox.Text, 0)
    };

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 1) { _step--; Render(); }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 2)
        {
            var s = BuildSettings();
            var serialError = SensorConfigurationRules.ValidateSerial(s);
            if (serialError.Length > 0)
            {
                MessageBox.Show(this, serialError, "串口", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(s.PortName))
            {
                MessageBox.Show(this, "请填写或选择串口名称（例如 COM12）。", "串口", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (s.SlaveAddress is < 1 or > 247)
            {
                MessageBox.Show(this, "从站地址必须在 1～247 之间。", "串口", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (s.PollIntervalSeconds is < 1 or > 60)
            {
                MessageBox.Show(this, "采集间隔必须在 1～60 秒之间。", "串口", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _cfg.Serial = s;
            ConfigStore.Save(_cfg);
        }

        if (_step == 5)
        {
            var err = ValidateThresholds();
            if (err.Length > 0)
            {
                MessageBox.Show(this, err, "阈值", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ApplyStep5();
        }

        if (_step >= Steps) { Finish(); return; }
        _step++;
        Render();
    }

    private string ValidateThresholds()
    {
        double? Parse(TextBox b) =>
            double.TryParse(b.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        var tl = Parse(TempLowBox); var th = Parse(TempHighBox);
        var hl = Parse(HumLowBox); var hh = Parse(HumHighBox);
        if (TempLowBox.Text?.Trim().Length > 0 && !tl.HasValue) return "温度下限不是有效数字。";
        if (TempHighBox.Text?.Trim().Length > 0 && !th.HasValue) return "温度上限不是有效数字。";
        if (HumLowBox.Text?.Trim().Length > 0 && !hl.HasValue) return "湿度下限不是有效数字。";
        if (HumHighBox.Text?.Trim().Length > 0 && !hh.HasValue) return "湿度上限不是有效数字。";
        if (tl.HasValue && th.HasValue && tl.Value >= th.Value) return "温度下限必须严格小于上限（等于边界算正常）。";
        if (hl.HasValue && hh.HasValue && hl.Value >= hh.Value) return "湿度下限必须严格小于上限（等于边界算正常）。";
        if (hl.HasValue && (hl.Value < 0 || hl.Value > 100)) return "湿度下限应在 0～100 %RH。";
        if (hh.HasValue && (hh.Value < 0 || hh.Value > 100)) return "湿度上限应在 0～100 %RH。";
        return "";
    }

    private void ApplyStep5()
    {
        double? Parse(TextBox b) =>
            double.TryParse(b.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

        var name = DeviceNameBox.Text?.Trim() ?? "";
        _cfg.Device.DisplayName = name.Length == 0 ? "温湿度传感器" : name;
        _cfg.Device.Location = LocationBox.Text?.Trim() ?? "";

        _cfg.Thresholds = new ThresholdSnapshot
        {
            VersionId = ThresholdVersions.Next(), // 新版本号，不改写旧历史
            TemperatureLow = Parse(TempLowBox),
            TemperatureHigh = Parse(TempHighBox),
            HumidityLow = Parse(HumLowBox),
            HumidityHigh = Parse(HumHighBox),
            EffectiveFromUtc = DateTimeOffset.UtcNow,
            Source = "首次向导设置"
        };
        ConfigStore.Save(_cfg);
    }

    private void TestRead_Click(object sender, RoutedEventArgs e)
    {
        var s = BuildSettings();
        if (string.IsNullOrWhiteSpace(s.PortName)) { TestResult.Text = "请先回到第 2 步填写串口。"; return; }
        TestResult.Text = "正在测试读取…（分别请求温度与湿度）";
        try
        {
            var transport = new SerialTransport();
            var svc = new AcquisitionService(transport, new SystemClock());
            _lastTest = svc.TestReadAsync(s, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            svc.Dispose();
            TestResult.Text = FormatTest(_lastTest, s);
            AppLog.Info("向导测试读取：" + TestResult.Text.Replace(Environment.NewLine, " | "));
        }
        catch (Exception ex)
        {
            TestResult.Text = "测试读取异常：" + ex.Message;
            AppLog.Error("向导测试读取异常", ex);
        }
    }

    private static string FormatTest(ReadingGroup g, SerialSettings s)
    {
        string Line(string name, MetricReading r, string unit)
        {
            if (r.Quality is MetricQuality.Good or MetricQuality.OutOfRange)
                return $"  ✅ {name}：{r.Value:F1} {unit}　（读取时间 {r.TimestampUtc?.ToLocalTime():HH:mm:ss}）";
            return $"  ❌ {name}：失败　原因：{r.Reason ?? "未读取"}";
        }
        var head = g.AnySuccess
            ? $"串口 {s.PortName} 打开成功，并已读取到传感器数据（{s.Describe()}）："
            : $"串口 {s.PortName} 打开成功，但未读取到有效数据（打开串口不等于连接成功）：";
        return head + Environment.NewLine
            + Line("温度", g.Temperature, "℃") + Environment.NewLine
            + Line("湿度", g.Humidity, "%RH") + Environment.NewLine
            + Environment.NewLine
            + "提示：若提示“被占用”，请关闭串口调试工具；若超时，请核对地址、波特率与接线（A/B 是否接反）。";
    }

    private void BuildSummary()
    {
        var s = _cfg.Serial;
        var mode = RbSqlServer.IsChecked == true ? "同时保存到 SQL Server（本机仍持续留存）" : "本机保存（推荐）";
        var th = _cfg.Thresholds;
        string ThText() => th.IsConfigured
            ? $"温度 {Fmt(th.TemperatureLow)}～{Fmt(th.TemperatureHigh)} ℃，湿度 {Fmt(th.HumidityLow)}～{Fmt(th.HumidityHigh)} %RH"
            : "未配置（不启用超限判定）";
        string Fmt(double? v) => v.HasValue ? v.Value.ToString("F1") : "—";

        var testLine = _lastTest is null
            ? "未执行测试读取（可在主窗口“测试读取”）"
            : (_lastTest.AnySuccess ? "已读取到数据" : "未读到有效数据（失败原因见第 3 步）");

        SummaryText.Text =
            $"型号：{Ecm30Protocol.DisplayName}（实物验证状态：{_protocol.VerificationStatus}）\n"
            + $"串口：{s.Describe()}　超时 {s.ReadTimeoutMs} ms　间隔 {s.PollIntervalSeconds} s\n"
            + $"测试读取：{testLine}\n"
            + $"保存方式：{mode}\n"
            + $"本地库：{SqlitePathBox.Text}\n"
            + $"设备：{_cfg.Device.DisplayName}　位置：{_cfg.Device.Location}\n"
            + $"阈值：{ThText()}\n"
            + $"设备编号：{_cfg.Device.DeviceId}";
    }

    private void Finish()
    {
        try
        {
            _cfg.Serial = BuildSettings();
            _cfg.StorageMode = RbSqlServer.IsChecked == true ? "sqlserver" : "sqlite";
            _cfg.SqliteFilePath = string.IsNullOrWhiteSpace(SqlitePathBox.Text)
                ? ConfigStore.DefaultDatabasePath : SqlitePathBox.Text.Trim();
            _cfg.SqlServerConnection = SqlServerBox.Text?.Trim() ?? "";
            _cfg.WizardCompleted = true;
            var err = ConfigStore.Save(_cfg);
            if (err.Length > 0)
            {
                DoneText.Foreground = System.Windows.Media.Brushes.DarkRed;
                DoneText.Text = "配置保存失败：" + err + "（未静默忽略，请检查目录权限）";
                return;
            }
            DoneText.Text = "配置已保存。";
            AppLog.Info("向导完成，配置已保存");
        }
        catch (Exception ex)
        {
            DoneText.Foreground = System.Windows.Media.Brushes.DarkRed;
            DoneText.Text = "保存配置异常：" + ex.Message;
            return;
        }

        var startNow = StartNowCheck.IsChecked == true;
        _main.ApplyWizardConfiguration();
        DialogResult = true;
        Close();

        if (startNow)
        {
            _main.StartAcquisition();
            _main.RefreshTrend();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cfg.WizardCompleted = true;   // 不再重复弹出，可在主窗口继续修改
        ConfigStore.Save(_cfg);
        AppLog.Info("向导被取消（配置保留现状）");
        DialogResult = false;
        Close();
    }
}
