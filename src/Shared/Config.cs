using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanCtl.Shared;

public enum FanMode { Auto, Fixed, Curve }

public sealed class CurvePoint
{
    public float T { get; set; }
    public float D { get; set; }
    public CurvePoint() { }
    public CurvePoint(float t, float d) { T = t; D = d; }
}

public sealed class FanConfig
{
    public string Id { get; set; } = "";
    public string? Alias { get; set; }
    public FanMode Mode { get; set; } = FanMode.Auto;
    public float Fixed { get; set; } = 50;
    public string? Sensor { get; set; }
    public List<CurvePoint> Points { get; set; } = Curves.Balanced();
    /// <summary>°C the temperature must drop before the duty is allowed to decrease.</summary>
    public float Hysteresis { get; set; } = 2;
    public bool Hidden { get; set; }
}

public sealed class AppConfig
{
    public int IntervalMs { get; set; } = 1000;
    /// <summary>2 = the app owns every fan with recommended curves (older configs get migrated once).</summary>
    public int Version { get; set; }
    public List<FanConfig> Fans { get; set; } = new();

    public FanConfig? Find(string id)
    {
        foreach (var f in Fans) if (f.Id == id) return f;
        return null;
    }
}

public static class Curves
{
    public static List<CurvePoint> Silent() => [new(30, 20), new(50, 30), new(65, 45), new(75, 65), new(85, 100)];
    public static List<CurvePoint> Balanced() => [new(30, 30), new(50, 40), new(65, 60), new(75, 80), new(85, 100)];
    public static List<CurvePoint> Performance() => [new(30, 45), new(45, 60), new(60, 80), new(70, 100)];
    /// <summary>Radiator fans on liquid temperature (liquid moves slowly, 25–45 °C is the useful range).</summary>
    public static List<CurvePoint> Radiator() => [new(25, 25), new(30, 35), new(35, 55), new(40, 80), new(45, 100)];
    /// <summary>AIO pump on liquid temperature: never too low, full speed when hot.</summary>
    public static List<CurvePoint> Pump() => [new(25, 60), new(35, 70), new(42, 90), new(48, 100)];

    /// <summary>Piecewise-linear interpolation; points must be sorted by T.</summary>
    public static float Eval(List<CurvePoint> p, float t)
    {
        int n = p.Count;
        if (n == 0) return 100;
        if (t <= p[0].T) return p[0].D;
        for (int i = 1; i < n; i++)
        {
            var b = p[i];
            if (t <= b.T)
            {
                var a = p[i - 1];
                float span = b.T - a.T;
                return span <= 0 ? b.D : a.D + (b.D - a.D) * (t - a.T) / span;
            }
        }
        return p[n - 1].D;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfig))]
internal partial class ConfigJson : JsonSerializerContext { }

public static class ConfigStore
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanCtl");
    public static readonly string FilePath = Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using var fs = File.OpenRead(FilePath);
                var cfg = JsonSerializer.Deserialize(fs, ConfigJson.Default.AppConfig);
                if (cfg != null)
                {
                    foreach (var f in cfg.Fans)
                    {
                        f.Points.Sort((a, b) => a.T.CompareTo(b.T));
                        // Migrate legacy LUID-based WDDM GPU sensor IDs (e.g. /gpu-wddm/000013e32/temperature/0) to index 0.
                        if (f.Sensor != null && f.Sensor.StartsWith("/gpu-wddm/", StringComparison.Ordinal))
                        {
                            const string prefix = "/gpu-wddm/";
                            int slash = f.Sensor.IndexOf('/', prefix.Length);
                            if (slash > prefix.Length && slash - prefix.Length >= 8)
                                f.Sensor = "/gpu-wddm/0" + f.Sensor[slash..];
                        }
                    }
                    if (cfg.IntervalMs < 250) cfg.IntervalMs = 250;
                    return cfg;
                }
            }
        }
        catch { /* corrupt config -> defaults */ }
        return new AppConfig();
    }

    public static void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(Dir);
        string tmp = FilePath + ".tmp";
        using (var fs = File.Create(tmp))
            JsonSerializer.Serialize(fs, cfg, ConfigJson.Default.AppConfig);
        File.Move(tmp, FilePath, overwrite: true);
    }
}
