using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ThermoHygro.Core;
using ThermoHygro.Protocols;

namespace ThermoHygro.Infrastructure;

/// <summary>一轮采集的完整结果（含两个指标各自状态与三个独立系统状态）。</summary>
public sealed class AcquisitionSnapshot
{
    public string DeviceId { get; init; } = "";
    public ReadingGroup Reading { get; init; } = new();
    public CollectorState Collector { get; init; } = CollectorState.NotConfigured;
    public StorageState Storage { get; init; } = StorageState.Unknown;
    public RemoteSyncState Remote { get; init; } = RemoteSyncState.Disabled;
    /// <summary>过期判定时间（由独立计时产生，不依赖采集线程是否活着）。</summary>
    public DateTimeOffset EvaluatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string StatusText { get; init; } = "";
    public string DeviceLabel { get; init; } = "";
    /// <summary>本轮序号（每次尝试递增）。</summary>
    public long Sequence { get; init; }
    /// <summary>本轮是否整体成功。</summary>
    public bool RoundCompleted { get; init; }
    /// <summary>错过时隙次数（上一轮未完成时不叠加新轮次，计入错过）。</summary>
    public long MissedSlots { get; init; }
}

/// <summary>采集服务接口：串口与采集任务只有一份拥有者。</summary>
public interface IAcquisitionService : IDisposable
{
    AcquisitionSnapshot Current { get; }
    event Action<AcquisitionSnapshot>? SnapshotChanged;
    bool IsRunning { get; }
    string Start(SerialSettings settings, DeviceIdentity device, ThresholdSnapshot thresholds);
    void Pause();
    Task StopAsync();
    /// <summary>运行中应用新串口配置：验证 → 暂停 → 释放旧串口 → 应用 → 连接。</summary>
    Task<string> ApplySettingsAsync(SerialSettings settings);
    /// <summary>读取单次测试（用于向导"测试读取"）。</summary>
    Task<ReadingGroup> TestReadAsync(SerialSettings settings, CancellationToken ct);
}

/// <summary>
/// 采集实现。要点：
/// ①两指标串行请求，一轮最多两个请求，间隔默认 5 秒，不叠加轮次；
/// ②上一轮未完成则记"错过时隙"，不做无界追赶；
/// ③各指标独立新鲜度：失败立即标失败，不用旧值冒充本次值；
/// ④过期由独立界面计时判定，采集线程卡死也能变 --。
/// </summary>
public sealed class AcquisitionService : IAcquisitionService
{
    private readonly Ecm30Protocol _protocol = new();
    private readonly ISerialTransport _transport;
    private ILocalStore? _store;
    private IRemoteSink? _remote;
    private readonly IClock _clock;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private SerialSettings _settings = new();
    private DeviceIdentity _device = new();
    private ThresholdSnapshot _thresholds = new() { VersionId = 1 };
    private long _sequence;
    private long _missed;
    private readonly object _gate = new();
    private readonly EventDetector _detector = new();
    /// <summary>退避阶梯（秒）：1/2/5/10/30，连续失败时逐级上升，成功后归零。</summary>
    private static readonly int[] BackoffSeconds = { 1, 2, 5, 10, 30 };
    private int _consecutiveFailures;

    public AcquisitionService(ISerialTransport transport, IClock clock, ILocalStore? store = null, IRemoteSink? remote = null)
    {
        _transport = transport;
        _clock = clock;
        _store = store;
        _remote = remote;
    }

    public AcquisitionSnapshot Current { get; private set; } = new();
    public event Action<AcquisitionSnapshot>? SnapshotChanged;
    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>新鲜度阈值 = max(3 × 采集间隔, 单轮最长读取时间 + 2 秒)。</summary>
    public TimeSpan FreshnessWindow =>
        TimeSpan.FromSeconds(Math.Max(3 * _settings.PollIntervalSeconds,
            (_settings.ReadTimeoutMs * 2 / 1000.0) + 2));

