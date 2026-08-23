using System.IO.Ports;

namespace BaldLight.Output;

/// <summary>
/// Owns the serial connection and is the only thing allowed to touch it.
///
/// Three properties matter here, and each one maps to a way the old setup went dark:
///
///  * Heartbeat. The frame is resent at least every HeartbeatMs even when nothing
///    on screen changed, so an Adalight sketch that blanks on timeout never does.
///  * Latest-frame-wins. Frames are handed over through a single slot, never a
///    queue, so a slow link drops stale frames instead of accumulating lag.
///  * Unconditional reconnect. Any write failure closes the port and re-locates the
///    device with backoff, forever, rather than leaving a dead handle open.
/// </summary>
public sealed class AdalightLink : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);

    private SerialConfig _config;
    private WatchdogConfig _watchdog;

    private Thread? _thread;
    private volatile bool _running;

    private SerialPort? _port;
    private byte[] _packet = Array.Empty<byte>();
    private int _ledCount;
    private bool _hasFrame;
    private volatile string? _reconnectReason;

    public bool IsConnected { get; private set; }
    public string? PortName { get; private set; }
    public DateTime LastWriteUtc { get; private set; }
    public long FramesWritten { get; private set; }

    public event Action? StateChanged;

    public AdalightLink(SerialConfig config, WatchdogConfig watchdog)
    {
        _config = config;
        _watchdog = watchdog;
    }

    public void UpdateConfig(SerialConfig config, WatchdogConfig watchdog)
    {
        lock (_gate)
        {
            var reopen = config.BaudRate != _config.BaudRate
                         || !string.Equals(config.PortOverride, _config.PortOverride, StringComparison.OrdinalIgnoreCase)
                         || !config.UsbIds.SequenceEqual(_config.UsbIds, StringComparer.OrdinalIgnoreCase);

            _config = config;
            _watchdog = watchdog;

            if (reopen) ClosePort("configuration changed");
        }
        _wake.Set();
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "BaldLight.Serial",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    public void Stop(bool blackout)
    {
        _running = false;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;

        lock (_gate)
        {
            if (blackout && _port is { IsOpen: true } && _ledCount > 0)
            {
                try
                {
                    BuildPacket(new byte[_ledCount * 3], _ledCount);
                    _port.Write(_packet, 0, _packet.Length);
                    // Give the UART time to drain before the handle goes away.
                    Thread.Sleep(60);
                }
                catch (Exception ex)
                {
                    Log.Debug($"Blackout write failed: {ex.Message}");
                }
            }
            ClosePort("shutting down");
        }
    }

    /// <summary>
    /// Asks the writer thread to drop the port and find the device again. Used after
    /// resume from sleep and session unlock, where the handle often survives on paper
    /// but the endpoint behind it has gone.
    /// </summary>
    public void ForceReconnect(string reason)
    {
        _reconnectReason = reason;
        _wake.Set();
    }

    /// <summary>Hands over the newest frame. Never blocks, never queues.</summary>
    public void Submit(ReadOnlySpan<byte> rgb, int ledCount)
    {
        lock (_gate)
        {
            BuildPacket(rgb, ledCount);
            _hasFrame = true;
        }
        _wake.Set();
    }

    public void SubmitBlack(int ledCount)
    {
        Submit(new byte[ledCount * 3], ledCount);
    }

    /// <summary>
    /// Adalight framing: the magic word, the LED count minus one as big-endian
    /// 16 bit, then a checksum that lets the firmware resynchronise mid-stream.
    /// </summary>
    private void BuildPacket(ReadOnlySpan<byte> rgb, int ledCount)
    {
        var payload = ledCount * 3;
        var total = 6 + payload;
        if (_packet.Length != total) _packet = new byte[total];

        var adjusted = ledCount - 1;
        var hi = (byte)((adjusted >> 8) & 0xFF);
        var lo = (byte)(adjusted & 0xFF);

        _packet[0] = (byte)'A';
        _packet[1] = (byte)'d';
        _packet[2] = (byte)'a';
        _packet[3] = hi;
        _packet[4] = lo;
        _packet[5] = (byte)(hi ^ lo ^ 0x55);

        rgb[..Math.Min(payload, rgb.Length)].CopyTo(_packet.AsSpan(6));
        _ledCount = ledCount;
    }

    private void Loop()
    {
        var backoff = _watchdog.ReconnectMinMs;

        while (_running)
        {
            try
            {
                var pending = _reconnectReason;
                if (pending != null)
                {
                    _reconnectReason = null;
                    ClosePort(pending);
                }

                if (_port is not { IsOpen: true })
                {
                    if (TryOpen())
                    {
                        backoff = _watchdog.ReconnectMinMs;
                    }
                    else
                    {
                        _wake.WaitOne(backoff);
                        backoff = Math.Min(backoff * 2, Math.Max(_watchdog.ReconnectMaxMs, _watchdog.ReconnectMinMs));
                        continue;
                    }
                }

                // Wake on a new frame, or on the heartbeat interval, whichever comes first.
                _wake.WaitOne(Math.Max(10, _watchdog.HeartbeatMs));
                if (!_running) break;

                byte[] packet;
                int length;
                lock (_gate)
                {
                    if (!_hasFrame || _packet.Length == 0) continue;
                    packet = _packet;
                    length = _packet.Length;
                }

                _port!.Write(packet, 0, length);
                LastWriteUtc = DateTime.UtcNow;
                FramesWritten++;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException
                                           or InvalidOperationException or ObjectDisposedException)
            {
                // This is the failure Prismatik swallows: the handle is dead but the
                // app keeps writing into it. Close, forget the port, and re-locate.
                Log.Warn($"Serial write failed on {PortName}: {ex.GetType().Name}: {ex.Message}");
                ClosePort("write failed");
                _wake.WaitOne(backoff);
                backoff = Math.Min(backoff * 2, Math.Max(_watchdog.ReconnectMaxMs, _watchdog.ReconnectMinMs));
            }
            catch (Exception ex)
            {
                Log.Error("Unexpected serial error", ex);
                ClosePort("unexpected error");
                _wake.WaitOne(backoff);
            }
        }
    }

    private bool TryOpen()
    {
        string? name;
        SerialConfig cfg;
        lock (_gate) cfg = _config;

        name = SerialPortLocator.Locate(cfg.UsbIds, cfg.PortOverride);
        if (name == null)
        {
            if (IsConnected || PortName != null)
            {
                Log.Warn("No matching USB serial device is present");
                PortName = null;
                SetConnected(false);
            }
            return false;
        }

        try
        {
            var port = new SerialPort(name, cfg.BaudRate, Parity.None, 8, StopBits.One)
            {
                WriteTimeout = 1000,
                ReadTimeout = 500,
                WriteBufferSize = 8192,
                ReadBufferSize = 4096,
                // On a Nano, DTR is capacitively coupled to RESET. Leaving both lines
                // de-asserted stops Windows from bouncing the board every time the
                // handle is opened, which is what makes the strip blink out.
                DtrEnable = false,
                RtsEnable = false,
                Handshake = Handshake.None
            };

            port.Open();
            port.DiscardOutBuffer();
            port.DiscardInBuffer();

            PortName = name;
            _port = port;

            // The AVR bootloader listens for a moment before handing over to the
            // sketch; bytes sent during that window are lost or misread.
            if (cfg.BootDelayMs > 0) Thread.Sleep(cfg.BootDelayMs);

            Log.Info($"Serial open on {name} at {cfg.BaudRate} baud");
            SetConnected(true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {name}: {ex.GetType().Name}: {ex.Message}");
            SetConnected(false);
            return false;
        }
    }

    private void ClosePort(string reason)
    {
        if (_port == null) return;

        Log.Info($"Closing {PortName}: {reason}");
        try { if (_port.IsOpen) _port.Close(); } catch { }
        try { _port.Dispose(); } catch { }
        _port = null;
        SetConnected(false);
    }

    private void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        try { StateChanged?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        Stop(blackout: false);
        _wake.Dispose();
    }
}
