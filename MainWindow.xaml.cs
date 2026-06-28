using System.IO;
using System.IO.Ports;
using System.Threading;
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
    private DateTime _lastWriteTime = DateTime.MinValue;
    private bool _monoState;
    private bool _autoplayState = true;
    private static readonly int[] BaudTable = { 9600, 19200, 38400, 57600, 115200, 230400, 460800 };

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
    const ushort REG_SIGGEN_CMD  = 0x0012;
    const ushort REG_SIGGEN_TYPE = 0x0013;
    const ushort REG_SIGGEN_FREQ = 0x0014;
    const ushort REG_RTC_YEAR    = 0x0015;
    const ushort REG_RTC_SEC     = 0x001A;
    const ushort REG_UPTIME      = 0x0020;
    const ushort REG_TEMP_X10    = 0x0021;
    const ushort REG_FW_MAJOR    = 0x0022;
    const ushort REG_FW_MINOR    = 0x0023;
    const ushort REG_HEAP_FREE   = 0x0025;
    const ushort REG_SAMPLE_RATE = 0x0026;
    const ushort REG_BAUDRATE    = 0x0027;
    const ushort REG_UPTIME_HI  = 0x0028;
    const ushort REG_SNAPSHOT    = 0x0040;
    const ushort REG_TRACK_NAME  = 0x0100;

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
            string rts = ChkRts485.IsChecked == true ? "1" : "0";
            File.WriteAllText(SettingsPath, $"{port},{TxtSlaveId.Text},{rts},{CmbBaud.SelectedIndex},{CmbPollRate.SelectedIndex}");
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
            if (parts.Length >= 3) ChkRts485.IsChecked = parts[2] == "1";
            if (parts.Length >= 4 && int.TryParse(parts[3], out int bi) && bi >= 0 && bi < BaudTable.Length)
                CmbBaud.SelectedIndex = bi;
            if (parts.Length >= 5 && int.TryParse(parts[4], out int pi) && pi >= 0 && pi < PollRates.Length)
                CmbPollRate.SelectedIndex = pi;
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
            int baudIdx = CmbBaud.SelectedIndex;
            if (baudIdx < 0 || baudIdx >= BaudTable.Length) baudIdx = 4;
            int baud = BaudTable[baudIdx];
            _port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 1000,
                WriteTimeout = 1000
            };
            if (ChkRts485.IsChecked == true)
            {
                _port.Handshake = Handshake.None;
                _port.RtsEnable = false;
            }
            _port.Open();
            _rts485 = ChkRts485.IsChecked == true;

            _master = ModbusSerialMaster.CreateRtu(_port);
            _master.Transport.ReadTimeout = 500;
            _master.Transport.Retries = 0;

            _connected = true;
            string modeStr = _rts485 ? " [RS-485]" : "";
            TxtStatus.Text = $"Connected: {portName} (ID={_slaveId}){modeStr}";
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

    static readonly string[] StateNames = ["Stop", "Play", "Error", "Pause"];
    static readonly string[] RepeatNames = ["All", "One", "Off", "Single", "Random"];
    static readonly string[] FormatNames = ["—", "MP3", "WAV", "FLAC"];
    private int _pollCycle;

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_connected || _master == null) return;
        if ((DateTime.Now - _lastWriteTime).TotalMilliseconds < 600) return;

        try
        {
            // Status + settings: 0x0000-0x0008 (9 regs)
            var regs = await Task.Run(() => { lock (_modbusLock) { var r = ReadRegs( REG_STATE, 9); return r; } });
            if (regs.Length < 9) return;

            int state = regs[0];
            _lastState = state;
            TxtState.Text = $"State: {(state < StateNames.Length ? StateNames[state] : "?")}";
            BtnPlayPause.Content = state == 1 ? "⏸ Pause" : "▶ Play";
            TxtTrack.Text = $"Track: {regs[1] + 1}/{regs[2]}";

            _suppressVolEvent = true;
            TxtVolume.Text = $"Volume: {regs[3]}%";
            if (!SliderVol.IsMouseCaptureWithin &&
                (DateTime.Now - _lastVolWrite).TotalMilliseconds > 1000)
            {
                SliderVol.Value = regs[3];
                TxtVol.Text = $"{regs[3]}%";
            }
            _suppressVolEvent = false;

            int rep = regs[4];
            TxtRepeat.Text = $"Repeat: {(rep < RepeatNames.Length ? RepeatNames[rep] : "?")}";
            _suppressRepeatEvent = true;
            if (rep < CmbRepeat.Items.Count) CmbRepeat.SelectedIndex = rep;
            _suppressRepeatEvent = false;

            _monoState = regs[5] != 0;
            BtnMono.Content = _monoState ? "Mono: On" : "Mono: Off";
            _autoplayState = regs[6] != 0;
            BtnAutoplay.Content = _autoplayState ? "Auto: On" : "Auto: Off";
            TxtSD.Text = $"SD: {(regs[7] != 0 ? "OK" : "—")}";

            // Info
            var info = await Task.Run(() => { lock (_modbusLock) { var r = ReadRegs(REG_UPTIME, 8); return r; } });
            if (info.Length >= 8)
            {
                var upHi = await Task.Run(() => { lock (_modbusLock) { var r = ReadRegs(REG_UPTIME_HI, 1); return r; } });
                uint upSec = (uint)info[0];
                if (upHi.Length >= 1) upSec |= (uint)upHi[0] << 16;
                int d = (int)(upSec / 86400);
                int h = (int)(upSec % 86400 / 3600);
                int m = (int)(upSec % 3600 / 60);
                int s = (int)(upSec % 60);
                TxtUptime.Text = $"Up: {d}d {h:D2}:{m:D2}:{s:D2}";
                TxtTemp.Text = $"Temp: {info[1] / 10.0:F1}°C";
                TxtFwVer.Text = $"FW: {info[2]}.{info[3]}";
                TxtHeap.Text = $"Heap: {info[5] * 16 / 1024}KB";
                uint sampleRate = (uint)info[6] * 100;
                TxtFormat.Text = sampleRate > 0 ? $"{sampleRate / 1000}kHz" : "—";
            }

            // RTC
            try
            {
                var rtc = await Task.Run(() => { lock (_modbusLock) { var r = ReadRegs(REG_RTC_YEAR, 6); return r; } });
                if (rtc.Length >= 6)
                {
                    string[] dayNames = ["Sun","Mon","Tue","Wed","Thu","Fri","Sat"];
                    string dow = "";
                    try { dow = dayNames[(int)new DateTime(rtc[0], rtc[1], rtc[2]).DayOfWeek] + " "; } catch { }
                    string rtcText = rtc[0] >= 2020
                        ? $"RTC: {dow}{rtc[2]:D2}/{rtc[1]:D2}/{rtc[0]} {rtc[3]:D2}:{rtc[4]:D2}:{rtc[5]:D2}"
                        : "RTC: no sync";
                    TxtRtcTime.Text = rtcText;
                    TxtRtcDisplay.Text = rtcText;
                }
            }
            catch { }

            // Track name
            var nameRegs = await Task.Run(() => { lock (_modbusLock) { var r = ReadRegs(REG_TRACK_NAME, 16); return r; } });
            if (nameRegs.Length < 1) return;
            var sb = new StringBuilder();
            foreach (var r in nameRegs)
            {
                char hi = (char)(r >> 8);
                char lo = (char)(r & 0xFF);
                if (hi != 0) sb.Append(hi);
                if (lo != 0) sb.Append(lo);
            }
            string trackName = sb.ToString().TrimEnd('\0');
            TxtTrackName.Text = $"♪ {trackName}";
        }
        catch (Exception ex)
        {
            Log($"Poll error: {ex.Message}");
        }
    }

    private readonly object _modbusLock = new();
    private bool _rts485;

    private void Rts485Pre()  { if (_rts485 && _port != null) _port.RtsEnable = true; }
    private void Rts485Post() { if (_rts485 && _port != null) { _port.BaseStream.Flush(); Thread.Sleep(2); _port.RtsEnable = false; } }

    private ushort[] ReadRegs(ushort start, ushort count)
    {
        Rts485Pre();
        var r = _master!.ReadHoldingRegisters(_slaveId, start, count);
        Rts485Post();
        return r;
    }

    private void WriteReg(ushort addr, ushort value)
    {
        if (!_connected) return;
        _lastWriteTime = DateTime.Now;
        Task.Run(() =>
        {
            lock (_modbusLock)
            {
                try
                { Rts485Pre(); _master!.WriteSingleRegister(_slaveId, addr, value); Rts485Post(); }
                catch { Rts485Post(); }
            }
        });
    }

    private void WriteRegs(ushort addr, ushort[] values)
    {
        if (!_connected) return;
        _lastWriteTime = DateTime.Now;
        Task.Run(() =>
        {
            lock (_modbusLock)
            {
                try
                { Rts485Pre(); _master!.WriteMultipleRegisters(_slaveId, addr, values); Rts485Post(); }
                catch { Rts485Post(); }
            }
        });
    }

    // Control buttons
    private int _lastState;
    private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_lastState == 1) WriteReg(REG_COMMAND, 5);  // playing → pause
        else WriteReg(REG_COMMAND, 1);                   // stop/pause → play
    }
    private void BtnStop_Click(object sender, RoutedEventArgs e) => WriteReg(REG_COMMAND, 2);
    private void BtnNext_Click(object sender, RoutedEventArgs e) => WriteReg(REG_COMMAND, 3);
    private void BtnPrev_Click(object sender, RoutedEventArgs e) => WriteReg(REG_COMMAND, 4);

    private DateTime _lastVolWrite = DateTime.MinValue;
    private void SliderVol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtVol == null) return;
        TxtVol.Text = $"{(int)SliderVol.Value}%";
        if (_suppressVolEvent) return;
        if ((DateTime.Now - _lastVolWrite).TotalMilliseconds < 100) return;
        _lastVolWrite = DateTime.Now;
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

    private void BtnSetBaud_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected || _master == null || _port == null) return;
        int idx = CmbBaud.SelectedIndex;
        if (idx < 0 || idx >= BaudTable.Length) return;

        WriteReg(REG_BAUDRATE, (ushort)idx);

        Task.Run(() =>
        {
            Thread.Sleep(200);
            lock (_modbusLock)
            {
                try
                {
                    _port!.BaudRate = BaudTable[idx];
                    Dispatcher.BeginInvoke(() =>
                    {
                        Log($"Baud changed to {BaudTable[idx]} (both sides)");
                        SaveSettings();
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(() => Log($"Baud switch failed: {ex.Message}"));
                }
            }
        });
    }

    // Signal Generator — set type+freq first, then start
    private void BtnSigStart_Click(object sender, RoutedEventArgs e)
    {
        ushort type = (ushort)(CmbSigType.SelectedIndex + 1);
        if (!ushort.TryParse(TxtSigFreq.Text, out ushort freq)) freq = 1000;
        WriteReg(REG_SIGGEN_TYPE, type);
        WriteReg(REG_SIGGEN_FREQ, freq);
        WriteReg(REG_SIGGEN_CMD, 1);
    }

    private void BtnSigStop_Click(object sender, RoutedEventArgs e)
    {
        WriteReg(REG_SIGGEN_CMD, 0);
    }

    // RTC Sync
    private void BtnRtcSync_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        WriteRegs(REG_RTC_YEAR, [
            (ushort)now.Year, (ushort)now.Month, (ushort)now.Day,
            (ushort)now.Hour, (ushort)now.Minute, (ushort)now.Second
        ]);
        Log($"RTC sync: {now:yyyy-MM-dd HH:mm:ss}");
    }

    // Raw register
    private async void BtnRegRead_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected) return;
        try
        {
            ushort addr = Convert.ToUInt16(TxtRegAddr.Text, 16);
            ushort count = ushort.Parse(TxtRegCount.Text);
            var regs = await Task.Run(() => { lock (_modbusLock) { var r = ReadRegs(addr, count); return r; } });
            var sb = new StringBuilder();
            foreach (var r in regs) sb.Append($"0x{r:X4} ");
            TxtRegResult.Text = sb.ToString();
            Log($"Read [0x{addr:X4}] ×{count}: {sb}");
        }
        catch (Exception ex) { TxtRegResult.Text = ex.Message; }
    }

    private void BtnRegWrite_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected) return;
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

    private void BtnClear_Click(object sender, RoutedEventArgs e) => TxtLog.Clear();

    static readonly int[] PollRates = [100, 250, 500, 1000, 2000];
    private void CmbPollRate_Changed(object sender, SelectionChangedEventArgs e)
    {
        int idx = CmbPollRate.SelectedIndex;
        if (idx >= 0 && idx < PollRates.Length && _pollTimer != null)
            _pollTimer.Interval = TimeSpan.FromMilliseconds(PollRates[idx]);
        SaveSettings();
    }

    // ── Register Map ────────────────────────────────────────────
    public class RegEntry { public string Addr { get; set; } = ""; public string Name { get; set; } = ""; public string Value { get; set; } = "—"; public string Dec { get; set; } = ""; public string RW { get; set; } = ""; public string Desc { get; set; } = ""; public ushort RawAddr; }

    private readonly List<RegEntry> _regMap = new()
    {
        new() { RawAddr=0x0000, Addr="0x0000", Name="STATE", RW="RO", Desc="0=stop 1=play 3=pause" },
        new() { RawAddr=0x0001, Addr="0x0001", Name="TRACK", RW="RO", Desc="track index" },
        new() { RawAddr=0x0002, Addr="0x0002", Name="TRACK_COUNT", RW="RO", Desc="total tracks" },
        new() { RawAddr=0x0003, Addr="0x0003", Name="VOLUME", RW="RW", Desc="0-100" },
        new() { RawAddr=0x0004, Addr="0x0004", Name="REPEAT", RW="RW", Desc="0=All 1=One 2=Off 3=Single 4=Random" },
        new() { RawAddr=0x0005, Addr="0x0005", Name="MONO", RW="RW", Desc="0=stereo 1=mono" },
        new() { RawAddr=0x0006, Addr="0x0006", Name="AUTOPLAY", RW="RW", Desc="0=off 1=on" },
        new() { RawAddr=0x0007, Addr="0x0007", Name="SD_OK", RW="RO", Desc="0=no SD 1=ok" },
        new() { RawAddr=0x0010, Addr="0x0010", Name="COMMAND", RW="WO", Desc="1=play 2=stop 3=next 4=prev 5=pause" },
        new() { RawAddr=0x0012, Addr="0x0012", Name="SIGGEN_CMD", RW="RW", Desc="0=stop 1=start" },
        new() { RawAddr=0x0013, Addr="0x0013", Name="SIGGEN_TYPE", RW="RW", Desc="1=Sine..6=Pink" },
        new() { RawAddr=0x0014, Addr="0x0014", Name="SIGGEN_FREQ", RW="RW", Desc="1-20000 Hz" },
        new() { RawAddr=0x0020, Addr="0x0020", Name="UPTIME", RW="RO", Desc="seconds low 16-bit" },
        new() { RawAddr=0x0021, Addr="0x0021", Name="TEMP_X10", RW="RO", Desc="temp × 10" },
        new() { RawAddr=0x0022, Addr="0x0022", Name="FW_MAJOR", RW="RO", Desc="firmware major" },
        new() { RawAddr=0x0023, Addr="0x0023", Name="FW_MINOR", RW="RO", Desc="firmware minor" },
        new() { RawAddr=0x0025, Addr="0x0025", Name="HEAP_FREE", RW="RO", Desc="heap ÷ 16" },
        new() { RawAddr=0x0026, Addr="0x0026", Name="SAMPLE_RATE", RW="RO", Desc="rate ÷ 100" },
        new() { RawAddr=0x0027, Addr="0x0027", Name="BAUDRATE", RW="RW", Desc="baud index 0-6" },
        new() { RawAddr=0x0028, Addr="0x0028", Name="UPTIME_HI", RW="RO", Desc="seconds high 16-bit" },
    };
    private bool _regMapInit;

    private async void BtnRegMapRead_Click(object sender, RoutedEventArgs e)
    {
        if (!_regMapInit) { DgRegMap.ItemsSource = _regMap; _regMapInit = true; }
        if (!_connected || _master == null) return;
        try
        {
            var b0 = await Task.Run(() => { lock (_modbusLock) { return ReadRegs(0x0000, 9); } });
            var b1 = await Task.Run(() => { lock (_modbusLock) { return ReadRegs(0x0012, 3); } });
            var b2 = await Task.Run(() => { lock (_modbusLock) { return ReadRegs(0x0020, 9); } });
            foreach (var reg in _regMap)
            {
                ushort? val = null;
                if (reg.RawAddr <= 0x0008 && b0 != null && reg.RawAddr < b0.Length) val = b0[reg.RawAddr];
                else if (reg.RawAddr >= 0x0012 && reg.RawAddr <= 0x0014 && b1 != null) val = b1[reg.RawAddr - 0x0012];
                else if (reg.RawAddr >= 0x0020 && reg.RawAddr <= 0x0028 && b2 != null && reg.RawAddr - 0x0020 < b2.Length) val = b2[reg.RawAddr - 0x0020];
                if (val.HasValue) { reg.Value = $"0x{val:X4}"; reg.Dec = $"{val}"; }
            }
            DgRegMap.Items.Refresh();
            TxtRegMapStatus.Text = $"OK ({DateTime.Now:HH:mm:ss})";
        }
        catch (Exception ex) { TxtRegMapStatus.Text = $"Error: {ex.Message}"; }
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveWindowState();
        SaveSettings();
        Disconnect();
        base.OnClosed(e);
    }
}
