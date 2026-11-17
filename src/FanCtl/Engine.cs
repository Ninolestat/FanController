using System.Text;
using FanCtl.Shared;
using LibreHardwareMonitor.Hardware;

namespace FanCtl;

/// <summary>
/// Owns LibreHardwareMonitor and runs the control loop. Single-threaded: every call comes from the message loop.
/// </summary>
internal sealed class Engine : IDisposable
{
    /// <summary>A temperature input: either an LHM sensor or a WDDM GPU reading.</summary>
    sealed class Temp
    {
        public required string Id, Name, Hw;
        public ISensor? Sensor;
        public GpuTemp? Gpu;
        public float Value = float.NaN;

        public void Refresh() => Value = (Sensor != null ? Sensor.Value : Gpu!.Read()) ?? float.NaN;
    }

    sealed class Fan
    {
        public required ISensor Sensor;     // Control sensor
        public required IControl Control;
        public ISensor? Rpm;
        public required string Id;
        public required string Name;
        public FanConfig? Cfg;
        public Temp? Source;
        public float Applied = float.NaN;   // last duty written by us
        public float Input = float.NaN;     // temperature after hysteresis
        public bool Touched;                // we changed it => restore on exit
        public long IdentStart;             // != 0 while "Identify" pulses the fan
    }

    /// <summary>Pulse a fan 100% / 25% every 2 s so the user can see or hear which one it is.</summary>
    public void Identify(string id)
    {
        foreach (var f in _fans) if (f.Id == id) f.IdentStart = Environment.TickCount64;
    }

    // Max duty change per second; ramp up fast (safety), down slowly (no audible pumping).
    const float RampUpPerSec = 20f, RampDownPerSec = 5f;
    const float CriticalCpu = 95f;

    readonly Computer _pc;
    readonly List<Fan> _fans = new();
    readonly List<Temp> _temps = new();
    readonly Dictionary<string, Temp> _tempById = new();
    readonly List<IHardware> _allHw = new();
    readonly List<GpuTemp> _gpus;
    readonly HashSet<IHardware> _neededHw = new();
    readonly HashSet<Temp> _neededTemps = new();
    Temp? _cpuPackage;
    AppConfig _cfg;
    long _lastTick;

    public int IntervalMs => _cfg.IntervalMs;

    readonly bool _readOnly;
    readonly Ite8689Fix? _fix;

    public Engine(bool readOnly = false)
    {
        _readOnly = readOnly;
        _pc = new Computer
        {
            IsMotherboardEnabled = true,   // ITE IT8689E Super I/O (fans + board temps)
            IsCpuEnabled = true,           // CPU package temp
            IsControllerEnabled = true,    // NZXT Kraken (pump, fan, liquid temp)
            // GPU deliberately off: NVAPI/NVML cost ~23 MB. GPU temps come from WDDM (GpuTemp).
        };
        _pc.Open();
        if (!readOnly) _fix = Ite8689Fix.TryCreate(_pc, out _);
        foreach (var h in _pc.Hardware) Collect(h);
        foreach (var h in _allHw) h.Update();
        _gpus = GpuTemp.Enumerate();
        Discover();
        foreach (var t in _temps) t.Refresh();
        _cfg = ConfigStore.Load();
        EnsureConfigEntries();
        Bind();
    }

    void Collect(IHardware h)
    {
        _allHw.Add(h);
        foreach (var s in h.SubHardware) Collect(s);
    }

    void AddTemp(Temp t)
    {
        _temps.Add(t);
        _tempById[t.Id] = t;
    }

    void Discover()
    {
        foreach (var h in _allHw)
        {
            foreach (var s in h.Sensors)
            {
                if (s.SensorType == SensorType.Temperature)
                {
                    if (!IsUsefulTemp(s)) continue;
                    var t = new Temp { Id = s.Identifier.ToString(), Name = s.Name, Hw = h.Name, Sensor = s };
                    AddTemp(t);
                    if (h.HardwareType == HardwareType.Cpu && s.Name == "CPU Package") _cpuPackage = t;
                }
                else if (s.SensorType == SensorType.Control && s.Control != null)
                {
                    ISensor? rpm = null;
                    foreach (var r in h.Sensors)
                        if (r.SensorType == SensorType.Fan && r.Index == s.Index) { rpm = r; break; }
                    _fans.Add(new Fan
                    {
                        Sensor = s, Control = s.Control, Rpm = rpm,
                        Id = s.Identifier.ToString(), Name = BoardFanName(s) ?? s.Name.Replace(" Control", "")
                    });
                }
            }
        }
        foreach (var g in _gpus) AddTemp(new Temp { Id = g.Id, Name = "GPU", Hw = g.Name, Gpu = g });
    }

