using ThermoHygro.Core;

namespace ThermoHygro.Infrastructure;

/// <summary>各传感器各自采集和保存；同一串口共享一个总线，多个串口可并行工作。</summary>
public sealed class MultiSensorAcquisitionService : IDisposable
{
    private readonly Func<string, ISerialTransport> _factory;
    private readonly IClock _clock;
    private readonly Dictionary<string, AcquisitionService> _services = new();
    private readonly List<SharedSerialBus> _buses = new();
    private readonly Dictionary<string, AcquisitionSnapshot> _snapshots = new();
    private readonly object _gate = new();
    private List<SensorConfiguration> _sensors = new();
    private ILocalStore? _store;
    private IRemoteSink? _remote;

    public MultiSensorAcquisitionService(Func<string, ISerialTransport>? factory = null, IClock? clock = null)
    { _factory = factory ?? (_ => new SerialTransport()); _clock = clock ?? new SystemClock(); }

    public string SelectedSensorId { get; set; } = "";
    public event Action<AcquisitionSnapshot>? SnapshotChanged;
    public Action<string>? AppLogBridge { get; set; }
    public bool IsRunning => _services.Values.Any(service => service.IsRunning);
    public AcquisitionSnapshot Current => SnapshotFor(SelectedSensorId);

    public AcquisitionSnapshot SnapshotFor(string deviceId)
    {
        lock (_gate)
        {
            if (_snapshots.TryGetValue(deviceId, out var value)) return value;
            var sensor = _sensors.FirstOrDefault(s => s.Device.DeviceId == deviceId);
            return new AcquisitionSnapshot { DeviceId = deviceId, DeviceLabel = sensor?.Device.DisplayName ?? "", StatusText = sensor?.Enabled == false ? "已停用" : "尚未开始采集" };
        }
    }

    public TimeSpan FreshnessWindow => FreshnessFor(SelectedSensorId);
    public TimeSpan FreshnessFor(string deviceId)
    {
        var sensor = _sensors.FirstOrDefault(s => s.Device.DeviceId == deviceId);
        if (sensor is null) return TimeSpan.FromSeconds(15);
        double busSeconds = _sensors.Where(s => s.Enabled && string.Equals(s.Serial.PortName, sensor.Serial.PortName, StringComparison.OrdinalIgnoreCase))
            .Sum(s => s.Serial.ReadTimeoutMs * 2 / 1000.0);
        return TimeSpan.FromSeconds(Math.Max(3 * sensor.Serial.PollIntervalSeconds, busSeconds + 2));
    }

    public string Start(IReadOnlyList<SensorConfiguration> sensors)
    {
        string invalid = SensorConfigurationRules.Validate(sensors);
        if (invalid.Length > 0) return invalid;
        if (!sensors.Any(s => s.Enabled))
        {
            StopAsync().GetAwaiter().GetResult(); ConfigureIdle(sensors);
            return "所有传感器已停用，采集已停止。";
        }
        StopAsync().GetAwaiter().GetResult();
        _sensors = sensors.Select(s => s.Clone()).ToList();
        lock (_gate) _snapshots.Clear();
        var errors = new List<string>();
        foreach (var group in _sensors.Where(s => s.Enabled).GroupBy(s => s.Serial.PortName.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var bus = new SharedSerialBus(_factory(group.Key));
            _buses.Add(bus);
            foreach (var sensor in group)
            {
                var service = new AcquisitionService(bus.CreateLease(), _clock, _store, _remote);
                service.AppLogBridge = message => AppLogBridge?.Invoke($"{sensor.Device.DisplayName}：{message}");
                service.SnapshotChanged += snap => Publish(sensor, snap);
                _services.Add(sensor.Device.DeviceId, service);
                string error = service.Start(sensor.Serial, sensor.Device, sensor.Thresholds);
                if (error.Length > 0) errors.Add($"{sensor.Device.DisplayName}：{error}");
            }
        }
        return string.Join("；", errors);
    }

    public void ConfigureIdle(IReadOnlyList<SensorConfiguration> sensors)
    {
        if (IsRunning) return;
        _sensors = sensors.Select(sensor => sensor.Clone()).ToList();
        lock (_gate) _snapshots.Clear();
    }

    private void Publish(SensorConfiguration sensor, AcquisitionSnapshot value)
    {
        var snapshot = new AcquisitionSnapshot
        {
            DeviceId = sensor.Device.DeviceId, DeviceLabel = sensor.Device.DisplayName,
            Reading = value.Reading, Collector = value.Collector, Storage = value.Storage, Remote = value.Remote,
            EvaluatedAtUtc = value.EvaluatedAtUtc, Sequence = value.Sequence, MissedSlots = value.MissedSlots,
            RoundCompleted = value.RoundCompleted, StatusText = value.StatusText
        };
        lock (_gate) _snapshots[sensor.Device.DeviceId] = snapshot;
        try { SnapshotChanged?.Invoke(snapshot); } catch { }
    }

    public void Pause() { foreach (var service in _services.Values) service.Pause(); }
    public async Task StopAsync()
    {
        foreach (var service in _services.Values) await service.StopAsync().ConfigureAwait(false);
        foreach (var service in _services.Values) service.Dispose();
        _services.Clear();
        foreach (var bus in _buses) bus.Dispose();
        _buses.Clear();
    }
    public void AttachStore(ILocalStore? store)
    { _store = store; foreach (var service in _services.Values) service.AttachStore(store); }
    public void AttachRemote(IRemoteSink? sink)
    { _remote = sink; foreach (var service in _services.Values) service.AttachRemote(sink); }

    public async Task<ReadingGroup> TestReadAsync(SerialSettings settings, CancellationToken ct)
    {
        if (IsRunning) throw new InvalidOperationException("请先暂停全部采集，再测试当前传感器。");
        using var transport = _factory(settings.PortName);
        using var service = new AcquisitionService(transport, _clock);
        return await service.TestReadAsync(settings, ct).ConfigureAwait(false);
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
