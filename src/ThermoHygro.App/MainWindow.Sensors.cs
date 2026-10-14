using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ThermoHygro.Core;
using ThermoHygro.Infrastructure;

namespace ThermoHygro.App;

public partial class MainWindow
{
    private bool _populatingSensors;
    private DispatcherTimer? _sensorDisplayTimer;
    private DateTime _trendRefreshedAt = DateTime.MinValue;
    private List<SensorConfiguration>? _previousSensors;
    private string _previousSelectedId = "";

    private void PopulateSensorSelectors()
    {
        _populatingSensors = true;
        try
        {
            SensorCombo.ItemsSource = _cfg.Sensors.ToList();
            DeviceSensorCombo.ItemsSource = _cfg.Sensors.ToList();
            SensorCombo.SelectedValue = _cfg.SelectedSensorId;
            DeviceSensorCombo.SelectedValue = _cfg.SelectedSensorId;
            PopulateSelectedSensor();
        }
        finally { _populatingSensors = false; }
        _acq.SelectedSensorId = _cfg.SelectedSensorId;
        _acq.ConfigureIdle(_cfg.Sensors);
        RefreshSensorOverview();
    }

    private void PopulateSelectedSensor()
    {
        PopulateSerialOnly();
        DeviceNameBox.Text = _cfg.Device.DisplayName;
        LocationBox.Text = _cfg.Device.Location;
        DeviceIdText.Text = $"固定编号：{_cfg.Device.DeviceId}（改名或改地址后历史仍连续）。";
        SensorEnabledCheck.IsChecked = ConfigStore.SelectedSensor(_cfg).Enabled;
        string Format(double? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        SensorTempLow.Text = Format(_cfg.Thresholds.TemperatureLow);
        SensorTempHigh.Text = Format(_cfg.Thresholds.TemperatureHigh);
        SensorHumLow.Text = Format(_cfg.Thresholds.HumidityLow);
        SensorHumHigh.Text = Format(_cfg.Thresholds.HumidityHigh);
    }

    private void SensorSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingSensors || sender is not ComboBox box || box.SelectedValue is not string id || id == _cfg.SelectedSensorId) return;
        ConfigStore.CommitSelectedSensor(_cfg);
        ConfigStore.SelectSensor(_cfg, id);
        var error = ConfigStore.Save(_cfg);
        PopulateSensorSelectors();
        RefreshHistorySensors(id);
        UpdateOverlayFreshnessWindow();
        ApplySnapshot(VisibleSnapshot(_acq.Current));
        RefreshTrend();
        if (error.Length > 0) DeviceFeedback.Text = error;
    }

    private void RefreshHistorySensors(string? prefer = null)
    {
        if (HistorySensorCombo is null) return;
        string selected = prefer ?? HistorySensorCombo.SelectedValue?.ToString() ?? _cfg.SelectedSensorId;
        var devices = new Dictionary<string, DeviceIdentity>();
        if (_store is SqliteLocalStore store)
        {
            try
            {
                foreach (var device in store.QueryDevicesAsync().GetAwaiter().GetResult())
                    devices[device.DeviceId] = new DeviceIdentity
                    {
                        DeviceId = device.DeviceId,
                        DisplayName = device.DisplayName + (_cfg.Sensors.Any(sensor => sensor.Device.DeviceId == device.DeviceId) ? "" : "（历史设备）"),
                        Location = device.Location
                    };
            }
            catch (Exception ex) { AppLog.Warn("读取历史设备目录失败：" + ex.Message); }
        }
        foreach (var sensor in _cfg.Sensors) devices[sensor.Device.DeviceId] = sensor.Device;
        HistorySensorCombo.ItemsSource = devices.Values.OrderBy(device => device.DisplayName).ToList();
        HistorySensorCombo.SelectedValue = devices.ContainsKey(selected) ? selected : _cfg.SelectedSensorId;
        HistoryStartDate.SelectedDate ??= DateTime.Today.AddDays(-6);
        HistoryEndDate.SelectedDate ??= DateTime.Today;
    }

    private void HistorySensor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryGrid is null) return;
        _snapshot = null;
        HistoryGrid.ItemsSource = null;
        HistorySummary.Text = "请选择日期范围后查询或导出当前传感器的报表。";
    }

    private void StartSensorDisplayTimer()
    {
        _sensorDisplayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sensorDisplayTimer.Tick += (_, _) =>
        {
            RefreshSensorOverview();
            ApplySnapshot(VisibleSnapshot(_acq.Current));
            if (_acq.IsRunning && DateTime.UtcNow - _trendRefreshedAt > TimeSpan.FromSeconds(30))
            {
                _trendRefreshedAt = DateTime.UtcNow; RefreshTrend();
            }
        };
        _sensorDisplayTimer.Start();
    }

    private AcquisitionSnapshot VisibleSnapshot(AcquisitionSnapshot value)
    {
        var sensor = _cfg.Sensors.FirstOrDefault(s => s.Device.DeviceId == value.DeviceId) ?? ConfigStore.SelectedSensor(_cfg);
        MetricReading Visible(MetricReading reading)
        {
            bool paused = !sensor.Enabled || value.Collector == CollectorState.Paused;
            bool stale = reading.TimestampUtc.HasValue && DateTimeOffset.UtcNow - reading.TimestampUtc.Value > _acq.FreshnessFor(sensor.Device.DeviceId);
            if (!paused && !stale) return reading;
            return new MetricReading
            {
                Kind = reading.Kind, Quality = MetricQuality.Stale,
                Reason = paused ? "已暂停或停用" : "超过更新时间，当前缺测",
                LastGoodValue = reading.Value ?? reading.LastGoodValue,
                LastGoodTimestampUtc = reading.TimestampUtc ?? reading.LastGoodTimestampUtc
            };
        }
        return new AcquisitionSnapshot
        {
            DeviceId = sensor.Device.DeviceId, DeviceLabel = sensor.Device.DisplayName,
            Reading = new ReadingGroup { AttemptTimeUtc = value.Reading.AttemptTimeUtc, Temperature = Visible(value.Reading.Temperature), Humidity = Visible(value.Reading.Humidity) },
            Collector = sensor.Enabled ? value.Collector : CollectorState.Paused, Storage = value.Storage,
            Remote = value.Remote, StatusText = sensor.Enabled ? value.StatusText : "已停用",
            EvaluatedAtUtc = DateTimeOffset.UtcNow, Sequence = value.Sequence, RoundCompleted = value.RoundCompleted, MissedSlots = value.MissedSlots
        };
    }

    private void RefreshSensorOverview()
    {
        string Format(MetricReading reading, string unit) => reading.Value.HasValue
            && reading.Quality is MetricQuality.Good or MetricQuality.OutOfRange ? $"{reading.Value:F1} {unit}" : "—";
        SensorOverviewGrid.ItemsSource = _cfg.Sensors.Select(sensor =>
        {
            var snapshot = VisibleSnapshot(_acq.SnapshotFor(sensor.Device.DeviceId));
            return new
            {
                Name = sensor.Device.DisplayName, Address = $"{sensor.Serial.PortName} / {sensor.Serial.SlaveAddress}",
                Temperature = Format(snapshot.Reading.Temperature, "℃"), Humidity = Format(snapshot.Reading.Humidity, "%RH"),
                Status = (!sensor.Enabled ? "已停用" : DescribeCollector(snapshot.Collector))
                    + " / " + (snapshot.Storage == StorageState.Ok ? "已保存" : snapshot.Storage == StorageState.Fault ? "未保存" : "尚未保存")
            };
        }).ToList();
    }

    /// <summary>先持久化成功，再应用采集配置；写文件失败时完整恢复上一份配置。</summary>
    private string CommitSensorList(List<SensorConfiguration> sensors, string selected, bool validate = true)
    {
        if (validate)
        {
            var error = SensorConfigurationRules.Validate(sensors);
            if (error.Length > 0) return error;
        }
        var previous = _cfg.Sensors.Select(sensor => sensor.Clone()).ToList();
        var previousId = _cfg.SelectedSensorId;
        var running = _acq.IsRunning;
        _cfg.Sensors = sensors;
        ConfigStore.SelectSensor(_cfg, selected);
        var saveError = ConfigStore.Save(_cfg);
        if (saveError.Length > 0)
        {
            _cfg.Sensors = previous;
            ConfigStore.SelectSensor(_cfg, previousId);
            return saveError;
        }
        _previousSensors = previous; _previousSelectedId = previousId;
        string connection = running ? _acq.Start(_cfg.Sensors) : "";
        if (_store is SqliteLocalStore store)
        {
            try { foreach (var sensor in sensors) store.SaveDeviceAsync(sensor.Device).GetAwaiter().GetResult(); }
            catch (Exception ex) { connection += "；设备名称已保存，但本地目录更新失败：" + ex.Message; }
        }
        PopulateSensorSelectors();
        RefreshHistorySensors();
        UpdateOverlayFreshnessWindow();
        ApplySnapshot(VisibleSnapshot(_acq.Current));
        RefreshTrend();
        AcquireStateChanged?.Invoke();
        return connection.Length > 0 ? "设置已保存；连接或存储提示：" + connection : "";
    }

    private string SaveSelectedSensorFromUi()
    {
        if (!int.TryParse(SlaveAddrBox.Text, out _) || !int.TryParse(TimeoutBox.Text, out _) || !int.TryParse(IntervalBox.Text, out _))
            return "地址、读取超时和采集间隔必须填写整数。";
        var list = _cfg.Sensors.Select(sensor => sensor.Clone()).ToList();
        var selected = list.First(sensor => sensor.Device.DeviceId == _cfg.SelectedSensorId);
        selected.Device.DisplayName = DeviceNameBox.Text.Trim();
        selected.Device.Location = LocationBox.Text.Trim();
        selected.Serial = ReadSerialFromUi();
        selected.Enabled = SensorEnabledCheck.IsChecked == true;
        return CommitSensorList(list, selected.Device.DeviceId);
    }

    private void AddSensor_Click(object sender, RoutedEventArgs e)
    {
        var list = _cfg.Sensors.Select(sensor => sensor.Clone()).ToList();
        var serial = _cfg.Serial.Clone();
        serial.SlaveAddress = Enumerable.Range(1, 247).FirstOrDefault(address =>
            !list.Any(sensor => string.Equals(sensor.Serial.PortName, serial.PortName, StringComparison.OrdinalIgnoreCase) && sensor.Serial.SlaveAddress == address));
        if (serial.SlaveAddress == 0) { DeviceFeedback.Text = "当前串口的 247 个地址已用完，请使用其他串口。"; return; }
        int suffix = 1;
        while (list.Any(sensor => sensor.Device.DisplayName == $"传感器{suffix}")) suffix++;
        var added = new SensorConfiguration { Device = new DeviceIdentity { DisplayName = $"传感器{suffix}" }, Serial = serial, Enabled = false };
        list.Add(added);
        var error = CommitSensorList(list, added.Device.DeviceId, validate: false);
        DeviceFeedback.Text = error.Length > 0 ? error : "已增加传感器草稿。填写名称、串口和实物地址，勾选启用后保存设备信息。";
    }

    private void RemoveSensor_Click(object sender, RoutedEventArgs e)
    {
        if (_cfg.Sensors.Count == 1) { DeviceFeedback.Text = "至少保留一个传感器；暂不使用时可取消启用。"; return; }
        var removed = _cfg.Device.DisplayName;
        var list = _cfg.Sensors.Where(sensor => sensor.Device.DeviceId != _cfg.SelectedSensorId).Select(sensor => sensor.Clone()).ToList();
        var error = CommitSensorList(list, list[0].Device.DeviceId, validate: false);
        DeviceFeedback.Text = error.Length > 0 ? error : $"已移除 {removed}，原记录可在历史报表的历史设备中查询。";
    }

    private void SaveSensorThresholds_Click(object sender, RoutedEventArgs e)
    {
        double? Parse(TextBox box)
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return null;
            if (!double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new InvalidOperationException("上下限必须填写有效数字，或留空关闭该项。");
            return value;
        }
        try
        {
            var threshold = new ThresholdSnapshot
            {
                VersionId = ThresholdVersions.Next(), EffectiveFromUtc = DateTimeOffset.UtcNow, Source = "传感器上下限维护",
                TemperatureLow = Parse(SensorTempLow), TemperatureHigh = Parse(SensorTempHigh),
                HumidityLow = Parse(SensorHumLow), HumidityHigh = Parse(SensorHumHigh)
            };
            var list = _cfg.Sensors.Select(sensor => sensor.Clone()).ToList();
            list.First(sensor => sensor.Device.DeviceId == _cfg.SelectedSensorId).Thresholds = threshold;
            var error = CommitSensorList(list, _cfg.SelectedSensorId);
            SensorThresholdFeedback.Text = error.Length > 0 ? error : "已保存当前传感器上下限，新采集按新版本判断；原历史上下限保持原样。";
        }
        catch (Exception ex) { SensorThresholdFeedback.Text = ex.Message; }
    }

    /// <summary>首次向导保存后刷新主界面和实际使用的数据库，再开始采集。</summary>
    public void ApplyWizardConfiguration()
    {
        ConfigStore.CommitSelectedSensor(_cfg);
        ConfigStore.NormalizeSensors(_cfg);
        PopulateFromConfig();
        PopulateSensorSelectors();
        ApplyStorage_Click(this, new RoutedEventArgs());
        RefreshHistorySensors();
    }
}
