using System.IO.Ports;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BaldLight.Output;

/// <summary>One USB serial port as Windows currently sees it.</summary>
public readonly record struct UsbSerialPort(string PortName, string Vid, string Pid)
{
    public string UsbId => $"{Vid}:{Pid}";
    public override string ToString() => $"{PortName} ({UsbId})";
}

/// <summary>
/// Finds the controller by USB vendor and product id instead of trusting a
/// hard-coded COM name. Windows renumbers adapters after a hub change or a driver
/// update, and a pinned "COM6" is one of the ways Prismatik ends up talking to
/// nothing.
///
/// Walking the USB enum tree rather than SerialPort.GetPortNames also means the
/// motherboard's own COM1 is never mistaken for the controller.
/// </summary>
public static class SerialPortLocator
{
    public static string? Locate(IEnumerable<string> usbIds, string? overridePort)
    {
        var available = SafePortNames();

        if (!string.IsNullOrWhiteSpace(overridePort))
        {
            var forced = overridePort.Trim();
            if (available.Contains(forced)) return forced;
            Log.Warn($"Configured port {forced} is not present; falling back to USB id lookup");
        }

        var usbPorts = EnumerateUsbSerialPorts()
            .Where(p => available.Contains(p.PortName))
            .ToList();

        if (usbPorts.Count == 0)
        {
            Log.Debug("No USB serial ports are enumerated at all");
            return null;
        }

        var wanted = new HashSet<string>(
            usbIds.Select(NormaliseId).Where(s => s != null)!,
            StringComparer.OrdinalIgnoreCase);

        var match = usbPorts.FirstOrDefault(p => wanted.Contains(p.UsbId));
        if (match.PortName != null)
        {
            Log.Debug($"Matched configured USB id: {match}");
            return match.PortName;
        }

        // Nothing in the list matched. If the machine has exactly one USB serial
        // device, it is almost certainly the controller, so use it rather than
        // making someone look up the vid/pid of an unlisted adapter.
        if (usbPorts.Count == 1)
        {
            Log.Info($"No configured USB id matched; using the only USB serial port present: {usbPorts[0]}");
            return usbPorts[0].PortName;
        }

        Log.Warn($"No configured USB id matched, and {usbPorts.Count} USB serial ports are present " +
                 $"({string.Join(", ", usbPorts)}). Set serial.portOverride or add the id to serial.usbIds.");
        return null;
    }

    /// <summary>Every USB-backed COM port, with the ids Windows filed it under.</summary>
    public static List<UsbSerialPort> EnumerateUsbSerialPorts()
    {
        var found = new List<UsbSerialPort>();

        RegistryKey? usb = null;
        try
        {
            usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not open the USB enum key: {ex.Message}");
        }

        if (usb == null) return found;

        using (usb)
        {
            foreach (var deviceName in SafeSubKeys(usb))
            {
                var ids = Regex.Match(deviceName, @"^VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
                if (!ids.Success) continue;

                var vid = ids.Groups[1].Value.ToUpperInvariant();
                var pid = ids.Groups[2].Value.ToUpperInvariant();

                RegistryKey? device = null;
                try { device = usb.OpenSubKey(deviceName); } catch { }
                if (device == null) continue;

                using (device)
                {
                    foreach (var instance in SafeSubKeys(device))
                    {
                        string? portName = null;
                        try
                        {
                            using var parameters = device.OpenSubKey($@"{instance}\Device Parameters");
                            portName = parameters?.GetValue("PortName") as string;
                        }
                        catch (Exception ex)
                        {
                            Log.Debug($"Could not read PortName for {deviceName}\\{instance}: {ex.Message}");
                        }

                        if (!string.IsNullOrWhiteSpace(portName))
                            found.Add(new UsbSerialPort(portName.Trim(), vid, pid));
                    }
                }
            }
        }

        return found;
    }

    private static string? NormaliseId(string raw)
    {
        var m = Regex.Match(raw?.Trim() ?? "", @"^([0-9A-Fa-f]{4})\s*[:_-]?\s*([0-9A-Fa-f]{4})$");
        return m.Success
            ? $"{m.Groups[1].Value.ToUpperInvariant()}:{m.Groups[2].Value.ToUpperInvariant()}"
            : null;
    }

    private static string[] SafeSubKeys(RegistryKey key)
    {
        try { return key.GetSubKeyNames(); }
        catch { return Array.Empty<string>(); }
    }

    private static HashSet<string> SafePortNames()
    {
        try
        {
            return new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Error("Enumerating serial ports failed", ex);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
