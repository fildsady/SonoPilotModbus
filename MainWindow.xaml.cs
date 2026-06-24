using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Modbus.Device;

namespace SonoPilotModbus;

public partial class MainWindow : Window
{
    private SerialPort? _port;
    private ModbusSerialMaster? _master;
    private byte _slaveId = 1;
    private DispatcherTimer? _pollTimer;
    private bool _connected;
    private bool _suppressVolEvent;
    private bool _suppressRepeatEvent;
    private bool _monoState;
    private bool _autoplayState = true;

    // Register addresses (match firmware modbus_rtu.h)
    const ushort REG_STATE       = 0x0000;
    const ushort REG_TRACK       = 0x0001;
    const ushort REG_TRACK_COUNT = 0x0002;
    const ushort REG_VOLUME      = 0x0003;
    const ushort REG_REPEAT      = 0x0004;
    const ushort REG_MONO        = 0x0005;
    const ushort REG_AUTOPLAY    = 0x0006;
    const ushort REG_SD_OK       = 0x0007;
    const ushort REG_COMMAND     = 0x0010;
    const ushort REG_GOTO_INDEX  = 0x0011;
    const ushort REG_UPTIME      = 0x0020;
    const ushort REG_TEMP_X10    = 0x0021;
    const ushort REG_HEAP_FREE   = 0x0025;
    const ushort REG_TRACK_NAME  = 0x0100;
    const ushort REG_SAMPLE_RATE = 0x0026;

