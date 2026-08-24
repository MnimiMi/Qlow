using System.IO.Ports;

namespace Qlow.Output;

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

    /// <summary>
    /// How many times the controller has announced itself since the port was opened.
    /// Anything beyond the greeting at connect means the board rebooted underneath us,
    /// which the serial handle cannot see: on a Nano the USB bridge and the MCU are
    /// separate chips, so a brownout resets the MCU while the COM port stays healthy
    /// and every write keeps succeeding.
    /// </summary>
    public long ControllerReboots { get; private set; }
    public DateTime? LastRebootUtc { get; private set; }

    private readonly byte[] _rxBuffer = new byte[512];
    private readonly System.Text.StringBuilder _rxLine = new(80);
    private int _rxMatch;
    private bool _loggedFirstRx;

    /// <summary>Reset cause the controller reported on its last boot, if it reports one.</summary>
    public string? LastResetCause { get; private set; }

    /// <summary>Most recent supply and survival telemetry from the controller.</summary>
    public string? LastDiagnostics { get; private set; }

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
            Name = "Qlow.Serial",
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

        WriteHeader(_packet, ledCount);
        rgb[..Math.Min(payload, rgb.Length)].CopyTo(_packet.AsSpan(HeaderLength));
        _ledCount = ledCount;
    }

    /// <summary>Bytes of framing in front of the colour data.</summary>
    public const int HeaderLength = 6;

    /// <summary>
    /// Writes the Adalight framing: the magic word, the LED count minus one as
    /// big-endian 16 bit, then a checksum over those two bytes. The checksum is
    /// what lets the firmware find the start of a frame in the middle of a stream
    /// after a truncated write, instead of staying desynchronised forever.
    /// </summary>
    public static void WriteHeader(Span<byte> destination, int ledCount)
    {
        if (destination.Length < HeaderLength)
            throw new ArgumentException($"Need at least {HeaderLength} bytes", nameof(destination));

        var adjusted = ledCount - 1;
        var hi = (byte)((adjusted >> 8) & 0xFF);
        var lo = (byte)(adjusted & 0xFF);

        destination[0] = (byte)'A';
        destination[1] = (byte)'d';
        destination[2] = (byte)'a';
        destination[3] = hi;
        destination[4] = lo;
        destination[5] = (byte)(hi ^ lo ^ 0x55);
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

                DrainIncoming();
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException
                                           or InvalidOperationException or ObjectDisposedException)
            {
                // The endpoint is gone. Keeping the handle open would leave the app
                // looking healthy while writing into nothing, so close it, forget the
                // port, and go find the device again.
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
            _rxMatch = 0;
            _rxLine.Clear();
            _loggedFirstRx = false;
            LastResetCause = null;
            LastRebootUtc = null;
            ControllerReboots = 0;

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

    /// <summary>
    /// Reads whatever the controller has sent back and watches for its boot greeting.
    /// The stock Adalight sketch writes "Ada\n" once on startup, right after flashing
    /// the strip red, green and blue as a wiring test. Seeing that mid-session is
    /// proof the board reset, and it is the only signal there is: the write side
    /// stays perfectly happy throughout.
    /// </summary>
    private void DrainIncoming()
    {
        var port = _port;
        if (port is not { IsOpen: true }) return;

        int available;
        try { available = port.BytesToRead; }
        catch { return; }
        if (available <= 0) return;

        int read;
        try { read = port.Read(_rxBuffer, 0, Math.Min(available, _rxBuffer.Length)); }
        catch { return; }
        if (read <= 0) return;

        if (!_loggedFirstRx)
        {
            _loggedFirstRx = true;
            Log.Info($"Controller greeting: {Printable(_rxBuffer, read)}");
        }
        else
        {
            Log.Debug($"Controller sent {read} bytes: {Printable(_rxBuffer, read)}");
        }

        for (var i = 0; i < read; i++)
        {
            var b = _rxBuffer[i];

            // Watch for the boot greeting.
            var expected = _rxMatch switch { 0 => (byte)'A', 1 => (byte)'d', _ => (byte)'a' };
            if (b == expected)
            {
                if (++_rxMatch == 3)
                {
                    _rxMatch = 0;
                    NoteReboot();
                }
            }
            else
            {
                _rxMatch = b == (byte)'A' ? 1 : 0;
            }

            // Independently, reassemble text lines so a firmware that reports its
            // reset cause can be understood.
            if (b == (byte)'\n')
            {
                InterpretLine(_rxLine.ToString());
                _rxLine.Clear();
            }
            else if (b is >= 32 and < 127)
            {
                if (_rxLine.Length < 80) _rxLine.Append((char)b);
            }
        }
    }

    /// <summary>
    /// The bundled firmware reports why the chip reset, straight out of MCUSR. That
    /// turns a repeating fault from something to speculate about into something with
    /// a named cause: a brownout, a pulled RESET pin, a watchdog, or a lost supply.
    /// Older sketches say nothing, and that is fine.
    /// </summary>
    private void InterpretLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return;

        // Supply and survival telemetry: "diag ram=KEPT boot=3 vcc=4890 vmin=4720".
        // vmin is the lowest supply the chip has measured since it last lost power,
        // which is the number that settles whether the supply is actually sagging.
        if (line.StartsWith("diag ", StringComparison.OrdinalIgnoreCase))
        {
            LastDiagnostics = line[5..].Trim();

            // The controller caught an interrupt with no handler. That is a restart
            // with a named cause rather than a mystery, so it must not be lost in
            // the routine telemetry.
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"badisr=[1-9]"))
            {
                Log.Warn($"Controller restarted from an unhandled interrupt -- {line}");
                return;
            }

            // Free bytes between the stack and the variables, at its lowest. Close to
            // zero means the stack has been running into them, which corrupts return
            // addresses and sends the chip back to address zero.
            var free = System.Text.RegularExpressions.Regex.Match(line, @"freemin=(\d+)");
            if (free.Success && int.TryParse(free.Groups[1].Value, out var bytes) && bytes is > 0 and < 200)
            {
                Log.Warn($"Controller is down to {bytes} bytes of free RAM -- {line}");
                return;
            }

            var vmin = System.Text.RegularExpressions.Regex.Match(line, @"vmin=(\d+)");
            if (vmin.Success && int.TryParse(vmin.Groups[1].Value, out var mv) && mv is > 0 and < 4400)
                Log.Warn($"Controller supply dipped to {mv} mV -- {line}");
            else
                Log.Info($"Controller {line}");

            return;
        }

        if (!line.StartsWith("rst=", StringComparison.OrdinalIgnoreCase))
        {
            Log.Debug($"Controller said: {line}");
            return;
        }

        LastResetCause = line[4..].Trim();

        var meaning = LastResetCause switch
        {
            var s when s.Contains("BROWNOUT", StringComparison.OrdinalIgnoreCase) =>
                "supply dipped below the brown-out threshold: current, wiring or the supply itself",
            var s when s.Contains("EXTERNAL", StringComparison.OrdinalIgnoreCase) =>
                "something pulled the RESET pin low: DTR from the USB bridge, or noise on that line",
            var s when s.Contains("WDT", StringComparison.OrdinalIgnoreCase) =>
                "the sketch stopped feeding the watchdog, so the firmware hung",
            var s when s.Contains("POWERON", StringComparison.OrdinalIgnoreCase) =>
                "the supply went away completely and came back: a break in the power path",
            _ => "cause not reported"
        };

        Log.Warn($"Controller reset cause: {LastResetCause} -- {meaning}");
    }

    private void NoteReboot()
    {
        var now = DateTime.UtcNow;
        var previous = LastRebootUtc;
        LastRebootUtc = now;
        ControllerReboots++;

        var gap = previous.HasValue
            ? $"{(now - previous.Value).TotalSeconds:F1}s since the last one"
            : "first since the port opened";

        Log.Warn($"Controller rebooted ({gap}, {ControllerReboots} total). " +
                 "The serial link never dropped, so this is the board resetting on its own: " +
                 "brownout, a loose supply or data lead, or a watchdog in the sketch.");
    }

    private static string Printable(byte[] buffer, int count)
    {
        var chars = new char[count];
        for (var i = 0; i < count; i++)
        {
            var b = buffer[i];
            chars[i] = b is >= 32 and < 127 ? (char)b : '.';
        }
        return $"\"{new string(chars)}\"";
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
