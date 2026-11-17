using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FanCtl.Shared;
using static FanCtl.Native;

namespace FanCtl;

internal static unsafe class Program
{
    const uint WM_TRAY = WM_APP + 1;
    const int CmdOpen = 1, CmdExit = 2;

    // Threads: the main thread only runs the tray message loop (always instant to respond);
    // a dedicated worker owns the Engine, since some hardware reads block (~220 ms for Intel CPU).
    static Engine? _engine;
    static SnapshotWriter? _snap;          // worker-only
    static volatile Process? _ui;
    static nint _hwnd;
    static uint _wmTaskbarCreated;
    static NOTIFYICONDATAW _nid;
    static string _lastTip = "";           // worker-only
    static int _ticksSinceTrim;            // worker-only
    static int _shuttingDown;
    static volatile bool _stop, _forceFull, _resume;
    static readonly AutoResetEvent _wake = new(false);
    static readonly ManualResetEventSlim _snapReady = new(false);
    static Thread? _worker;
    static EventWaitHandle? _reload;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--dump") return Dump(args.Length > 1 ? args[1] : null);
        if (args.Length > 1 && args[0] == "--memprobe") return MemProbe(args[1]);
        if (args.Length > 1 && args[0] == "--sweep") return Sweep(args[1]);
        if (args.Length > 1 && args[0] == "--regdump") return RegDump(args[1]);
        if (args.Length > 3 && args[0] == "--regdiff") return RegDiff(args[1], args[2], args[3]);

        using var mutex = new Mutex(true, Ipc.Mutex, out bool first);
        if (!first)
        {
            // Already running: ask the resident instance to open the window.
            try { using var ev = EventWaitHandle.OpenExisting(Ipc.ShowEvent); ev.Set(); } catch { }
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, _) => Shutdown();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

        _reload = UserEvent(Ipc.ReloadEvent);
        using var show = UserEvent(Ipc.ShowEvent);

        _engine = new Engine();
        _engine.Tick(full: false);
        _snap = new SnapshotWriter();
        _engine.WriteSnapshot(_snap);
        // Initialization allocates a lot (driver discovery, SMBIOS parsing): give it back.
        Compact();
        EnableEcoQoS();

        CreateTrayWindow();
        _worker = new Thread(ControlLoop, 256 * 1024) { IsBackground = true, Name = "control", Priority = ThreadPriority.AboveNormal };
        _worker.Start();

        bool openUi = args.Length == 0 || args[0] != "--tray";
        if (openUi) OpenUi();

        nint* handles = stackalloc nint[1] { show.SafeWaitHandle.DangerousGetHandle() };
        MSG msg;
        while (true)
        {
            uint r = MsgWaitForMultipleObjects(1, handles, 0, INFINITE, QS_ALLINPUT);
            if (r == 0) OpenUi();
            else
            {
                while (PeekMessageW(&msg, 0, 0, 0, PM_REMOVE))
                {
                    if (msg.message == WM_QUIT) goto exit;
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
            }
        }
    exit:
        Shutdown();
        return 0;
    }

