using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Qlow.Capture;

/// <summary>
/// A downscaled BGRA snapshot of the desktop. Width/Height are the mip dimensions,
/// not the monitor dimensions.
/// </summary>
public sealed class CapturedFrame
{
    public byte[] Pixels = Array.Empty<byte>();
    public int Width;
    public int Height;
    public int Stride;
}

/// <summary>
/// Why a grab returned what it did. The caller has to tell these apart: a screen
/// that simply is not changing is healthy and must not be mistaken for a broken
/// pipeline, or the recovery logic ends up fighting an idle desktop.
/// </summary>
public enum CaptureStatus
{
    /// <summary>Fresh content was captured.</summary>
    NewFrame,

    /// <summary>Duplication is fine, nothing on screen changed within the timeout.</summary>
    Unchanged,

    /// <summary>Duplication is gone or being rebuilt; no frame is available.</summary>
    Unavailable
}

/// <summary>
/// DXGI Desktop Duplication with unconditional recovery.
///
/// Duplication is lost on a long list of everyday events: entering or leaving
/// exclusive fullscreen, a GPU driver reset, the UAC secure desktop, a resolution
/// or refresh-rate change, the monitor powering down, an RDP session attaching.
/// Every one of those must be treated as "tear it all down and build it again".
/// Nothing here throws outward: the caller only ever sees a frame or a null.
/// </summary>
public sealed class DesktopDuplicator : IDisposable
{
    private readonly int _monitorIndex;
    private readonly int _desiredWidth;

    private IDXGIFactory1? _factory;
    private IDXGIAdapter1? _adapter;
    private IDXGIOutput1? _output1;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;

    private ID3D11Texture2D? _scratch;
    private ID3D11ShaderResourceView? _scratchSrv;
    private ID3D11Texture2D? _staging;

    private int _sourceWidth;
    private int _sourceHeight;
    private int _mipLevel;
    private int _mipWidth;
    private int _mipHeight;

    private bool _frameHeld;
    private CapturedFrame? _last;

    public bool IsReady => _duplication != null;
    public string Description { get; private set; } = "not initialised";

    /// <summary>
    /// Counts frames that actually went through the downscale, as opposed to calls
    /// that timed out and handed back the previous frame. Benchmarks need to tell
    /// those apart; nothing else does.
    /// </summary>
    public long FramesDownscaled { get; private set; }

    public int MipLevel => _mipLevel;
    public int MipWidth => _mipWidth;
    public int MipHeight => _mipHeight;

    public DesktopDuplicator(int monitorIndex, int desiredWidth)
    {
        _monitorIndex = Math.Max(0, monitorIndex);
        _desiredWidth = Math.Clamp(desiredWidth, 64, 1920);
    }

