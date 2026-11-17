using System.Collections;
using System.Reflection;
using LibreHardwareMonitor.Hardware;

namespace FanCtl;

/// <summary>
/// Manual PWM control for the ITE IT8689E on Gigabyte boards, ported from the Linux it87 driver
/// (frankcrawford/it87, master 2026-11: it87_save_conventional_pwm / it87_disable_extra_vectors /
/// it87_apply_conventional_override / it87_restore_*).
///
/// Why: on these boards the chip's SmartFan engine keeps evaluating up to 9 "extra vectors"
/// (secondary curves) even in manual mode, so the BIOS curve acts as a floor or overrides the duty
/// completely. LibreHardwareMonitor only writes 0x7F to the control register and leaves them on.
///
/// Sequence per channel (nr = 0..5):
///   take:    save CTRL, DUTY, EXTVEC bytes  ->  clear EXTVEC enables  ->  DUTY = pwm  ->  CTRL = saved &amp; 0x7F
///   release: DUTY = saved  ->  CTRL = saved  ->  EXTVEC = saved   (extra vectors last, as it87 does)
///
/// Registers above 0xFF are banked: bank = reg >> 8, selected via bits 7:5 of EC register 0x06.
/// All values live in volatile chip RAM: a reboot restores the BIOS configuration.
/// </summary>
internal sealed class Ite8689Fix
{
    static readonly byte[] RegPwm = { 0x15, 0x16, 0x17, 0x7f, 0xa7, 0xaf };
    static readonly byte[] RegDuty = { 0x63, 0x6b, 0x73, 0x7b, 0xa3, 0xab };
    const byte RegBank = 0x06;
    public const int Channels = 6;

    static int ExtVecB2(int nr, int vec) => 0x212 + nr * 0x10 + vec * 4; // vectors 1..3, bit 7
    static int ExtVecB3(int nr) => 0x31c + nr * 0x10;                     // vectors 4..9, bits 0..5

    sealed class Snap
    {
        public byte Ctrl, Duty;
        public byte[] Ext = new byte[4];
    }

    readonly Func<ushort, byte> _in;
    readonly Action<ushort, byte> _out;
    readonly ushort _addrPort, _dataPort;
    readonly Func<int, bool> _waitIsa;
    readonly Action _releaseIsa;
    readonly Snap?[] _saved = new Snap?[Channels];
    public string HardwareName { get; }

    Ite8689Fix(Func<ushort, byte> @in, Action<ushort, byte> @out, ushort addr, ushort data,
               Func<int, bool> wait, Action release, string hw)
    {
        _in = @in; _out = @out; _addrPort = addr; _dataPort = data;
        _waitIsa = wait; _releaseIsa = release; HardwareName = hw;
        RecoverAfterCrash();
    }

    /// <summary>Find LHM's already-detected IT8689E and borrow its raw port accessors.</summary>
    public static Ite8689Fix? TryCreate(IComputer pc, out string info)
    {
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        info = "";
        try
        {
            foreach (var hw in pc.Hardware)
            {
                if (hw.HardwareType != HardwareType.Motherboard) continue;
                var lpc = hw.GetType().GetField("_lpcIO", F)?.GetValue(hw);
                if (lpc?.GetType().GetField("_superIOs", F)?.GetValue(lpc) is not IEnumerable list) continue;
                foreach (var sio in list)
                {
                    var t = sio.GetType();
                    if (t.Name != "IT87XX") continue;
                    string chip = t.GetProperty("Chip", F)?.GetValue(sio)?.ToString() ?? "";
                    if (!chip.Contains("8689", StringComparison.Ordinal)) { info = $"chip {chip}: fix non necessario"; continue; }

                    var port = t.GetField("_port", F)!.GetValue(sio)!;
                    var addr = (ushort)t.GetField("_addressReg", F)!.GetValue(sio)!;
                    var data = (ushort)t.GetField("_dataReg", F)!.GetValue(sio)!;
                    var pt = port.GetType();
                    var rd = pt.GetMethod("ReadIoPort", F)!.CreateDelegate<Func<ushort, byte>>(port);
                    var wr = pt.GetMethod("WriteIoPort", F)!.CreateDelegate<Action<ushort, byte>>(port);

                    var mt = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Hardware.Mutexes")!;
                    const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                    var wait = mt.GetMethod("WaitIsaBus", S)!.CreateDelegate<Func<int, bool>>();
                    var rel = mt.GetMethod("ReleaseIsaBus", S)!.CreateDelegate<Action>();

                    string name = "";
                    foreach (var sub in hw.SubHardware)
                        if (sub.Name.Contains("8689", StringComparison.Ordinal)) name = sub.Name;
                    info = $"IT8689E @ 0x{addr - 5:X}";
                    return new Ite8689Fix(rd, wr, addr, data, wait, rel, name);
                }
            }
            if (info.Length == 0) info = "IT8689E non trovato";
        }
        catch (Exception ex) { info = "accesso al chip non disponibile: " + ex.Message; }
        return null;
    }

    // ---------- raw banked access (caller holds the ISA bus mutex) ----------

    byte RawRead(byte reg) { _out(_addrPort, reg); return _in(_dataPort); }
    void RawWrite(byte reg, byte v) { _out(_addrPort, reg); _out(_dataPort, v); }

    byte SetBank(byte bank)
    {
        byte breg = RawRead(RegBank);
        byte old = (byte)(breg >> 5);
        if (old != bank) RawWrite(RegBank, (byte)((breg & 0x1f) | (bank << 5)));
        return old;
    }

