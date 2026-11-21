using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DimmerLUT;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && TryRunCli(args, out var exitCode))
            return exitCode;

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static bool TryRunCli(string[] args, out int exitCode)
    {
        exitCode = 0;
        switch (args[0].ToLowerInvariant())
        {
            case "--list" or "-l" or "list":
                AttachToParentConsole();
                ListDisplays();
                return true;

            case "--set":
                AttachToParentConsole();
                if (args.Length < 2 || !int.TryParse(args[1], out var percent))
                {
                    Console.Error.WriteLine("--set requires a percentage between 1 and 100, e.g. --set 50");
                    exitCode = 2;
                    return true;
                }
                exitCode = SetAllCli(percent);
                return true;

            case "--restore":
                AttachToParentConsole();
                exitCode = RestoreAllCli();
                return true;

            case "--help" or "-h" or "-?" or "/?" or "help":
                AttachToParentConsole();
                PrintHelp();
                return true;

            default:
                if (args[0].StartsWith("-") || args[0].StartsWith("/"))
                {
                    AttachToParentConsole();
                    Console.Error.WriteLine($"Unknown option: {args[0]}");
                    Console.Error.WriteLine();
                    PrintHelp();
                    exitCode = 2;
                    return true;
                }
                return false;
        }
    }

    private static int SetAllCli(int percent)
    {
        percent = Math.Clamp(percent, 1, 100);

        var displays = NativeMethods.GetAttachedDisplays();
        if (displays.Count == 0)
        {
            Console.Error.WriteLine("No attached display devices were found.");
            return 1;
        }

        var originals = RampStore.Load();
        var errors = new List<string>();
        var applied = 0;

        foreach (var display in displays)
        {
            IntPtr hdc = NativeMethods.CreateDC("DISPLAY", display.DeviceName, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                errors.Add($"{display.DeviceName}: could not create a display device context");
                continue;
            }

            try
            {
                // Capture the pre-dim ramp exactly once; later --set runs reuse it.
                if (!originals.ContainsKey(display.DeviceName))
                {
                    if (!NativeMethods.TryGetDeviceGammaRamp(hdc, out var original))
                    {
                        errors.Add($"{display.DeviceName}: driver does not expose a readable gamma ramp");
                        continue;
                    }
                    originals[display.DeviceName] = original;
                }

                var ramp = GammaRamp.CreateScale(percent / 100.0);
                if (!NativeMethods.SetDeviceGammaRamp(hdc, ref ramp))
                {
                    errors.Add($"{display.DeviceName}: driver rejected the gamma ramp");
                    continue;
                }
                applied++;
            }
            finally
            {
                NativeMethods.DeleteDC(hdc);
            }
        }

        if (originals.Count > 0)
            RampStore.Save(originals);

        foreach (var e in errors)
            Console.Error.WriteLine($"  ! {e}");

        Console.WriteLine($"Applied {percent}% brightness to {applied} display(s).");
        if (applied > 0)
            Console.WriteLine("Run 'DimmerLUT.exe --restore' to restore the original ramps.");
        return applied > 0 ? 0 : 1;
    }

    private static int RestoreAllCli()
    {
        var originals = RampStore.Load();
        if (originals.Count == 0)
        {
            Console.Error.WriteLine("No saved original ramps found. Nothing to restore.");
            return 1;
        }

        var restored = new List<string>();
        var errors = new List<string>();

        foreach (var (deviceName, original) in originals)
        {
            IntPtr hdc = NativeMethods.CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                errors.Add($"{deviceName}: could not create a display device context");
                continue;
            }

            try
            {
                var ramp = original;
                if (!NativeMethods.SetDeviceGammaRamp(hdc, ref ramp))
                {
                    errors.Add($"{deviceName}: driver rejected the original gamma ramp");
                    continue;
                }
                restored.Add(deviceName);
            }
            finally
            {
                NativeMethods.DeleteDC(hdc);
            }
        }

        // Only drop entries that actually restored, so a temporarily-disconnected
        // monitor keeps its original for a later --restore.
        foreach (var name in restored)
            originals.Remove(name);

        if (originals.Count == 0)
            RampStore.Clear();
        else
            RampStore.Save(originals);

        foreach (var e in errors)
            Console.Error.WriteLine($"  ! {e}");

        Console.WriteLine($"Restored original ramps on {restored.Count} display(s).");
        return restored.Count > 0 ? 0 : 1;
    }

    private static void ListDisplays()
    {
        var displays = NativeMethods.GetAttachedDisplays();
        if (displays.Count == 0)
        {
            Console.WriteLine("No attached display devices were found.");
            return;
        }

        foreach (var d in displays)
            Console.WriteLine($"{d.DeviceName}\t{d.Description}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("DimmerLUT - GPU display LUT dimming");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  DimmerLUT.exe                  Launch the GUI");
        Console.WriteLine("  DimmerLUT.exe --list           List attached displays");
        Console.WriteLine("  DimmerLUT.exe --set <1-100>    Set brightness on all displays");
        Console.WriteLine("  DimmerLUT.exe --restore        Restore saved original ramps");
        Console.WriteLine("  DimmerLUT.exe --help           Show this help");
        Console.WriteLine();
        Console.WriteLine("Notes:");
        Console.WriteLine("  --set captures the current ramp on first use and reuses it on later");
        Console.WriteLine("  runs, so --restore always returns the display to its pre-dim state.");
        Console.WriteLine("  To recapture a new 'original' (e.g. after calibration), run");
        Console.WriteLine("  --restore first, adjust the display, then --set again.");
    }

    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private static void AttachToParentConsole()
    {
        if (!AttachConsole(ATTACH_PARENT_PROCESS))
            return;

        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* ignore */ }
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }
}

