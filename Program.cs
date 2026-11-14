using System.Runtime.InteropServices;

namespace DimmerLUT;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
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