    private static readonly string SettingsPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt");
    private static readonly string WindowPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "window.txt");

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowState();
        SizeChanged += (_, _) => SaveWindowState();
        LocationChanged += (_, _) => SaveWindowState();
        StateChanged += (_, _) => SaveWindowState();
        RefreshPorts();
        LoadSettings();
    }

    private void RefreshPorts()
    {
        CmbPort.Items.Clear();
        foreach (var p in SerialPort.GetPortNames())
            CmbPort.Items.Add(p);
        if (CmbPort.Items.Count > 0) CmbPort.SelectedIndex = 0;
    }

    private void SaveWindowState()
    {
        try
        {
            if (!IsLoaded) return;
            double l = Left, t = Top, w = Width, h = Height;
            if (double.IsNaN(l) || double.IsInfinity(l) || w < 100 || h < 100) return;
            if (WindowState == WindowState.Maximized)
            {
                var b = RestoreBounds;
                if (!double.IsInfinity(b.Left) && !double.IsNaN(b.Left) && b.Width >= 100)
                { l = b.Left; t = b.Top; w = b.Width; h = b.Height; }
            }
            File.WriteAllText(WindowPath,
                $"{l:F0},{t:F0},{w:F0},{h:F0},{(WindowState == WindowState.Maximized ? 1 : 0)}");
        }
        catch { }
    }

    private void RestoreWindowState()
    {
        try
        {
            if (!File.Exists(WindowPath)) { WindowStartupLocation = WindowStartupLocation.CenterScreen; return; }
            var parts = File.ReadAllText(WindowPath).Trim().Split(',');
            if (parts.Length < 5) { WindowStartupLocation = WindowStartupLocation.CenterScreen; return; }
            Left = double.Parse(parts[0]); Top = double.Parse(parts[1]);
            Width = double.Parse(parts[2]); Height = double.Parse(parts[3]);
            if (parts[4] == "1") WindowState = WindowState.Maximized;
        }
        catch { WindowStartupLocation = WindowStartupLocation.CenterScreen; }
    }

    private void SaveSettings()
    {
        try
        {
            string port = CmbPort.SelectedItem?.ToString() ?? "";
            File.WriteAllText(SettingsPath, $"{port},{TxtSlaveId.Text}");
        }
        catch { }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var parts = File.ReadAllText(SettingsPath).Trim().Split(',');
            if (parts.Length >= 1)
            {
                for (int i = 0; i < CmbPort.Items.Count; i++)
                    if (CmbPort.Items[i].ToString() == parts[0])
                    { CmbPort.SelectedIndex = i; break; }
            }
            if (parts.Length >= 2) TxtSlaveId.Text = parts[1];
        }
        catch { }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_connected)
        {
            Disconnect();
            return;
        }

        if (CmbPort.SelectedItem == null) { Log("No COM port selected"); return; }
        string portName = CmbPort.SelectedItem.ToString()!;
        if (!byte.TryParse(TxtSlaveId.Text, out _slaveId)) _slaveId = 1;

        try
        {
            _port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 1000,
                WriteTimeout = 1000
            };
            _port.Open();
            _master = ModbusSerialMaster.CreateRtu(_port);
            _master.Transport.ReadTimeout = 1000;
            _master.Transport.Retries = 1;

            _connected = true;
            TxtStatus.Text = $"Connected: {portName} (ID={_slaveId})";
            TxtStatus.Foreground = FindResource("CatGreen") as System.Windows.Media.Brush;
            BtnConnect.Content = "Disconnect";
            SaveSettings();
            Log($"Connected to {portName} slave={_slaveId}");

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _pollTimer.Tick += PollTimer_Tick;
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            Log($"Connect failed: {ex.Message}");
        }
    }

    private void Disconnect()
    {
        _pollTimer?.Stop();
        _master?.Dispose();
        _port?.Close();
        _master = null;
        _port = null;
        _connected = false;
        TxtStatus.Text = "Disconnected";
        TxtStatus.Foreground = FindResource("CatRed") as System.Windows.Media.Brush;
        BtnConnect.Content = "Connect";
        Log("Disconnected");
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_connected || _master == null) return;

        try
        {
            var regs = await Task.Run(() => { lock (_modbusLock) { return _master.ReadHoldingRegisters(_slaveId, REG_STATE, 9); } });

            string[] states = ["Stop", "Play", "Error", "Pause"];
            string[] repeats = ["All", "One", "Off", "Single", "Random"];

            int state = regs[0];
            TxtState.Text = $"State: {(state < states.Length ? states[state] : "?")}";
            TxtTrack.Text = $"Track: {regs[1] + 1}/{regs[2]}";

            _suppressVolEvent = true;
            TxtVolume.Text = $"Volume: {regs[3]}%";
            if (!SliderVol.IsMouseCaptureWithin) SliderVol.Value = regs[3];
            TxtVol.Text = $"{regs[3]}%";
            _suppressVolEvent = false;

            int rep = regs[4];
            TxtRepeat.Text = $"Repeat: {(rep < repeats.Length ? repeats[rep] : "?")}";
            _suppressRepeatEvent = true;
            if (rep < CmbRepeat.Items.Count) CmbRepeat.SelectedIndex = rep;
            _suppressRepeatEvent = false;

            _monoState = regs[5] != 0;
            BtnMono.Content = _monoState ? "Mono: On" : "Mono: Off";
            _autoplayState = regs[6] != 0;
            BtnAutoplay.Content = _autoplayState ? "Auto: On" : "Auto: Off";
            TxtSD.Text = $"SD: {(regs[7] != 0 ? "OK" : "—")}";

            var info = await Task.Run(() => { lock (_modbusLock) { return _master.ReadHoldingRegisters(_slaveId, REG_UPTIME, 2); } });
            TxtUptime.Text = $"Uptime: {info[0] / 60}m{info[0] % 60}s";
            TxtTemp.Text = $"Temp: {info[1] / 10.0:F1}°C";

            var sr = await Task.Run(() => { lock (_modbusLock) { return _master.ReadHoldingRegisters(_slaveId, REG_SAMPLE_RATE, 1); } });
            uint sampleRate = (uint)sr[0] * 100;

            var name = await Task.Run(() => { lock (_modbusLock) { return _master.ReadHoldingRegisters(_slaveId, REG_TRACK_NAME, 16); } });
            var sb = new StringBuilder();
            foreach (var r in name)
            {
                char hi = (char)(r >> 8);
                char lo = (char)(r & 0xFF);
                if (hi != 0) sb.Append(hi);
                if (lo != 0) sb.Append(lo);
            }
            string trackName = sb.ToString().TrimEnd('\0');
            TxtTrackName.Text = $"♪ {trackName}";

            string ext = "";
            int dot = trackName.LastIndexOf('.');
            if (dot >= 0) ext = trackName[(dot + 1)..].ToUpper();
            TxtFormat.Text = sampleRate > 0 ? $"{ext} {sampleRate / 1000}kHz" : "—";
        }
        catch (Exception ex)
        {
            Log($"Poll error: {ex.Message}");
        }
    }

    private readonly object _modbusLock = new();
    private void WriteReg(ushort addr, ushort value)
    {
        if (!_connected || _master == null) return;
        Task.Run(() =>
        {
            lock (_modbusLock)
            {
                try
                {
                    _master!.WriteSingleRegister(_slaveId, addr, value);
                    Dispatcher.BeginInvoke(() => Log($"Write [0x{addr:X4}] = {value}"));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(() => Log($"Write failed: {ex.Message}"));
                }
            }
        });
    }

    // Control buttons
    private void BtnPlay_Click(object sender, RoutedEventArgs e) => WriteReg(REG_COMMAND, 1);
    private void BtnStop_Click(object sender, RoutedEventArgs e) => WriteReg(REG_COMMAND, 2);
    private void BtnNext_Click(object sender, RoutedEventArgs e) => WriteReg(REG_COMMAND, 3);
    private void BtnPrev_Click(object sender, RoutedEventArgs e) => WriteReg(REG_COMMAND, 4);
    private void BtnPause_Click(object sender, RoutedEventArgs e) => WriteReg(REG_COMMAND, 5);

    private void SliderVol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtVol == null) return;
        TxtVol.Text = $"{(int)SliderVol.Value}%";
        if (_suppressVolEvent || !SliderVol.IsMouseCaptureWithin) return;
        WriteReg(REG_VOLUME, (ushort)SliderVol.Value);
    }

    private void CmbRepeat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRepeatEvent) return;
        WriteReg(REG_REPEAT, (ushort)CmbRepeat.SelectedIndex);
    }

    private void BtnMono_Click(object sender, RoutedEventArgs e)
    {
        _monoState = !_monoState;
        WriteReg(REG_MONO, (ushort)(_monoState ? 1 : 0));
        BtnMono.Content = _monoState ? "Mono: On" : "Mono: Off";
    }

    private void BtnAutoplay_Click(object sender, RoutedEventArgs e)
    {
        _autoplayState = !_autoplayState;
        WriteReg(REG_AUTOPLAY, (ushort)(_autoplayState ? 1 : 0));
        BtnAutoplay.Content = _autoplayState ? "Auto: On" : "Auto: Off";
    }

    private void BtnGoto_Click(object sender, RoutedEventArgs e)
    {
        if (ushort.TryParse(TxtGoto.Text, out ushort idx))
            WriteReg(REG_GOTO_INDEX, idx);
    }

    // Raw register
    private async void BtnRegRead_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected || _master == null) return;
        try
        {
            ushort addr = Convert.ToUInt16(TxtRegAddr.Text, 16);
            ushort count = ushort.Parse(TxtRegCount.Text);
            var regs = await Task.Run(() => { lock (_modbusLock) { return _master!.ReadHoldingRegisters(_slaveId, addr, count); } });
            var sb = new StringBuilder();
            foreach (var r in regs) sb.Append($"0x{r:X4} ");
            TxtRegResult.Text = sb.ToString();
            Log($"Read [0x{addr:X4}] ×{count}: {sb}");
        }
        catch (Exception ex) { TxtRegResult.Text = ex.Message; }
    }

    private void BtnRegWrite_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected || _master == null) return;
        try
        {
            ushort addr = Convert.ToUInt16(TxtRegAddr.Text, 16);
            ushort val = Convert.ToUInt16(TxtRegVal.Text, 16);
            WriteReg(addr, val);
            TxtRegResult.Text = $"Written 0x{val:X4} to 0x{addr:X4}";
        }
        catch (Exception ex) { TxtRegResult.Text = ex.Message; }
    }

    private void Log(string msg)
    {
        string ts = DateTime.Now.ToString("HH:mm:ss");
        TxtLog.AppendText($"[{ts}] {msg}\n");
        TxtLog.ScrollToEnd();
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveWindowState();
        SaveSettings();
        Disconnect();
        base.OnClosed(e);
    }
}