internal static class RampStore
{
    private static readonly JsonSerializerOptions s_json = new() { IncludeFields = true };

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DimmerLUT", "originals.json");

    public static Dictionary<string, GammaRamp> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, GammaRamp>>(json, s_json);
            return new Dictionary<string, GammaRamp>(data ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void Save(Dictionary<string, GammaRamp> data)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data, s_json));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ! Could not save original ramps: {ex.Message}");
        }
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch { /* ignore */ }
    }
}

internal sealed class MainForm : Form
{
    private readonly ComboBox _displayBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 390 };
    private readonly NumericUpDown _percent = new()
    {
        Minimum = 1, Maximum = 100, Value = 75, Increment = 1, Width = 90
    };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(490, 0) };
    private readonly Dictionary<string, GammaRamp> _originalRamps = new(StringComparer.OrdinalIgnoreCase);
    private List<DisplayInfo> _displays = [];

    public MainForm()
    {
        Text = "Dimmer LUT";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(530, 245);

        var heading = new Label
        {
            Text = "GPU display LUT dimming",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(18, 16)
        };

        var displayLabel = new Label { Text = "Display:", AutoSize = true, Location = new Point(18, 58) };
        _displayBox.Location = new Point(105, 54);

        var percentLabel = new Label { Text = "Brightness (%):", AutoSize = true, Location = new Point(18, 98) };
        _percent.Location = new Point(135, 94);

        var applyButton = new Button { Text = "Apply to selected", Location = new Point(18, 137), Size = new Size(145, 32) };
        var applyAllButton = new Button { Text = "Apply to all", Location = new Point(173, 137), Size = new Size(110, 32) };
        var resetButton = new Button { Text = "Restore selected", Location = new Point(293, 137), Size = new Size(115, 32) };
        var resetAllButton = new Button { Text = "Restore all", Location = new Point(418, 137), Size = new Size(95, 32) };

        applyButton.Click += (_, _) => ApplySelected();
        applyAllButton.Click += (_, _) => ApplyAll();
        resetButton.Click += (_, _) => RestoreSelected();
        resetAllButton.Click += (_, _) => RestoreAll();

        _status.Location = new Point(18, 184);
        _status.Text = "75% means each RGB channel is multiplied by 0.75. Original LUTs are restored when this app closes.";

        Controls.AddRange([heading, displayLabel, _displayBox, percentLabel, _percent,
                           applyButton, applyAllButton, resetButton, resetAllButton, _status]);

        LoadDisplays();
        FormClosing += (_, _) => RestoreAll(silent: true);
    }

    private void LoadDisplays()
    {
        _displays = NativeMethods.GetAttachedDisplays();
        _displayBox.Items.Clear();

        foreach (var display in _displays)
            _displayBox.Items.Add(display);

        if (_displayBox.Items.Count > 0)
            _displayBox.SelectedIndex = 0;
        else
            SetStatus("No attached display devices were found.");
    }

    private DisplayInfo? SelectedDisplay => _displayBox.SelectedItem as DisplayInfo;

    private void ApplySelected()
    {
        if (SelectedDisplay is not { } display)
        {
            SetStatus("Select a display first.");
            return;
        }

        Apply(display);
    }

    private void ApplyAll()
    {
        if (_displays.Count == 0)
        {
            SetStatus("No displays found.");
            return;
        }

        var successes = 0;
        var failures = new List<string>();
        foreach (var display in _displays)
        {
            if (TryApply(display, (int)_percent.Value, out var error))
                successes++;
            else
                failures.Add($"{display.DeviceName}: {error}");
        }

        SetStatus(failures.Count == 0
            ? $"Applied {(int)_percent.Value}% RGB output to {successes} display(s)."
            : $"Applied to {successes} display(s). Failed: {string.Join(" | ", failures)}");
    }

    private void Apply(DisplayInfo display)
    {
        if (TryApply(display, (int)_percent.Value, out var error))
            SetStatus($"Applied {(int)_percent.Value}% RGB output to {display.DeviceName}.");
        else
            SetStatus($"Could not apply LUT to {display.DeviceName}: {error}");
    }

    private bool TryApply(DisplayInfo display, int percent, out string error)
    {
        error = "";
        IntPtr hdc = NativeMethods.CreateDC("DISPLAY", display.DeviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            error = "Could not create a display device context.";
            return false;
        }

        try
        {
            if (!_originalRamps.ContainsKey(display.DeviceName))
            {
                if (!NativeMethods.TryGetDeviceGammaRamp(hdc, out var original))
                {
                    error = "The driver does not expose a readable gamma ramp.";
                    return false;
                }
                _originalRamps[display.DeviceName] = original;
            }

            var ramp = GammaRamp.CreateScale(percent / 100.0);
            if (!NativeMethods.SetDeviceGammaRamp(hdc, ref ramp))
            {
                error = "The graphics driver rejected the gamma ramp. This display/driver may not support software LUT control.";
                return false;
            }

            return true;
        }
        finally
        {
            NativeMethods.DeleteDC(hdc);
        }
    }

    private void RestoreSelected()
    {
        if (SelectedDisplay is not { } display)
        {
            SetStatus("Select a display first.");
            return;
        }

        Restore(display, silent: false);
    }

    private void RestoreAll(bool silent = false)
    {
        var errors = new List<string>();
        foreach (var display in _displays)
        {
            if (!Restore(display, silent: true, out var error) && error.Length > 0)
                errors.Add($"{display.DeviceName}: {error}");
        }

        if (!silent)
            SetStatus(errors.Count == 0 ? "Original display LUTs restored where available."
                                        : "Some LUTs could not be restored: " + string.Join(" | ", errors));
    }

    private void Restore(DisplayInfo display, bool silent)
    {
        Restore(display, silent, out var error);
        if (!silent)
            SetStatus(error.Length == 0 ? $"Restored original LUT for {display.DeviceName}."
                                        : $"Could not restore {display.DeviceName}: {error}");
    }

    private bool Restore(DisplayInfo display, bool silent, out string error)
    {
        error = "";
        if (!_originalRamps.TryGetValue(display.DeviceName, out var original))
            return true;

        IntPtr hdc = NativeMethods.CreateDC("DISPLAY", display.DeviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            error = "Could not create a display device context.";
            return false;
        }

        try
        {
            if (!NativeMethods.SetDeviceGammaRamp(hdc, ref original))
            {
                error = "The driver rejected the original gamma ramp.";
                return false;
            }

            _originalRamps.Remove(display.DeviceName);
            return true;
        }
        finally
        {
            NativeMethods.DeleteDC(hdc);
        }
    }

    private void SetStatus(string message) => _status.Text = message;
}