    public string Start(SerialSettings settings, DeviceIdentity device, ThresholdSnapshot thresholds)
    {
        StopInternal();
        _settings = settings.Clone();
        _device = device;
        _thresholds = thresholds;
        _sequence = 0;
        _missed = 0;
        _consecutiveFailures = 0;

        string err = _transport.Open(_settings.PortName, _settings);
        if (err.Length > 0)
        {
            // 打开失败：明确"未连接"，绝不假装成功
            Publish(new AcquisitionSnapshot
            {
                Collector = CollectorState.Disconnected,
                Storage = StorageStateFor(),
                Remote = RemoteStateFor(),
                StatusText = err,
                DeviceLabel = _device.DisplayName,
                Sequence = 0
            });
            // 接入失败仍启动重试任务；USB 转串口恢复后可以重新连接。
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
            return err;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        Publish(new AcquisitionSnapshot
        {
            Collector = CollectorState.Connecting,
            Storage = StorageStateFor(),
            Remote = RemoteStateFor(),
            StatusText = "已打开串口，正在读取传感器…",
            DeviceLabel = _device.DisplayName
        });
        return "";
    }

    public void Pause()
    {
        var cts = _cts;
        if (cts is null) return;
        StopInternal();
        Publish(new AcquisitionSnapshot
        {
            Collector = CollectorState.Paused,
            Storage = StorageStateFor(),
            Remote = RemoteStateFor(),
            StatusText = "已暂停采集",
            DeviceLabel = _device.DisplayName,
            Sequence = _sequence,
            MissedSlots = _missed,
            Reading = Current.Reading
        });
    }

    public async Task StopAsync()
    {
        StopInternal();
        await Task.CompletedTask;
    }

    private void StopInternal()
    {
        var cts = _cts; var loop = _loop;
        _cts = null; _loop = null;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            try { loop?.Wait(TimeSpan.FromSeconds(5)); } catch { }
            try { cts.Dispose(); } catch { }
        }
        _transport.Close();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var scheduler = Stopwatch.StartNew();
        var nextDue = TimeSpan.Zero;
        while (!ct.IsCancellationRequested)
        {
            var started = _clock.UtcNow;
            _sequence++;

            ReadingGroup group;
            bool roundOk;
            try
            {
                group = await ReadRoundAsync(ct).ConfigureAwait(false);
                roundOk = !ct.IsCancellationRequested;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                group = FailedGroup(started, $"采集异常：{ex.GetType().Name}：{ex.Message}");
                roundOk = false;
            }

            if (ct.IsCancellationRequested) break;

            // 保存：只有提交成功才显示"已保存"
            var storageState = StorageStateFor();
            var remoteState = RemoteStateFor();
            if (_store is not null)
            {
                try
                {
                    var outcome = await _store.AppendSampleAsync(_device, _thresholds, group, ct).ConfigureAwait(false);
                    if (outcome.Saved)
                    {
                        storageState = StorageState.Ok;
                        _hasSaved = true;
                        _storageError = "";
                    }
                    else
                    {
                        // 保存返回失败时必须记住原因：否则下一轮 StorageStateFor() 会重新
                        // 报"已保存"，用户看不到任何异常（LastError 已在库端填好）。
                        storageState = StorageState.Fault;
                        _storageError = _store.LastError.Length > 0
                            ? _store.LastError
                            : (outcome.Error ?? "本地保存失败（原因未知）");
                    }
                }
                catch (Exception ex)
                {
                    storageState = StorageState.Fault;
                    _storageError = $"本地保存失败：{ex.Message}（当前读数不再记录，传感器值仍可查看）";
                }
            }

            var snap = Build(group, roundOk, storageState, remoteState);
            Publish(snap);

            // 写入监控事件（只在状态跨越时产生，不重复刷屏）
            if (_store is not null)
            {
                try
                {
                    var evts = _detector.Detect(_device.DeviceId, group, roundOk, storageState, remoteState, _thresholds.VersionId);
                    if (evts.Count > 0)
                    {
                        await _store.AppendEventsAsync(evts, ct).ConfigureAwait(false);
                        foreach (var ev in evts) AppLogBridge?.Invoke($"事件：{ev.EventType}｜{ev.Context}");
                    }
                }
                catch { /* 事件写入失败不影响采集与保存 */ }
            }

            // 调度：正常按固定节拍；连续失败时按退避阶梯（1/2/5/10/30 秒）重试
            var interval = TimeSpan.FromSeconds(Math.Max(1, _settings.PollIntervalSeconds));
            if (roundOk && group.AnySuccess)
            {
                _consecutiveFailures = 0;
                nextDue = nextDue.Add(interval);
            }
            else
            {
                _consecutiveFailures++;
                var step = BackoffSeconds[Math.Min(_consecutiveFailures - 1, BackoffSeconds.Length - 1)];
                nextDue = scheduler.Elapsed.Add(TimeSpan.FromSeconds(step));
            }
            var now = scheduler.Elapsed;
            if (nextDue <= now)
            {
                long behind = (long)Math.Ceiling((now - nextDue).TotalMilliseconds / interval.TotalMilliseconds);
                if (behind > 1)
                {
                    _missed += behind - 1;
                    nextDue = now;
                }
                else nextDue = now;
            }
            var wait = nextDue - now;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            try { await Task.Delay(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>事件日志回调（由界面注入，用于同步显示到日志页）；未注入时静默。</summary>
    public Action<string>? AppLogBridge { get; set; }

    /// <summary>当前退避级别（供界面显示）。</summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    private string _storageError = "";
    private bool _hasSaved;

    private AcquisitionSnapshot Build(ReadingGroup group, bool roundOk, StorageState storage, RemoteSyncState remote)
    {
        var now = _clock.UtcNow;
        var t = EvaluateFreshness(group.Temperature, MetricKind.Temperature, now);
        var h = EvaluateFreshness(group.Humidity, MetricKind.Humidity, now);

        var reading = new ReadingGroup
        {
            AttemptTimeUtc = group.AttemptTimeUtc,
            Temperature = t,
            Humidity = h
        };

        CollectorState state;
        string status;
        if (!roundOk)
        {
            state = CollectorState.Reconnecting;
            status = "本轮读取未完成";
        }
        else if (t.Quality == MetricQuality.Good && h.Quality == MetricQuality.Good)
        {
            state = CollectorState.Acquiring; status = "采集中";
        }
        else if (t.Quality == MetricQuality.Good || h.Quality == MetricQuality.Good)
        {
            state = CollectorState.PartiallyMissing; status = "部分指标缺测";
        }
        else if (t.Quality == MetricQuality.OutOfRange || h.Quality == MetricQuality.OutOfRange)
        {
            state = CollectorState.Acquiring; status = "采集中（存在超限）";
        }
        else
        {
            state = CollectorState.Disconnected; status = "读取失败，正在重试";
        }

        var text = status;
        if (storage == StorageState.Fault) text += " · 本地未保存";
        return new AcquisitionSnapshot
        {
            Reading = reading,
            Collector = state,
            Storage = storage,
            Remote = remote,
            EvaluatedAtUtc = now,
            StatusText = text,
            DeviceLabel = _device.DisplayName,
            Sequence = _sequence,
            RoundCompleted = roundOk,
            MissedSlots = _missed
        };
    }

    private MetricReading EvaluateFreshness(MetricReading r, MetricKind kind, DateTimeOffset now)
    {
        if (r.Quality == MetricQuality.NotSupported) return r;
        if (r.Quality == MetricQuality.Good || r.Quality == MetricQuality.OutOfRange)
        {
            // 独立计时判定过期：即使采集线程卡死，超过新鲜度窗口也显示 --
            if (r.TimestampUtc.HasValue && now - r.TimestampUtc.Value > FreshnessWindow)
                return new MetricReading
                {
                    Kind = kind,
                    Quality = MetricQuality.Stale,
                    Value = null,
                    Reason = $"读数已过期（超过 {FreshnessWindow.TotalSeconds:F0} 秒未更新）",
                    LastGoodTimestampUtc = r.TimestampUtc,
                    LastGoodValue = r.Value
                };
            return r;
        }
        return r;
    }

    private ReadingGroup FailedGroup(DateTimeOffset t, string reason) => new()
    {
        AttemptTimeUtc = t,
        Temperature = new MetricReading { Kind = MetricKind.Temperature, Quality = MetricQuality.Failed, Reason = reason },
        Humidity = new MetricReading { Kind = MetricKind.Humidity, Quality = MetricQuality.Failed, Reason = reason }
    };

    /// <summary>一轮 = 温度请求 + 湿度请求，严格串行；每指标独立成败。</summary>
    private async Task<ReadingGroup> ReadRoundAsync(CancellationToken ct)
    {
        var attempt = _clock.UtcNow;
        if (!_transport.IsOpen)
        {
            string error = _transport.Open(_settings.PortName, _settings);
            if (error.Length > 0) return FailedGroup(attempt, error);
        }
        var metrics = _protocol.SupportedMetrics; // [0]=温度 [1]=湿度
        MetricReading temp = MetricReading.Unread(MetricKind.Temperature);
        MetricReading hum = MetricReading.Unread(MetricKind.Humidity);

        for (int i = 0; i < metrics.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var m = metrics[i];
            var kind = m.MetricId == "temperature" ? MetricKind.Temperature : MetricKind.Humidity;

            byte[] req;
            try { req = _protocol.BuildReadRequest((byte)_settings.SlaveAddress, m); }
            catch (Exception ex)
            {
                var mr = new MetricReading { Kind = kind, Quality = MetricQuality.Failed, Reason = $"构建请求失败：{ex.Message}" };
                if (kind == MetricKind.Temperature) temp = mr; else hum = mr;
                continue;
            }

            TransportResult tr = await _transport
                .RequestAsync(req, _protocol.ExpectedResponseLength, (byte)_settings.SlaveAddress, _settings.ReadTimeoutMs, ct)
                .ConfigureAwait(false);

            if (tr.Success && tr.Frame is not null)
            {
                var pr = _protocol.ParseResponse(tr.Frame, (byte)_settings.SlaveAddress, m);
                if (pr.Success && pr.Value.HasValue)
                {
                    bool oor = _thresholds.IsOutOfRange(kind, pr.Value.Value);
                    var good = new MetricReading
                    {
                        Kind = kind,
                        Quality = oor ? MetricQuality.OutOfRange : MetricQuality.Good,
                        Value = pr.Value.Value,
                        TimestampUtc = _clock.UtcNow,
                        Reason = oor ? "超出已配置的上下限" : null
                    };
                    if (kind == MetricKind.Temperature) temp = good; else hum = good;
                }
                else
                {
                    var bad = new MetricReading { Kind = kind, Quality = MetricQuality.Failed, Reason = pr.ReasonText };
                    if (kind == MetricKind.Temperature) temp = bad; else hum = bad;
                }
            }
            else
            {
                var bad = new MetricReading
                {
                    Kind = kind,
                    Quality = MetricQuality.Failed,
                    Reason = tr.ErrorKind switch
                    {
                        "Timeout" => $"读取超时（{_settings.ReadTimeoutMs} 毫秒无完整回复）",
                        "NotOpen" => "串口未打开或被拔出",
                        "ReadError" => tr.Error,
                        "Canceled" => "已取消",
                        _ => tr.Error ?? "未知错误"
                    }
                };
                if (kind == MetricKind.Temperature) temp = bad; else hum = bad;
            }
        }

        return new ReadingGroup { AttemptTimeUtc = attempt, Temperature = temp, Humidity = hum };
    }

    /// <summary>向导"测试读取"：打开串口 ≠ 连接成功，必须分别给出温/湿结果。</summary>
    public async Task<ReadingGroup> TestReadAsync(SerialSettings settings, CancellationToken ct)
    {
        if (IsRunning) throw new InvalidOperationException("请先暂停采集，再测试读取。");
        var attempt = _clock.UtcNow;
        var savedSettings = _settings;
        var savedRunning = IsRunning;
        try
        {
            _settings = settings.Clone();
            string err = "只有先成功打开串口才能测试读取。";
            if (!_transport.IsOpen || _transport.PortName != settings.PortName)
            {
                _transport.Close();
                err = _transport.Open(settings.PortName, settings);
            }
            else err = "";

            if (err.Length > 0)
                return new ReadingGroup
                {
                    AttemptTimeUtc = attempt,
                    Temperature = new MetricReading { Kind = MetricKind.Temperature, Quality = MetricQuality.Failed, Reason = err },
                    Humidity = new MetricReading { Kind = MetricKind.Humidity, Quality = MetricQuality.Failed, Reason = err }
                };

            return await ReadRoundAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (!savedRunning)
            {
                _transport.Close();
                _settings = savedSettings;
            }
        }
    }

    private StorageState StorageStateFor()
    {
        if (_store is null) return StorageState.Unknown;
        if (_storageError.Length > 0) return StorageState.Fault;
        return _hasSaved ? StorageState.Ok : StorageState.Unknown;
    }

    private RemoteSyncState RemoteStateFor()
    {
        var r = _remote;
        if (r is null) return RemoteSyncState.Disabled;
        return r.State;
    }

    /// <summary>运行时绑定/解绑远端同步（界面切换存储模式时调用）。</summary>
    public void AttachRemote(IRemoteSink? sink) => _remote = sink;

    /// <summary>运行时绑定本地存储。界面在本地库就绪（或切换数据库路径）后调用，
    /// 否则采集到的样本不会写入任何库。</summary>
    public void AttachStore(ILocalStore? store)
    {
        _store = store;
        if (store is not null)
        {
            _storageError = "";
            _hasSaved = false;
            _detector.ResetStorageState();
        }
    }

    private void Publish(AcquisitionSnapshot snap)
    {
        lock (_gate) Current = snap;
        try { SnapshotChanged?.Invoke(snap); } catch { }
    }

    /// <summary>运行中修改串口：验证 → 暂停 → 持久化 → 释放旧串口 → 应用 → 重连；失败可回滚。</summary>
    public async Task<string> ApplySettingsAsync(SerialSettings settings)
    {
        var old = _settings.Clone();
        var oldRunning = IsRunning;

        // 1. 验证新值
        if (string.IsNullOrWhiteSpace(settings.PortName)) return "串口名称不能为空。";
        if (settings.BaudRate <= 0) return "波特率必须大于 0。";
        if (settings.SlaveAddress is < 1 or > 247) return "从站地址必须在 1～247 之间。";
        if (settings.PollIntervalSeconds is < 1 or > 60) return "采集间隔必须在 1～60 秒之间。";
        if (settings.ReadTimeoutMs < 200) return "读取超时至少 200 毫秒。";

        // 2. 暂停旧采集并释放旧串口
        StopInternal();
        await Task.Yield();

        // 3. 应用新配置并尝试连接
        _settings = settings.Clone();
        string err = _transport.Open(_settings.PortName, _settings);
        if (err.Length > 0)
        {
            Publish(new AcquisitionSnapshot
            {
                Collector = CollectorState.Disconnected,
                Storage = StorageStateFor(),
                Remote = RemoteStateFor(),
                StatusText = $"新串口配置未连接：{err}",
                DeviceLabel = _device.DisplayName,
                Sequence = _sequence
            });
            return err; // 调用方可一键恢复旧配置
        }

        if (oldRunning)
        {
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }
        Publish(new AcquisitionSnapshot
        {
            Collector = oldRunning ? CollectorState.Connecting : CollectorState.Paused,
            Storage = StorageStateFor(),
            Remote = RemoteStateFor(),
            StatusText = oldRunning ? "已应用新串口配置，正在读取…" : "已应用新串口配置（采集未启动）",
            DeviceLabel = _device.DisplayName,
            Sequence = _sequence
        });
        _lastGoodSettings = old;
        return "";
    }

    private SerialSettings? _lastGoodSettings;

    /// <summary>一键恢复上一次可用配置。</summary>
    public Task<string> RestorePreviousAsync() =>
        _lastGoodSettings is null ? Task.FromResult("没有可恢复的上一份配置。") : ApplySettingsAsync(_lastGoodSettings);

    public void Dispose() => StopInternal();
}
