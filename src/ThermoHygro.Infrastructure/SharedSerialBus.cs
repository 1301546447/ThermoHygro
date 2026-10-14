using ThermoHygro.Core;

namespace ThermoHygro.Infrastructure;

/// <summary>同一串口只打开一次。收发互斥覆盖完整请求和回复，传感器不会争用端口或串帧。</summary>
internal sealed class SharedSerialBus : IDisposable
{
    private readonly ISerialTransport _transport;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly HashSet<Lease> _owners = new();
    private SerialSettings? _line;

    public SharedSerialBus(ISerialTransport transport) => _transport = transport;
    public ISerialTransport CreateLease() => new Lease(this);

    private string Open(Lease lease, string port, SerialSettings settings)
    {
        _requests.Wait();
        try
        {
            if (_line is not null && _owners.Count > 0 && !SensorConfigurationRules.SameLine(_line, settings))
                return $"串口 {port} 的线路参数不一致。";
            if (!_transport.IsOpen)
            {
                string error = _transport.Open(port, settings);
                if (error.Length > 0) return error;
            }
            _line = settings.Clone();
            _owners.Add(lease);
            return "";
        }
        finally { _requests.Release(); }
    }

    private void Close(Lease lease)
    {
        _requests.Wait();
        try
        {
            _owners.Remove(lease);
            if (_owners.Count == 0) _transport.Close();
        }
        finally { _requests.Release(); }
    }

    private async Task<TransportResult> Request(Lease lease, byte[] request, int length, byte address, int timeout, CancellationToken ct)
    {
        await _requests.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_owners.Contains(lease)) return new() { Error = "采集已停止", ErrorKind = "NotOpen" };
            return await _transport.RequestAsync(request, length, address, timeout, ct).ConfigureAwait(false);
        }
        finally { _requests.Release(); }
    }

    public void Dispose()
    {
        _requests.Wait();
        try { _owners.Clear(); _transport.Dispose(); }
        finally { _requests.Release(); }
        // 不立即 Dispose 信号量，避免退出期间已经排队的取消任务访问被释放的句柄。
    }

    private sealed class Lease(SharedSerialBus bus) : ISerialTransport
    {
        private bool _opened;
        public bool IsOpen => _opened && bus._transport.IsOpen;
        public string PortName => bus._transport.PortName;
        public string Open(string portName, SerialSettings settings)
        {
            string error = bus.Open(this, portName, settings);
            _opened = error.Length == 0;
            return error;
        }
        public void Close() { bus.Close(this); _opened = false; }
        public Task<TransportResult> RequestAsync(byte[] request, int expectedLength, byte expectedAddress, int timeoutMs, CancellationToken ct) =>
            bus.Request(this, request, expectedLength, expectedAddress, timeoutMs, ct);
        public void PurgeInput()
        {
            bus._requests.Wait();
            try { bus._transport.PurgeInput(); }
            finally { bus._requests.Release(); }
        }
        public void Dispose() => Close();
    }
}
