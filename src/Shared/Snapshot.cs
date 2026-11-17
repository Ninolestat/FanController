using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace FanCtl.Shared;

public static class Ipc
{
    public const string Mutex = @"Local\FanCtl.Instance";
    public const string Map = @"Local\FanCtl.Snapshot";
    public const string ReloadEvent = @"Local\FanCtl.Reload";
    public const string ShowEvent = @"Local\FanCtl.Show";      // -> service: open UI
    public const string UiFocusEvent = @"Local\FanCtl.UiFocus"; // -> UI: bring to front
    public const string IdentifyEvent = @"Local\FanCtl.Identify"; // -> service: pulse the fan named in IdentifyFile
    public static string IdentifyFile => Path.Combine(ConfigStore.Dir, "identify");
    public const int IdentifyMs = 8000;
    public const int MapSize = 64 * 1024;
    public const int Header = 8; // int seq, int len
}

public struct TempReading
{
    public string Id, Name, Hw;
    public float Value;
}

public struct FanReading
{
    public string Id, Name, Hw;
    public float Rpm;    // NaN if no tach
    public float Duty;   // current duty reported by hardware (%), NaN if unknown
    public float Input;  // temperature fed to the curve (after hysteresis), NaN if n/a
    public float Min;    // lowest duty the device accepts (e.g. Kraken fans: 20%)
}

/// <summary>
/// Single-writer seqlock over a memory-mapped page. The writer only runs while the UI is open.
/// </summary>
public sealed class SnapshotWriter : IDisposable
{
    readonly MemoryMappedFile _mmf;
    readonly MemoryMappedViewAccessor _view;
    readonly MemoryStream _ms = new(4096);
    readonly BinaryWriter _bw;
    int _seq;

    public SnapshotWriter()
    {
        _mmf = MemoryMappedFile.CreateOrOpen(Ipc.Map, Ipc.MapSize);
        _view = _mmf.CreateViewAccessor(0, Ipc.MapSize);
        _bw = new BinaryWriter(_ms, Encoding.UTF8, leaveOpen: true);
    }

    public BinaryWriter Begin()
    {
        _ms.Position = 0;
        _ms.SetLength(0);
        return _bw;
    }

    public void Commit()
    {
        _bw.Flush();
        int len = (int)_ms.Length;
        if (len > Ipc.MapSize - Ipc.Header) return;
        _view.Write(0, ++_seq);           // odd: writing
        Thread.MemoryBarrier();
        _view.Write(4, len);
        _view.WriteArray(Ipc.Header, _ms.GetBuffer(), 0, len);
        Thread.MemoryBarrier();
        _view.Write(0, ++_seq);           // even: stable
    }

    public void Dispose() { _view.Dispose(); _mmf.Dispose(); }
}

public sealed class SnapshotReader : IDisposable
{
    readonly MemoryMappedFile _mmf;
    readonly MemoryMappedViewAccessor _view;
    byte[] _buf = new byte[4096];
    int _lastSeq = -1;

    public readonly List<TempReading> Temps = new();
    public readonly List<FanReading> Fans = new();

    public SnapshotReader()
    {
        _mmf = MemoryMappedFile.OpenExisting(Ipc.Map, MemoryMappedFileRights.Read);
        _view = _mmf.CreateViewAccessor(0, Ipc.MapSize, MemoryMappedFileAccess.Read);
    }

    /// <summary>Returns true if a new snapshot was read.</summary>
    public bool Poll()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int s1 = _view.ReadInt32(0);
            if ((s1 & 1) != 0) { Thread.SpinWait(50); continue; }
            if (s1 == _lastSeq) return false;
            int len = _view.ReadInt32(4);
            if (len <= 0 || len > Ipc.MapSize - Ipc.Header) return false;
            if (_buf.Length < len) _buf = new byte[len];
            _view.ReadArray(Ipc.Header, _buf, 0, len);
            Thread.MemoryBarrier();
            if (_view.ReadInt32(0) != s1) continue;
            _lastSeq = s1;
            Parse(len);
            return true;
        }
        return false;
    }

    void Parse(int len)
    {
        Temps.Clear(); Fans.Clear();
        using var br = new BinaryReader(new MemoryStream(_buf, 0, len), Encoding.UTF8);
        int n = br.ReadInt32();
        for (int i = 0; i < n; i++)
            Temps.Add(new TempReading { Id = br.ReadString(), Name = br.ReadString(), Hw = br.ReadString(), Value = br.ReadSingle() });
        n = br.ReadInt32();
        for (int i = 0; i < n; i++)
            Fans.Add(new FanReading
            {
                Id = br.ReadString(), Name = br.ReadString(), Hw = br.ReadString(),
                Rpm = br.ReadSingle(), Duty = br.ReadSingle(), Input = br.ReadSingle(), Min = br.ReadSingle()
            });
    }

    public void Dispose() { _view.Dispose(); _mmf.Dispose(); }
}