    /// <summary>
    /// Header labels as printed by Gigabyte Control Center, by Super I/O channel index.
    /// Z790 AORUS ELITE X WIFI7: verified by RPM match for CPU / System 1 / CPU OPT;
    /// System 2/3/4 follow GCC's order (nothing connected there to confirm).
    /// </summary>
    static readonly (string board, string[] names)[] BoardFanNames =
    [
        ("Z790 AORUS ELITE X WIFI7", ["CPU", "System 1", "System 2", "System 3", "CPU OPT", "System 4 Pump"]),
    ];

    string? BoardFanName(ISensor control)
    {
        if (control.Hardware.HardwareType != HardwareType.SuperIO) return null;
        string board = "";
        foreach (var h in _allHw) if (h.HardwareType == HardwareType.Motherboard) board = h.Name;
        foreach (var (b, names) in BoardFanNames)
            if (board.Contains(b, StringComparison.OrdinalIgnoreCase) && control.Index < names.Length)
                return names[control.Index];
        return null;
    }

    static bool IsUsefulTemp(ISensor s)
    {
        var n = s.Name;
        if (n.Contains("Distance to TjMax", StringComparison.Ordinal)) return false;
        if (s.Hardware.HardwareType == HardwareType.Cpu)
            return n is "CPU Package" or "Core Max" or "Core Average";
        return true;
    }

    const int ConfigVersion = 2;

    void EnsureConfigEntries()
    {
        bool changed = false;
        bool migrate = _cfg.Version < ConfigVersion;
        foreach (var f in _fans)
        {
            var c = _cfg.Find(f.Id);
            if (c == null) { c = new FanConfig { Id = f.Id }; _cfg.Fans.Add(c); Recommend(f, c); changed = true; }
            else if (migrate) { Recommend(f, c); changed = true; }
        }
        if (migrate) { _cfg.Version = ConfigVersion; changed = true; }
        if (changed) try { ConfigStore.Save(_cfg); } catch { }
    }

    /// <summary>
    /// Never decides speeds for the user: only picks a sensible default sensor/curve shape
    /// (used if the user switches to Curve) and hides CPU_OPT, which on this board only
    /// carries the Kraken pump's tach signal.
    /// </summary>
    void Recommend(Fan f, FanConfig c)
    {
        bool cooler = f.Sensor.Hardware.HardwareType == HardwareType.Cooler;
        c.Sensor ??= DefaultSourceFor(f);
        if (f.Name == "CPU OPT" && !cooler) c.Hidden = true;
        if (c.Mode == FanMode.Auto)
            c.Points = cooler ? (f.Name.StartsWith("Pump", StringComparison.Ordinal) ? Curves.Pump() : Curves.Radiator())
                              : Curves.Balanced();
    }

    string? DefaultSourceFor(Fan f)
    {
        // Kraken pump/fan -> liquid temperature; everything else -> CPU package.
        if (f.Sensor.Hardware.HardwareType == HardwareType.Cooler)
            foreach (var s in f.Sensor.Hardware.Sensors)
                if (s.SensorType == SensorType.Temperature) return s.Identifier.ToString();
        return _cpuPackage?.Id;
    }

    void Bind()
    {
        _neededHw.Clear();
        _neededTemps.Clear();
        foreach (var f in _fans)
        {
            f.Cfg = _cfg.Find(f.Id);
            f.Source = f.Cfg?.Sensor != null && _tempById.TryGetValue(f.Cfg.Sensor, out var t) ? t : null;
            f.Input = float.NaN;
            if (f.Cfg?.Mode == FanMode.Curve && f.Source != null) Need(f.Source);
        }
        if (_cpuPackage != null) Need(_cpuPackage); // critical-temperature guard
    }

    void Need(Temp t)
    {
        _neededTemps.Add(t);
        if (t.Sensor != null) _neededHw.Add(t.Sensor.Hardware);
    }

    public void Reload()
    {
        _cfg = ConfigStore.Load();
        EnsureConfigEntries();
        Bind();
    }