    static void Shutdown()
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0) return;
        try { if (_ui is { HasExited: false } ui) ui.Kill(); } catch { }
        fixed (NOTIFYICONDATAW* p = &_nid) Shell_NotifyIconW(NIM_DELETE, p);
        _stop = true;
        _wake.Set();
        if (_worker != null && _worker != Thread.CurrentThread) _worker.Join(3000);
        _engine?.Dispose(); // restores BIOS / firmware control on every fan we touched
        _snap?.Dispose();
    }

    static bool UiOpen => _ui is { HasExited: false };

    /// <summary>
    /// Named event that the same user can signal even from a non-elevated process
    /// (the default DACL of an elevated token only admits Administrators/SYSTEM).
    /// </summary>
    static EventWaitHandle UserEvent(string name)
    {
        var sec = new System.Security.AccessControl.EventWaitHandleSecurity();
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        sec.AddAccessRule(new System.Security.AccessControl.EventWaitHandleAccessRule(user,
            System.Security.AccessControl.EventWaitHandleRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, sec);
    }

    static void ControlLoop()
    {
        using var identify = UserEvent(Ipc.IdentifyEvent);
        var handles = new WaitHandle[] { _reload!, _wake, identify };
        while (!_stop)
        {
            int r = WaitHandle.WaitAny(handles, _engine!.IntervalMs);
            if (_stop) break;
            try
            {
                if (r == 0) _engine.Reload();
                if (r == 2)
                {
                    try { _engine.Identify(File.ReadAllText(Ipc.IdentifyFile).Trim()); } catch { }
                }
                if (_resume) { _resume = false; _engine.Resume(); }
                Step();
            }
            catch { /* a failed read must never kill the control loop */ }
        }
    }

    static void Step()
    {
        bool force = _forceFull;
        bool ui = force || UiOpen;
        if (!ui && _ui != null)
        {
            _ui.Dispose(); _ui = null;
            Compact();
        }

        _engine!.Tick(full: ui);
        if (ui) _engine.WriteSnapshot(_snap ??= new SnapshotWriter());
        if (force) { _forceFull = false; _snapReady.Set(); }

        string tip = _engine.Tooltip();
        if (tip != _lastTip)
        {
            _lastTip = tip;
            var nid = _nid;            // copy: the struct is shared with the tray thread
            SetTip(ref nid, tip);
            nid.uFlags = NIF_TIP;
            Shell_NotifyIconW(NIM_MODIFY, &nid);
        }

        if (!ui && ++_ticksSinceTrim >= 300) { _ticksSinceTrim = 0; Compact(); }
    }

    static void Compact()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        TrimWorkingSet();
    }

    static void OpenUi()
    {
        if (UiOpen)
        {
            try { using var ev = EventWaitHandle.OpenExisting(Ipc.UiFocusEvent); ev.Set(); } catch { }
            return;
        }
        string exe = Path.Combine(AppContext.BaseDirectory, "FanCtl.UI.exe");
        if (!File.Exists(exe)) return;
        // Ask the worker for a full snapshot so the window opens with data already in place.
        _snapReady.Reset();
        _forceFull = true;
        _wake.Set();
        _snapReady.Wait(1000);
        try { _ui = Process.Start(new ProcessStartInfo(exe, "--child") { UseShellExecute = false }); } catch { }
    }

    static void CreateTrayWindow()
    {
        nint inst = GetModuleHandleW(0);
        fixed (char* cls = "FanCtlTray")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = inst,
                lpszClassName = cls,
            };
            RegisterClassExW(&wc);
        }
        _hwnd = CreateWindowExW(0, "FanCtlTray", "Fan Controller", 0, 0, 0, 0, 0, 0, 0, inst, 0);
        _wmTaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        nint icon = LoadImageW(inst, 32512, IMAGE_ICON, 0, 0, LR_DEFAULTSIZE | LR_SHARED);
        if (icon == 0) icon = LoadIconW(0, 32512);
        _nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = icon,
        };
        SetTip(ref _nid, "Fan Controller");
        AddIcon();
    }

    static void AddIcon()
    {
        _nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        fixed (NOTIFYICONDATAW* p = &_nid) Shell_NotifyIconW(NIM_ADD, p);
    }

    static void ShowMenu()
    {
        nint m = CreatePopupMenu();
        AppendMenuW(m, MF_STRING, CmdOpen, "Apri Fan Controller");
        AppendMenuW(m, MF_SEPARATOR, 0, null);
        AppendMenuW(m, MF_STRING, CmdExit, "Esci (ripristina BIOS)");
        POINT pt;
        GetCursorPos(&pt);
        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenu(m, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, pt.X, pt.Y, 0, _hwnd, 0);
        DestroyMenu(m);
        if (cmd == CmdOpen) OpenUi();
        else if (cmd == CmdExit) PostQuitMessage(0);
    }

    [UnmanagedCallersOnly]
    static nint WndProc(nint h, uint m, nint w, nint l)
    {
        try
        {
            switch (m)
            {
                case WM_TRAY:
                    uint ev = (uint)(l & 0xFFFF);
                    if (ev == WM_LBUTTONUP) OpenUi();
                    else if (ev == WM_RBUTTONUP) ShowMenu();
                    return 0;
                case WM_POWERBROADCAST:
                    if ((uint)w == PBT_APMRESUMEAUTOMATIC) { _resume = true; _wake.Set(); }
                    break;
                case WM_ENDSESSION:
                    if (w != 0) Shutdown();
                    return 0;
            }
            if (m == _wmTaskbarCreated && m != 0) { AddIcon(); return 0; }
        }
        catch { }
        return DefWindowProcW(h, m, w, l);
    }

    /// <summary>Measures how much private memory each LibreHardwareMonitor group adds.</summary>
    static int MemProbe(string path)
    {
        using var o = new StreamWriter(path);
        static (double ws, double priv) Mem()
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            using var p = Process.GetCurrentProcess();
            return (p.WorkingSet64 / 1048576.0, p.PrivateMemorySize64 / 1048576.0);
        }
        var m0 = Mem();
        o.WriteLine($"baseline                ws {m0.ws,6:0.0}  priv {m0.priv,6:0.0}");
        var groups = new (string name, Action<LibreHardwareMonitor.Hardware.Computer> on)[]
        {
            ("Motherboard", c => c.IsMotherboardEnabled = true),
            ("Cpu", c => c.IsCpuEnabled = true),
            ("Controller", c => c.IsControllerEnabled = true),
            ("Gpu", c => c.IsGpuEnabled = true),
        };
        var keep = new List<LibreHardwareMonitor.Hardware.Computer>();
        foreach (var (name, on) in groups)
        {
            if (name == "Gpu") continue; // measured below via WDDM instead
            var c = new LibreHardwareMonitor.Hardware.Computer();
            on(c);
            var sw = Stopwatch.StartNew();
            c.Open();
            long open = sw.ElapsedMilliseconds;
            void Upd() { foreach (var h in c.Hardware) { h.Update(); foreach (var s in h.SubHardware) s.Update(); } }
            sw.Restart(); Upd(); double first = sw.Elapsed.TotalMilliseconds;
            double min = double.MaxValue, sum = 0;
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(100);
                using var p0 = Process.GetCurrentProcess();
                var cpu0 = p0.TotalProcessorTime;
                sw.Restart(); Upd(); double ms = sw.Elapsed.TotalMilliseconds;
                min = Math.Min(min, ms); sum += ms;
                if (i == 9) { p0.Refresh(); o.WriteLine($"      last update: wall {ms:0.0} ms, cpu {(p0.TotalProcessorTime - cpu0).TotalMilliseconds:0.0} ms"); }
            }
            keep.Add(c);
            var m = Mem();
            o.WriteLine($"+ {name,-20}  ws {m.ws,6:0.0}  priv {m.priv,6:0.0}  open {open} ms  first {first:0.0} ms  steady avg {sum / 10:0.00} / min {min:0.00} ms");
            foreach (var h in c.Hardware) o.WriteLine($"      {h.HardwareType}: {h.Name}");
            o.Flush();
        }

        var swg = Stopwatch.StartNew();
        var gpus = GpuTemp.Enumerate();
        long gopen = swg.ElapsedMilliseconds;
        swg.Restart();
        float? gv = null;
        for (int i = 0; i < 10; i++) foreach (var g in gpus) gv = g.Read();
        var mg = Mem();
        o.WriteLine($"+ GPU (WDDM)            ws {mg.ws,6:0.0}  priv {mg.priv,6:0.0}  open {gopen} ms  read {swg.Elapsed.TotalMilliseconds / 10:0.000} ms");
        foreach (var g in gpus) o.WriteLine($"      {g.Name}: {g.Read():0.0} °C");
        foreach (var g in gpus) g.Dispose();
        foreach (var c in keep) c.Close();
        keep.Clear();
        o.Flush();

        // Realistic run: the actual engine, 30 ticks with UI open (full + snapshot), 30 ticks with UI closed.
        using var e = new Engine(readOnly: true);
        using var snap = new SnapshotWriter();
        Compact();
        var m1 = Mem();
        o.WriteLine($"engine ready (compacted) ws {m1.ws,6:0.0}  priv {m1.priv,6:0.0}");
        static (double ws, double priv) Raw() { using var p = Process.GetCurrentProcess(); return (p.WorkingSet64 / 1048576.0, p.PrivateMemorySize64 / 1048576.0); }
        for (int phase = 0; phase < 2; phase++)
        {
            bool full = phase == 0;
            using var p0 = Process.GetCurrentProcess();
            var cpu0 = p0.TotalProcessorTime;
            for (int i = 1; i <= 30; i++)
            {
                e.Tick(full);
                if (full) e.WriteSnapshot(snap);
                if (i % 10 == 0) { var r = Raw(); o.WriteLine($"  {(full ? "ui open " : "ui closed")} tick {i,2}  ws {r.ws,6:0.0}  priv {r.priv,6:0.0}  gen0 GCs {GC.CollectionCount(0)}"); o.Flush(); }
                Thread.Sleep(1000);
            }
            p0.Refresh();
            o.WriteLine($"  cpu per tick: {(p0.TotalProcessorTime - cpu0).TotalMilliseconds / 30:0.0} ms");
            if (full) { Compact(); var r = Raw(); o.WriteLine($"  after compact ws {r.ws,6:0.0}  priv {r.priv,6:0.0}"); }
        }
        o.WriteLine($"managed heap {GC.GetTotalMemory(false) / 1024} KB, committed {GC.GetGCMemoryInfo().TotalCommittedBytes / 1024} KB");
        return 0;
    }

    /// <summary>
    /// Measured proof of control: drive every motherboard header to 100 / 30 / 0 %,
    /// read back RPM and the duty register after 8 s (detects BIOS/EC overriding us), then restore.
    /// </summary>
    static int Sweep(string path)
    {
        using var o = new StreamWriter(path) { AutoFlush = true };
        var pc = new LibreHardwareMonitor.Hardware.Computer { IsMotherboardEnabled = true };
        pc.Open();
        var hws = new List<LibreHardwareMonitor.Hardware.IHardware>();
        foreach (var h in pc.Hardware) { hws.Add(h); hws.AddRange(h.SubHardware); }
        void Upd() { foreach (var h in hws) h.Update(); }
        Upd();

        var pairs = new List<(LibreHardwareMonitor.Hardware.ISensor ctl, LibreHardwareMonitor.Hardware.ISensor? rpm)>();
        foreach (var h in hws)
            foreach (var s in h.Sensors)
                if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Control && s.Control != null)
                {
                    LibreHardwareMonitor.Hardware.ISensor? r = null;
                    foreach (var x in h.Sensors)
                        if (x.SensorType == LibreHardwareMonitor.Hardware.SensorType.Fan && x.Index == s.Index) r = x;
                    pairs.Add((s, r));
                }

        var fix = Ite8689Fix.TryCreate(pc, out string fixInfo);
        o.WriteLine($"Scheda: {string.Join(" / ", hws.ConvertAll(h => h.Name))}");
        o.WriteLine($"Metodo: {(fix != null ? "correzione it87 (vettori Smart Fan disattivati) — " : "LibreHardwareMonitor — ")}{fixInfo}");
        o.WriteLine("Header        | inizio       | 100%          | 30%           | 0%            | ripristino");
        o.WriteLine("              | rpm   duty   | rpm   letto   | rpm   letto   | rpm   letto   | rpm   duty");
        string Cell(LibreHardwareMonitor.Hardware.ISensor c, LibreHardwareMonitor.Hardware.ISensor? r) =>
            $"{r?.Value ?? float.NaN,5:0} {c.Value ?? float.NaN,5:0}%";

        var regs = new List<string>();
        foreach (var (ctl, rpm) in pairs)
        {
            Upd();
            bool viaFix = fix != null && ctl.Hardware.Name == fix.HardwareName;
            int nr = ctl.Index;
            if (rpm?.Value is not > 0)
            {
                o.WriteLine($"{ctl.Name,-13} | {Cell(ctl, rpm),-12} | (niente collegato, saltato)");
                continue;
            }
            string line = $"{ctl.Name,-13} | {Cell(ctl, rpm),-12} |";
            if (viaFix) regs.Add($"{ctl.Name} prima:   {fix!.Describe(nr)}");
            try
            {
                foreach (float duty in new[] { 100f, 30f, 0f })
                {
                    if (viaFix) fix!.Set(nr, duty); else ctl.Control!.SetSoftware(duty);
                    Thread.Sleep(8000);
                    Upd();
                    line += $" {Cell(ctl, rpm),-13} |";
                    if (viaFix) regs.Add($"{ctl.Name} {duty,3}%:   {fix!.Describe(nr)}");
                }
            }
            finally
            {
                if (viaFix) fix!.Restore(nr); else ctl.Control!.SetDefault();
            }
            Thread.Sleep(6000);
            Upd();
            line += $" {Cell(ctl, rpm)}";
            if (viaFix) regs.Add($"{ctl.Name} ripristino: {fix!.Describe(nr)}");
            o.WriteLine(line);
        }
        o.WriteLine();
        o.WriteLine("'letto' = valore del registro PWM riletto dal chip dopo 8 s.");
        o.WriteLine();
        o.WriteLine("Registri (hex):");
        foreach (var r in regs) o.WriteLine("  " + r);
        fix?.RestoreAll();
        pc.Close();
        return 0;
    }

    /// <summary>Read-only: 3 samples of IT8689E banks 0..3, 800 ms apart (to spot self-changing registers).</summary>
    static int RegDump(string path)
    {
        var pc = new LibreHardwareMonitor.Hardware.Computer { IsMotherboardEnabled = true };
        pc.Open();
        var fix = Ite8689Fix.TryCreate(pc, out string info);
        if (fix == null) { File.WriteAllText(path + ".err", info); pc.Close(); return 1; }
        using (var fs = File.Create(path))
            for (int s = 0; s < 3; s++)
            {
                fs.Write(fix.DumpBanks());
                Thread.Sleep(800);
            }
        pc.Close();
        return 0;
    }

    /// <summary>Registers that differ between two dumps but are stable within each dump.</summary>
    static int RegDiff(string a, string b, string outPath)
    {
        byte[] da = File.ReadAllBytes(a), db = File.ReadAllBytes(b);
        const int N = 1024;
        using var o = new StreamWriter(outPath);
        int noisy = 0, changed = 0;
        for (int r = 0; r < N; r++)
        {
            bool stableA = da[r] == da[N + r] && da[r] == da[2 * N + r];
            bool stableB = db[r] == db[N + r] && db[r] == db[2 * N + r];
            if (!stableA || !stableB) { noisy++; continue; }
            if (da[r] == db[r]) continue;
            changed++;
            o.WriteLine($"bank {r >> 8} reg 0x{r & 0xff:X2} (0x{r:X3}):  {da[r]:X2} -> {db[r]:X2}   bits {Convert.ToString(da[r], 2).PadLeft(8, '0')} -> {Convert.ToString(db[r], 2).PadLeft(8, '0')}");
        }
        o.WriteLine($"-- {changed} registri cambiati, {noisy} registri variabili da soli (esclusi)");
        return 0;
    }

    static int Dump(string? path)
    {
        using var e = new Engine();
        e.Tick(full: true);
        using var o = path != null ? new StreamWriter(path) : new StreamWriter(Console.OpenStandardOutput());
        e.Dump(o);
        return 0;
    }
}
