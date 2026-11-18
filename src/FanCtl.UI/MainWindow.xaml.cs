using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FanCtl.Shared;

namespace FanCtl.UI;

public partial class MainWindow : Window
{
    const string TaskName = "Fan Controller";

    readonly AppConfig _cfg = ConfigStore.Load();
    readonly ObservableCollection<TempVm> _temps = new();
    readonly ObservableCollection<FanVm> _fans = new();
    readonly Dictionary<string, TempVm> _tempMap = new();
    readonly Dictionary<string, FanVm> _fanMap = new();
    readonly DispatcherTimer _poll, _save;
    SnapshotReader? _reader;
    EventWaitHandle? _reload, _focus;
    RegisteredWaitHandle? _focusWait;
    FanVm? _sel;
    bool _loading;

    public MainWindow()
    {
        InitializeComponent();
        TempList.ItemsSource = _temps;
        SensorBox.ItemsSource = _temps;
        FanList.ItemsSource = _fans;

        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Poll(), Dispatcher);
        _save = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _save.Tick += (_, _) => { _save.Stop(); SaveNow(); };
        Editor.Changed += ScheduleSave;

        Loaded += OnLoaded;
        Closing += (_, _) => { if (_save.IsEnabled) { _save.Stop(); SaveNow(); } };
        Closed += (_, _) =>
        {
            _poll.Stop();
            _focusWait?.Unregister(null);
            _reader?.Dispose(); _reload?.Dispose(); _focus?.Dispose();
        };
    }

    // ---------- window chrome: dark title bar + Mica ----------

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins m);
    [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyChrome();
        Theme.Changed += () => { ApplyChrome(); Editor.InvalidateVisual(); };
    }

    void ApplyChrome()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        Background = new SolidColorBrush(Theme.Surface);
        int on = Theme.Light ? 0 : 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, 4); // DWMWA_USE_IMMERSIVE_DARK_MODE
        int mica = 2;                                // DWMSBT_MAINWINDOW
        if (Environment.OSVersion.Version.Build >= 22621 && DwmSetWindowAttribute(hwnd, 38, ref mica, 4) == 0)
        {
            Background = Brushes.Transparent;
            HwndSource.FromHwnd(hwnd)!.CompositionTarget.BackgroundColor = Colors.Transparent;
            var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
        }
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!TryConnectService())
            ShowBanner("Servizio Fan Controller non raggiungibile: avvia FanCtl.exe (richiede amministratore).");

        _focus = new EventWaitHandle(false, EventResetMode.AutoReset, Ipc.UiFocusEvent);
        _focusWait = ThreadPool.RegisterWaitForSingleObject(_focus, (_, _) => Dispatcher.BeginInvoke(BringToFront), null, -1, false);

        Poll();
        _poll.Start();
        if (_fans.Count > 0) FanList.SelectedIndex = 0;

        CheckConflicts();
        StartupBox.IsChecked = TaskExists();
    }

    bool TryConnectService()
    {
        try
        {
            _reader ??= new SnapshotReader();
            _reload ??= EventWaitHandle.OpenExisting(Ipc.ReloadEvent);
            return true;
        }
        catch
        {
            _reader?.Dispose(); _reader = null;
            _reload?.Dispose(); _reload = null;
            return false;
        }
    }

    void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
        Focus();
    }

    void ShowBanner(string text)
    {
        BannerText.Text = text;
        Banner.Visibility = Visibility.Visible;
    }

    void CheckConflicts()
    {
        string[] names = ["NZXT CAM", "FanControl", "GCC", "SIV", "EasyTune", "SignalRgb"];
        var found = new List<string>();
        foreach (var n in names)
        {
            var ps = Process.GetProcessesByName(n);
            if (ps.Length > 0) found.Add(n);
            foreach (var p in ps) p.Dispose();
        }
        if (found.Count > 0)
            ShowBanner($"In esecuzione: {string.Join(", ", found)}. Chiudili (anche dalla tray) per evitare che sovrascrivano le velocità delle ventole.");
    }

    // ---------- live data ----------

    void Poll()
    {
        if (_reader == null)
        {
            if (!TryConnectService()) return;
            Banner.Visibility = Visibility.Collapsed;
            CheckConflicts();
        }
        if (!_reader!.Poll()) return;

        foreach (var t in _reader.Temps)
        {
            if (!_tempMap.TryGetValue(t.Id, out var vm))
            {
                vm = new TempVm { Id = t.Id, Name = FriendlyTemp(t.Name), Hw = FriendlyHw(t.Hw) };
                _tempMap[t.Id] = vm;
                _temps.Add(vm);
            }
            vm.Value = t.Value;
        }

        bool added = false;
        foreach (var f in _reader.Fans)
        {
            if (!_fanMap.TryGetValue(f.Id, out var vm))
            {
                string hw = FriendlyHw(f.Hw);
                vm = new FanVm { Id = f.Id, DefaultName = FriendlyFan(f.Name, hw), Hw = hw };
                vm.Name = CfgFor(vm).Alias ?? vm.DefaultName;
                _fanMap[f.Id] = vm;
                _fans.Add(vm);
                added = true;
            }
            vm.Update(f.Rpm, f.Duty, f.Input, f.Min);
        }
        if (added && !_dragging) SortFansByConfig();
        if (_sel == null && _fans.Count > 0) FanList.SelectedIndex = 0;

        RefreshStatuses();
        if (_sel != null) UpdateLive();
    }

    // ---------- plain-language state ----------

    readonly HashSet<string> _everSpun = new();
    bool _showEmpty;

    static string FriendlyHw(string hw) =>
        hw.Contains("IT86", StringComparison.OrdinalIgnoreCase) || hw.Contains("IT87", StringComparison.OrdinalIgnoreCase) ||
        hw.Contains("Nuvoton", StringComparison.OrdinalIgnoreCase) ? "Scheda madre"
        : hw.Contains("Kraken", StringComparison.OrdinalIgnoreCase) ? "Kraken"
        : hw;

    static string FriendlyFan(string name, string hw) => hw switch
    {
        "Kraken" when name == "Pump" => "Pompa",
        "Kraken" when name == "Fan" => "Ventole Kraken",
        "Scheda madre" when name.StartsWith("Fan #") => "Ventola " + name[4..],
        _ => name,
    };

    static string FriendlyTemp(string name) => name switch
    {
        "CPU Package" => "CPU",
        "Core Max" => "CPU · core più caldo",
        "Core Average" => "CPU · media core",
        "Liquid" => "Liquido",
        _ when name.StartsWith("Temperature #") => "Sensore scheda " + name[12..],
        _ => name,
    };

    string StatusFor(FanVm f, FanConfig c, out bool warn)
    {
        warn = false;
        switch (c.Mode)
        {
            case FanMode.Fixed:
                float v = Math.Max(c.Fixed, f.Min);
                if (v <= 0) { warn = true; return "Fissa a 0% · ferma"; }
                return $"Fissa al {v:0}%";
            case FanMode.Curve:
                if (c.Sensor == null || !_tempMap.TryGetValue(c.Sensor, out var t) || float.IsNaN(t.Value))
                {
                    warn = true;
                    return "Sensore non trovato · al 100% per sicurezza";
                }
                float temp = float.IsNaN(f.Input) ? t.Value : f.Input;
                return float.IsNaN(f.Duty) ? $"Curva su {t.Name}" : $"Curva su {t.Name} · {temp:0}° → {f.Duty:0}%";
            default:
                return f.Hw == "Kraken" ? "Automatica · decide il Kraken" : "Automatica · decide il BIOS";
        }
    }

    void RefreshStatuses()
    {
        int empty = 0;
        foreach (var f in _fans)
        {
            var c = CfgFor(f);
            f.Status = StatusFor(f, c, out bool warn);
            f.Warn = warn;
            if (f.Rpm >= 1) _everSpun.Add(f.Id);
            // Nothing plugged in: told to spin (duty > 0) yet never reported any RPM.
            bool isEmpty = !_everSpun.Contains(f.Id) && !float.IsNaN(f.Rpm) && f.Rpm < 1
                           && (float.IsNaN(f.Duty) || f.Duty > 0) && c.Alias == null
                           || c.Hidden;
            if (isEmpty) empty++;
            f.Opacity = !float.IsNaN(f.Rpm) && f.Rpm < 1 && c.Alias == null ? 0.55 : 1;
            f.Visibility = isEmpty && !_showEmpty && f != _sel ? Visibility.Collapsed : Visibility.Visible;
        }
        EmptyToggle.Visibility = empty > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyToggle.Content = _showEmpty ? "Nascondi vuoti e segnali" : $"Mostra vuoti e segnali ({empty})";
    }

    void EmptyToggle_Click(object sender, RoutedEventArgs e)
    {
        _showEmpty = !_showEmpty;
        RefreshStatuses();
    }

    void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _fans) CfgFor(f).Mode = FanMode.Auto;
        ScheduleSave();
        if (_sel != null)
        {
            _loading = true; ModeAuto.IsChecked = true; _loading = false;
            ApplyModeVisibility(FanMode.Auto);
        }
        RefreshStatuses();
    }

    FanConfig CfgFor(FanVm vm)
    {
        var c = _cfg.Find(vm.Id);
        if (c == null) { c = new FanConfig { Id = vm.Id }; _cfg.Fans.Add(c); }
        return c;
    }

    void UpdateLive()
    {
        var f = _sel!;
        RpmBig.Text = float.IsNaN(f.Rpm) ? "—" : $"{f.Rpm:0} RPM";
        DutyBig.Text = float.IsNaN(f.Duty) ? "" : $"velocità {f.Duty:0}%";
        HwText.Text = $"{f.Hw} · {f.Status}";
        HwText.Foreground = f.Warn ? new SolidColorBrush(Color.FromRgb(0xD8, 0x3B, 0x01)) : (Brush)FindResource("Text2");

        var c = CfgFor(f);
        if (c.Mode != FanMode.Curve) return;
        float t = f.Input;
        if (float.IsNaN(t) && c.Sensor != null && _tempMap.TryGetValue(c.Sensor, out var tv)) t = tv.Value;
        Editor.SetLive(t, f.Duty);
    }

    // ---------- selection & editing ----------

    void FanList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _sel = FanList.SelectedItem as FanVm;
        Detail.Visibility = _sel == null ? Visibility.Hidden : Visibility.Visible;
        if (_sel == null) return;

        _loading = true;
        var c = CfgFor(_sel);
        NameBox.Text = _sel.Name;
        (c.Mode switch { FanMode.Fixed => ModeFixed, FanMode.Curve => ModeCurve, _ => ModeAuto }).IsChecked = true;
        SensorBox.SelectedItem = c.Sensor != null && _tempMap.TryGetValue(c.Sensor, out var tv) ? tv : null;
        FixedSlider.Minimum = _sel.Min;
        FixedSlider.Value = Math.Max(c.Fixed, _sel.Min);
        FixedText.Text = $"{FixedSlider.Value:0}%";
        MinHint.Text = _sel.Min > 0
            ? $"Questo dispositivo non scende sotto il {_sel.Min:0}%."
            : "Molte ventole PWM hanno una velocità minima: sotto il 20–30% possono non rallentare più (o fermarsi del tutto).";
        Editor.MinDuty = _sel.Min;
        Editor.Points = c.Points;
        Editor.SetLive(float.NaN, float.NaN);
        ApplyModeVisibility(c.Mode);
        _loading = false;
        UpdateLive();
    }

    void ApplyModeVisibility(FanMode m)
    {
        bool curve = m == FanMode.Curve;
        Editor.Visibility = curve ? Visibility.Visible : Visibility.Collapsed;
        CurveOptions.Visibility = curve ? Visibility.Visible : Visibility.Hidden;
        CurveFooter.Visibility = curve ? Visibility.Visible : Visibility.Hidden;
        FixedPanel.Visibility = m == FanMode.Fixed ? Visibility.Visible : Visibility.Collapsed;
        AutoPanel.Visibility = m == FanMode.Auto ? Visibility.Visible : Visibility.Collapsed;
    }

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_sel == null) return;
        var m = sender == ModeFixed ? FanMode.Fixed : sender == ModeCurve ? FanMode.Curve : FanMode.Auto;
        ApplyModeVisibility(m);
        if (_loading) return;
        var c = CfgFor(_sel);
        c.Mode = m;
        if (m == FanMode.Curve && c.Sensor == null && _temps.Count > 0)
        {
            c.Sensor = _temps[0].Id;
            _loading = true; SensorBox.SelectedItem = _temps[0]; _loading = false;
        }
        ScheduleSave();
    }

    void SensorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _sel == null || SensorBox.SelectedItem is not TempVm t) return;
        CfgFor(_sel).Sensor = t.Id;
        Editor.SetLive(t.Value, float.NaN);
        ScheduleSave();
    }

    void FixedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        float v = MathF.Round((float)e.NewValue);
        FixedText.Text = $"{v:0}%";
        if (_loading || _sel == null) return;
        CfgFor(_sel).Fixed = v;
        ScheduleSave();
    }

    void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _sel == null) return;
        string s = NameBox.Text.Trim();
        var c = CfgFor(_sel);
        c.Alias = s.Length == 0 || s == _sel.DefaultName ? null : s;
        _sel.Name = c.Alias ?? _sel.DefaultName;
        RefreshStatuses();
        ScheduleSave();
    }

    void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape) Keyboard.ClearFocus();
    }

    void Rename_Click(object sender, RoutedEventArgs e) => BeginRename();

    async void Identify_Click(object sender, RoutedEventArgs e)
    {
        if (_sel == null) return;
        try
        {
            Directory.CreateDirectory(ConfigStore.Dir);
            File.WriteAllText(Ipc.IdentifyFile, _sel.Id);
            using var ev = EventWaitHandle.OpenExisting(Ipc.IdentifyEvent);
            ev.Set();
        }
        catch { ShowBanner("Identificazione non disponibile: riavvia Fan Controller dalla tray."); return; }

        IdentifyBtn.IsEnabled = false;
        IdentifyBtn.Content = "Identifico…";
        await Task.Delay(Ipc.IdentifyMs);
        IdentifyBtn.Content = "Identifica";
        IdentifyBtn.IsEnabled = true;
    }

    void BeginRename()
    {
        if (_sel == null) return;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    // ---------- drag to reorder ----------

    FanVm? _dragItem;
    Point _dragStart;
    bool _dragging;

    void FanList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Clicks on a row slider are speed changes, never reorder drags.
        for (var n = e.OriginalSource as DependencyObject; n != null && n != FanList; n = VisualTreeHelper.GetParent(n))
            if (n is Slider) { _dragItem = null; return; }

        _dragItem = e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(FanList, d) is ListBoxItem li
            ? li.DataContext as FanVm : null;
        _dragStart = e.GetPosition(FanList);
        _dragging = false;
        if (e.ClickCount == 2 && _dragItem != null)
        {
            FanList.SelectedItem = _dragItem;
            _dragItem = null;
            Dispatcher.BeginInvoke(BeginRename, DispatcherPriority.Input);
            e.Handled = true;
        }
    }

    void FanList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(FanList);
        if (!_dragging)
        {
            if (Math.Abs(p.Y - _dragStart.Y) < 5) return;
            _dragging = true;
            FanList.SelectedItem = _dragItem;
            FanList.CaptureMouse();
            Mouse.OverrideCursor = Cursors.SizeNS;
        }
        int from = _fans.IndexOf(_dragItem), to = IndexAt(p.Y);
        if (from >= 0 && to >= 0 && to != from) _fans.Move(from, to);
        e.Handled = true;
    }

    int IndexAt(double y)
    {
        for (int i = 0; i < _fans.Count; i++)
        {
            if (FanList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            double top = c.TranslatePoint(new Point(0, 0), FanList).Y;
            if (y < top + c.ActualHeight) return i;
        }
        return _fans.Count - 1;
    }

    void FanList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();
    void FanList_LostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    void EndDrag()
    {
        _dragItem = null;
        if (!_dragging) return;
        _dragging = false;
        Mouse.OverrideCursor = null;
        if (FanList.IsMouseCaptured) FanList.ReleaseMouseCapture();
        PersistOrder();
    }

    void FanList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2) { BeginRename(); e.Handled = true; return; }
        // Alt+↑/↓ moves the selected fan
        if (_sel == null || Keyboard.Modifiers != ModifierKeys.Alt) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int i = _fans.IndexOf(_sel), j = key == Key.Up ? i - 1 : key == Key.Down ? i + 1 : i;
        if (j == i || j < 0 || j >= _fans.Count) return;
        _fans.Move(i, j);
        PersistOrder();
        e.Handled = true;
    }

    void PersistOrder()
    {
        var ordered = new List<FanConfig>(_cfg.Fans.Count);
        foreach (var f in _fans) ordered.Add(CfgFor(f));
        foreach (var c in _cfg.Fans) if (!ordered.Contains(c)) ordered.Add(c);
        _cfg.Fans.Clear();
        _cfg.Fans.AddRange(ordered);
        ScheduleSave();
    }

    void SortFansByConfig()
    {
        var sorted = new List<FanVm>(_fans);
        sorted.Sort((a, b) => _cfg.Fans.IndexOf(CfgFor(a)).CompareTo(_cfg.Fans.IndexOf(CfgFor(b))));
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = _fans.IndexOf(sorted[i]);
            if (cur != i) _fans.Move(cur, i);
        }
    }

    void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (_sel == null) return;
        var c = CfgFor(_sel);
        c.Points = ((Button)sender).Tag switch
        {
            "silent" => Curves.Silent(),
            "perf" => Curves.Performance(),
            _ => Curves.Balanced(),
        };
        Editor.Points = c.Points;
        ScheduleSave();
    }

    List<CurvePoint>? _copiedCurve;

    void CopyCurve_Click(object sender, RoutedEventArgs e)
    {
        if (_sel == null) return;
        _copiedCurve = CfgFor(_sel).Points.ConvertAll(p => new CurvePoint(p.T, p.D));
        PasteBtn.IsEnabled = true;
    }

    void PasteCurve_Click(object sender, RoutedEventArgs e)
    {
        if (_sel == null || _copiedCurve == null) return;
        var c = CfgFor(_sel);
        c.Points = _copiedCurve.ConvertAll(p => new CurvePoint(p.T, p.D));
        Editor.Points = c.Points;
        ScheduleSave();
    }

    void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_sel == null) return;
        var src = CfgFor(_sel);
        foreach (var f in _fans)
        {
            if (f == _sel || f.Hw != _sel.Hw) continue;
            var c = CfgFor(f);
            c.Mode = src.Mode;
            c.Sensor = src.Sensor;
            c.Fixed = src.Fixed;
            c.Hysteresis = src.Hysteresis;
            c.Points = src.Points.ConvertAll(p => new CurvePoint(p.T, p.D));
        }
        ScheduleSave();
    }

    void ScheduleSave()
    {
        _save.Stop();
        _save.Start();
        RefreshStatuses();
        if (_sel != null) UpdateLive();
    }

    void SaveNow()
    {
        try
        {
            foreach (var f in _cfg.Fans) f.Points.Sort((a, b) => a.T.CompareTo(b.T));
            ConfigStore.Save(_cfg);
            _reload?.Set();
        }
        catch (Exception ex) { ShowBanner("Impossibile salvare la configurazione: " + ex.Message); }
    }

    // ---------- autostart (Task Scheduler, elevated, no 72h limit) ----------

    static bool TaskExists() => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

    void StartupBox_Click(object sender, RoutedEventArgs e)
    {
        bool want = StartupBox.IsChecked == true;
        int rc;
        if (want)
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "FanCtl.exe");
            string user = WindowsIdentity.GetCurrent().Name;
            string xml = $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo><Description>Fan Controller</Description></RegistrationInfo>
                  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId></LogonTrigger></Triggers>
                  <Principals><Principal id="Author"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Priority>4</Priority>
                  </Settings>
                  <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--tray</Arguments></Exec></Actions>
                </Task>
                """;
            string tmp = Path.Combine(Path.GetTempPath(), "fanctl-task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            rc = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
            try { File.Delete(tmp); } catch { }
        }
        else rc = Schtasks($"/Delete /TN \"{TaskName}\" /F");

        if (rc != 0) ShowBanner("Impossibile aggiornare l'avvio automatico (schtasks).");
        StartupBox.IsChecked = TaskExists();
    }

    static int Schtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