    public void Tick(bool full)
    {
        long now = Environment.TickCount64;
        float dt = _lastTick == 0 ? 1f : Math.Clamp((now - _lastTick) / 1000f, 0.1f, 5f);
        _lastTick = now;

        if (full)
        {
            foreach (var h in _allHw) h.Update();
            foreach (var t in _temps) t.Refresh();
        }
        else
        {
            foreach (var h in _neededHw) h.Update();
            foreach (var t in _neededTemps) t.Refresh();
        }

        if (_readOnly) return;
        bool critical = _cpuPackage != null && _cpuPackage.Value >= CriticalCpu;

        foreach (var f in _fans)
        {
            if (f.IdentStart != 0)
            {
                long el = now - f.IdentStart;
                if (el < Ipc.IdentifyMs && !critical)
                {
                    float v = Math.Clamp((el / 2000) % 2 == 0 ? 100f : 25f, f.Control.MinSoftwareValue, f.Control.MaxSoftwareValue);
                    if (v != f.Applied) try { SetFan(f, v); f.Applied = v; f.Touched = true; } catch { }
                    continue;
                }
                f.IdentStart = 0;
                f.Applied = float.NaN; // resume normal control (or hand back to BIOS below)
            }

            var cfg = f.Cfg;
            if (cfg == null || cfg.Mode == FanMode.Auto)
            {
                if (f.Touched && !critical) Restore(f);
                if (!critical) continue;
            }

            float target;
            if (critical) target = 100;
            else if (cfg!.Mode == FanMode.Fixed) target = cfg.Fixed;
            else
            {
                float tv = f.Source?.Value ?? float.NaN;
                if (float.IsNaN(tv)) target = 100; // lost sensor -> fail safe
                else
                {
                    if (float.IsNaN(f.Input) || tv >= f.Input || f.Input - tv >= cfg.Hysteresis) f.Input = tv;
                    target = Curves.Eval(cfg.Points, f.Input);
                }
            }

            float cur = float.IsNaN(f.Applied) ? (f.Sensor.Value ?? target) : f.Applied;
            // Ramping only smooths automatic curves; a speed the user sets by hand applies at once.
            float next = critical || cfg!.Mode == FanMode.Fixed
                ? target
                : Math.Clamp(target, cur - RampDownPerSec * dt, cur + RampUpPerSec * dt);
            next = Math.Clamp(MathF.Round(next, 1), f.Control.MinSoftwareValue, f.Control.MaxSoftwareValue);

            if (float.IsNaN(f.Applied) || MathF.Abs(next - f.Applied) >= 0.5f || (next != f.Applied && next == target))
            {
                try { SetFan(f, next); f.Applied = next; f.Touched = true; } catch { }
            }
        }
    }

    bool UsesFix(Fan f) => _fix != null && f.Sensor.Hardware.Name == _fix.HardwareName;

    void SetFan(Fan f, float pct)
    {
        if (UsesFix(f)) _fix!.Set(f.Sensor.Index, pct);
        else f.Control.SetSoftware(pct);
    }

    void Restore(Fan f)
    {
        try
        {
            if (UsesFix(f)) _fix!.Restore(f.Sensor.Index);
            else f.Control.SetDefault();
        }
        catch { }
        f.Touched = false;
        f.Applied = float.NaN;
        f.Input = float.NaN;
    }

    /// <summary>After sleep the firmware may have reset duty registers: force a rewrite.</summary>
    public void Resume()
    {
        foreach (var f in _fans) f.Applied = float.NaN;
        _lastTick = 0;
    }

    public void WriteSnapshot(SnapshotWriter w)
    {
        var bw = w.Begin();
        bw.Write(_temps.Count);
        foreach (var t in _temps)
        {
            bw.Write(t.Id);
            bw.Write(t.Name);
            bw.Write(t.Hw);
            bw.Write(t.Value);
        }
        bw.Write(_fans.Count);
        foreach (var f in _fans)
        {
            bw.Write(f.Id);
            bw.Write(f.Name);
            bw.Write(f.Sensor.Hardware.Name);
            bw.Write(f.Rpm?.Value ?? float.NaN);
            bw.Write(f.Sensor.Value ?? float.NaN);
            bw.Write(f.Input);
            bw.Write(Math.Clamp(f.Control.MinSoftwareValue, 0f, 100f));
        }
        w.Commit();
    }

    readonly StringBuilder _tip = new(128);

    public string Tooltip()
    {
        _tip.Clear().Append("Fan Controller");
        if (_cpuPackage != null && !float.IsNaN(_cpuPackage.Value)) _tip.Append(" · CPU ").Append(MathF.Round(_cpuPackage.Value)).Append('°');
        foreach (var f in _fans)
            if (f.Source != null && f.Source != _cpuPackage && !float.IsNaN(f.Source.Value))
            {
                _tip.Append(" · ").Append(f.Source.Name).Append(' ').Append(MathF.Round(f.Source.Value)).Append('°');
                break;
            }
        return _tip.ToString();
    }

    public void Dump(TextWriter o)
    {
        foreach (var h in _allHw)
        {
            o.WriteLine($"[{h.HardwareType}] {h.Name}  ({h.Identifier})");
            foreach (var s in h.Sensors)
                if (s.SensorType is SensorType.Temperature or SensorType.Fan or SensorType.Control)
                    o.WriteLine($"   {s.SensorType,-12} {s.Name,-28} {s.Value,8:0.0}  {s.Identifier}{(s.Control != null ? $"  [ctl {s.Control.MinSoftwareValue}-{s.Control.MaxSoftwareValue}]" : "")}");
        }
        foreach (var g in _gpus) o.WriteLine($"[WDDM] {g.Name}   GPU {g.Read(),6:0.0}  {g.Id}");
    }

    public void Dispose()
    {
        foreach (var f in _fans) if (f.Touched) Restore(f);
        _fix?.RestoreAll();
        try { _pc.Close(); } catch { }
        foreach (var g in _gpus) g.Dispose();
    }
}