    /// <summary>
    /// Grabs a frame. Returns the previous frame when the desktop simply has not
    /// changed, and null when the pipeline is being rebuilt.
    /// </summary>
    public CapturedFrame? TryGrab(int timeoutMs, out CaptureStatus status)
    {
        if (!EnsureInitialised())
        {
            status = CaptureStatus.Unavailable;
            return null;
        }

        try
        {
            ReleaseHeldFrame();

            var result = _duplication!.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out var resource);

            if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            {
                // Nothing changed on screen. Not an error, and the strip should keep
                // showing whatever it already shows.
                status = CaptureStatus.Unchanged;
                return _last;
            }

            if (result.Failure)
            {
                Log.Warn($"AcquireNextFrame failed: {result} ({DescribeResult(result)}), rebuilding");
                Teardown();
                status = CaptureStatus.Unavailable;
                return null;
            }

            _frameHeld = true;

            using (resource)
            {
                // LastPresentTime == 0 means only the cursor moved.
                if (frameInfo.LastPresentTime == 0 || resource == null)
                {
                    status = CaptureStatus.Unchanged;
                    return _last;
                }

                using var desktop = resource.QueryInterface<ID3D11Texture2D>();
                _last = Downscale(desktop);
                status = CaptureStatus.NewFrame;
                return _last;
            }
        }
        catch (SharpGenException ex)
        {
            Log.Warn($"Capture threw {ex.ResultCode} ({DescribeResult(ex.ResultCode)}), rebuilding");
            Teardown();
            status = CaptureStatus.Unavailable;
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Capture threw, rebuilding", ex);
            Teardown();
            status = CaptureStatus.Unavailable;
            return null;
        }
    }

    /// <summary>Forces a full rebuild on the next grab.</summary>
    public void Invalidate(string reason)
    {
        if (_duplication != null || _device != null) Log.Info($"Capture invalidated: {reason}");
        Teardown();
    }

    private CapturedFrame Downscale(ID3D11Texture2D desktop)
    {
        var desc = desktop.Description;

        if (_scratch == null || desc.Width != _sourceWidth || desc.Height != _sourceHeight)
        {
            _sourceWidth = (int)desc.Width;
            _sourceHeight = (int)desc.Height;
            BuildScalingTextures();
        }

        // Copy the desktop into mip 0 of a mipped texture, let the GPU build the
        // chain, then pull down only the small mip. Reading the full 2560x1440
        // surface every frame would cost roughly 14 MB of PCIe traffic per frame.
        _context!.CopySubresourceRegion(_scratch!, 0, 0, 0, 0, desktop, 0);
        _context.GenerateMips(_scratchSrv!);
        _context.CopySubresourceRegion(_staging!, 0, 0, 0, 0, _scratch!, (uint)_mipLevel);

        var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var stride = (int)map.RowPitch;
            var frame = new CapturedFrame
            {
                Width = _mipWidth,
                Height = _mipHeight,
                Stride = stride,
                Pixels = new byte[stride * _mipHeight]
            };

            System.Runtime.InteropServices.Marshal.Copy(map.DataPointer, frame.Pixels, 0, frame.Pixels.Length);
            FramesDownscaled++;
            return frame;
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
    }

    private void BuildScalingTextures()
    {
        _scratchSrv?.Dispose();
        _scratch?.Dispose();
        _staging?.Dispose();

        // Pick the mip whose width is closest to the requested downscale width.
        _mipLevel = 0;
        while (_mipLevel < 12 && (_sourceWidth >> (_mipLevel + 1)) >= _desiredWidth) _mipLevel++;

        _mipWidth = Math.Max(1, _sourceWidth >> _mipLevel);
        _mipHeight = Math.Max(1, _sourceHeight >> _mipLevel);

        var scratchDesc = new Texture2DDescription
        {
            Width = (uint)_sourceWidth,
            Height = (uint)_sourceHeight,
            MipLevels = (uint)(_mipLevel + 1),
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.GenerateMips
        };

        _scratch = _device!.CreateTexture2D(scratchDesc);
        _scratchSrv = _device.CreateShaderResourceView(_scratch);

        var stagingDesc = new Texture2DDescription
        {
            Width = (uint)_mipWidth,
            Height = (uint)_mipHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };

        _staging = _device.CreateTexture2D(stagingDesc);

        Log.Info($"Capture scaling: {_sourceWidth}x{_sourceHeight} -> mip {_mipLevel} = {_mipWidth}x{_mipHeight}");
    }

    private bool EnsureInitialised()
    {
        if (_duplication != null) return true;

        try
        {
            _factory ??= DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            if (_factory.EnumAdapters1(0, out _adapter).Failure || _adapter == null)
            {
                Log.Warn("No DXGI adapter available");
                return false;
            }

            if (_adapter.EnumOutputs((uint)_monitorIndex, out var output).Failure || output == null)
            {
                // Monitor index out of range, e.g. a display was unplugged. Fall back
                // to the primary rather than going dark.
                if (_monitorIndex != 0 && _adapter.EnumOutputs(0, out output).Success && output != null)
                {
                    Log.Warn($"Monitor {_monitorIndex} unavailable, falling back to monitor 0");
                }
                else
                {
                    Log.Warn("No DXGI output available");
                    return false;
                }
            }

            using (output)
            {
                _output1 = output.QueryInterface<IDXGIOutput1>();
                Description = $"{output.Description.DeviceName} on adapter {_adapter.Description1.Description.Trim()}";
            }

            var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };

            var create = D3D11.D3D11CreateDevice(
                _adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                featureLevels,
                out _device,
                out _,
                out _context);

            if (create.Failure || _device == null || _context == null)
            {
                Log.Warn($"D3D11CreateDevice failed: {create}");
                Teardown();
                return false;
            }

            _duplication = _output1!.DuplicateOutput(_device);
            _sourceWidth = 0;
            _sourceHeight = 0;

            Log.Info($"Capture ready on {Description}");
            return true;
        }
        catch (SharpGenException ex)
        {
            // E_ACCESSDENIED here almost always means an exclusive-fullscreen app
            // owns the output, or another duplication client already holds it.
            Log.Warn($"Capture init failed: {ex.ResultCode} ({DescribeResult(ex.ResultCode)})");
            Teardown();
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Capture init failed", ex);
            Teardown();
            return false;
        }
    }

    private void ReleaseHeldFrame()
    {
        if (!_frameHeld) return;
        _frameHeld = false;
        try { _duplication?.ReleaseFrame(); } catch { }
    }

    /// <summary>
    /// Drops every DXGI and D3D object so the next grab builds them again.
    ///
    /// The last captured frame deliberately survives. It is still the most recent
    /// thing that was on screen, and discarding it means a rebuild on an idle desktop
    /// has nothing to return, which reads as a failure and triggers another rebuild.
    /// </summary>
    private void Teardown()
    {
        ReleaseHeldFrame();

        try { _duplication?.Dispose(); } catch { }
        try { _scratchSrv?.Dispose(); } catch { }
        try { _scratch?.Dispose(); } catch { }
        try { _staging?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        try { _output1?.Dispose(); } catch { }
        try { _adapter?.Dispose(); } catch { }
        try { _factory?.Dispose(); } catch { }

        _duplication = null;
        _scratchSrv = null;
        _scratch = null;
        _staging = null;
        _context = null;
        _device = null;
        _output1 = null;
        _adapter = null;
        _factory = null;
        _sourceWidth = 0;
        _sourceHeight = 0;
    }

    private static string DescribeResult(Result r)
    {
        if (r == Vortice.DXGI.ResultCode.AccessLost) return "desktop switch, fullscreen transition or mode change";
        if (r == Vortice.DXGI.ResultCode.DeviceRemoved) return "GPU removed or driver reset";
        if (r == Vortice.DXGI.ResultCode.DeviceReset) return "GPU reset";
        if (r == Vortice.DXGI.ResultCode.WaitTimeout) return "no new frame";
        if (r.Code == unchecked((int)0x80070005)) return "access denied, exclusive fullscreen or another duplication client";
        return "unknown";
    }

    public void Dispose() => Teardown();
}