    byte Read(int reg)
    {
        byte bank = (byte)(reg >> 8);
        byte old = bank != 0 ? SetBank(bank) : (byte)0;
        byte v = RawRead((byte)reg);
        if (bank != 0) SetBank(old);
        return v;
    }

    void Write(int reg, byte v)
    {
        byte bank = (byte)(reg >> 8);
        byte old = bank != 0 ? SetBank(bank) : (byte)0;
        RawWrite((byte)reg, v);
        if (bank != 0) SetBank(old);
    }

    void Locked(Action a)
    {
        if (!_waitIsa(100)) throw new TimeoutException("ISA bus occupato");
        try
        {
            // Make sure we start from bank 0 like LHM/BIOS expect, and leave it there.
            byte prev = SetBank(0);
            try { a(); }
            finally { if (prev != 0) SetBank(prev); }
        }
        finally { _releaseIsa(); }
    }

    // ---------- public API ----------

    public bool Owns(int nr) => nr >= 0 && nr < Channels && _saved[nr] != null;

    /// <summary>Take (or keep) software ownership of channel nr and drive it at pct %.</summary>
    public void Set(int nr, float pct)
    {
        if (nr < 0 || nr >= Channels) return;
        byte duty = (byte)Math.Clamp(MathF.Round(pct * 255f / 100f), 0, 255);
        Locked(() =>
        {
            var s = _saved[nr];
            if (s == null)
            {
                s = new Snap { Ctrl = Read(RegPwm[nr]), Duty = Read(RegDuty[nr]) };
                for (int i = 0; i < 3; i++) s.Ext[i] = Read(ExtVecB2(nr, i));
                s.Ext[3] = Read(ExtVecB3(nr));
                _saved[nr] = s;
                Persist();
            }
            // it87_disable_extra_vectors
            for (int i = 0; i < 3; i++)
            {
                int r = ExtVecB2(nr, i);
                Write(r, (byte)(Read(r) & 0x7f));
            }
            int r3 = ExtVecB3(nr);
            Write(r3, (byte)(Read(r3) & 0xc0));
            // it87_apply_conventional_override (FEAT_NEWER_AUTOPWM): duty, then ctrl = saved & 0x7f
            Write(RegDuty[nr], duty);
            Write(RegPwm[nr], (byte)(s.Ctrl & 0x7f));
        });
    }

    /// <summary>Hand channel nr back to the BIOS exactly as it was.</summary>
    public void Restore(int nr)
    {
        if (nr < 0 || nr >= Channels || _saved[nr] is not { } s) return;
        Locked(() => RestoreSnap(nr, s));
        _saved[nr] = null;
        Persist();
    }

    public void RestoreAll()
    {
        for (int i = 0; i < Channels; i++)
            try { Restore(i); } catch { }
    }

    void RestoreSnap(int nr, Snap s)
    {
        Write(RegDuty[nr], s.Duty);
        Write(RegPwm[nr], s.Ctrl);
        for (int i = 0; i < 3; i++) Write(ExtVecB2(nr, i), s.Ext[i]);
        Write(ExtVecB3(nr), s.Ext[3]);
    }

    /// <summary>Read-only snapshot of banks 0..3 (1024 registers).</summary>
    public byte[] DumpBanks()
    {
        var b = new byte[4 * 256];
        Locked(() => { for (int i = 0; i < b.Length; i++) b[i] = Read(i); });
        return b;
    }

    /// <summary>Raw register dump of one channel (for the diagnostic report).</summary>
    public string Describe(int nr)
    {
        string r = "";
        Locked(() =>
        {
            r = $"ctrl[{RegPwm[nr]:X2}]={Read(RegPwm[nr]):X2} duty[{RegDuty[nr]:X2}]={Read(RegDuty[nr]):X2} " +
                $"ext={Read(ExtVecB2(nr, 0)):X2},{Read(ExtVecB2(nr, 1)):X2},{Read(ExtVecB2(nr, 2)):X2},{Read(ExtVecB3(nr)):X2}";
        });
        return r;
    }

    // ---------- crash safety: snapshot on disk, valid only for the current boot ----------

    static string SnapFile => Path.Combine(Shared.ConfigStore.Dir, "it8689-snapshot.bin");
    static long BootId => (DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64)).Ticks / TimeSpan.TicksPerMinute;

    void Persist()
    {
        try
        {
            Directory.CreateDirectory(Shared.ConfigStore.Dir);
            using var bw = new BinaryWriter(File.Create(SnapFile));
            bw.Write(BootId);
            for (int i = 0; i < Channels; i++)
            {
                var s = _saved[i];
                bw.Write(s != null);
                if (s == null) continue;
                bw.Write(s.Ctrl); bw.Write(s.Duty); bw.Write(s.Ext);
            }
        }
        catch { }
    }

    /// <summary>
    /// If a previous run died without releasing (killed, power button on the app...), its snapshot
    /// is still on disk. Apply it only within the same boot: after a reboot the BIOS already
    /// reprogrammed the chip and the old snapshot is stale.
    /// </summary>
    void RecoverAfterCrash()
    {
        try
        {
            if (!File.Exists(SnapFile)) return;
            using (var br = new BinaryReader(File.OpenRead(SnapFile)))
            {
                long boot = br.ReadInt64();
                if (Math.Abs(boot - BootId) <= 2)
                {
                    for (int i = 0; i < Channels; i++)
                    {
                        if (!br.ReadBoolean()) continue;
                        var s = new Snap { Ctrl = br.ReadByte(), Duty = br.ReadByte(), Ext = br.ReadBytes(4) };
                        int nr = i;
                        Locked(() => RestoreSnap(nr, s));
                    }
                }
            }
            File.Delete(SnapFile);
        }
        catch { }
    }
}