internal sealed class DisplayInfo
{
    public string DeviceName { get; }
    public string Description { get; }

    public DisplayInfo(string deviceName, string description)
    {
        DeviceName = deviceName;
        Description = description;
    }

    public override string ToString() => $"{DeviceName} — {Description}";
}

[StructLayout(LayoutKind.Sequential)]
internal struct GammaRamp
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public ushort[] Red;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public ushort[] Green;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public ushort[] Blue;

    public static GammaRamp CreateScale(double scale)
    {
        scale = Math.Clamp(scale, 0.01, 1.0);
        var r = new ushort[256];
        var g = new ushort[256];
        var b = new ushort[256];

        for (var i = 0; i < 256; i++)
        {
            // Map input code i to 75% (or selected percentage) of full-scale output.
            var value = (ushort)Math.Clamp(
                Math.Round(i * scale * 65535.0 / 255.0),
                0, 65535);

            r[i] = value;
            g[i] = value;
            b[i] = value;
        }

        return new GammaRamp { Red = r, Green = g, Blue = b };
    }
}

internal static class NativeMethods
{
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumDisplayDevices(
        string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateDC(
        string lpszDriver, string lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDeviceGammaRamp(IntPtr hdc, ref GammaRamp lpRamp);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetDeviceGammaRamp(IntPtr hdc, ref GammaRamp lpRamp);

    internal static bool TryGetDeviceGammaRamp(IntPtr hdc, out GammaRamp ramp)
    {
        ramp = new GammaRamp
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };
        return GetDeviceGammaRamp(hdc, ref ramp);
    }

    internal static List<DisplayInfo> GetAttachedDisplays()
    {
        var result = new List<DisplayInfo>();

        for (uint i = 0; i < 32; i++)
        {
            var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref device, 0))
                break;

            if ((device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                continue;

            var description = device.DeviceString ?? "Display";
            var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(device.DeviceName, 0, ref monitor, 0) &&
                !string.IsNullOrWhiteSpace(monitor.DeviceString))
                description = monitor.DeviceString;

            result.Add(new DisplayInfo(device.DeviceName, description));
        }

        return result;
    }
}
