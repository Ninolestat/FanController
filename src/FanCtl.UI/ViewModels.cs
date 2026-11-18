using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FanCtl.UI;

public abstract class Vm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class TempVm : Vm
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Hw { get; init; }

    float _value = float.NaN;
    string _text = "—";
    public float Value
    {
        get => _value;
        set { _value = value; Text = float.IsNaN(value) ? "—" : $"{value:0}°"; }
    }
    public string Text { get => _text; private set => Set(ref _text, value); }
}

public sealed class FanVm : Vm
{
    public required string Id { get; init; }
    public required string DefaultName { get; init; }
    public required string Hw { get; init; }

    string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    public float Rpm { get; private set; } = float.NaN;
    public float Duty { get; private set; } = float.NaN;
    public float Input { get; private set; } = float.NaN;

    string _rpmText = "—", _dutyText = "";
    double _opacity = 1;
    public string RpmText { get => _rpmText; private set => Set(ref _rpmText, value); }
    public string DutyText { get => _dutyText; private set => Set(ref _dutyText, value); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, value); }

    public float Min { get; private set; }

    // Row slider (direct manual control)
    double _sliderValue, _sliderMin;
    string _sliderText = "";
    public bool SyncingSlider;
    public double SliderValue { get => _sliderValue; set => Set(ref _sliderValue, value); }
    public double SliderMin { get => _sliderMin; set => Set(ref _sliderMin, value); }
    public string SliderText { get => _sliderText; set => Set(ref _sliderText, value); }

    string _status = "";
    bool _warn;
    System.Windows.Visibility _visibility = System.Windows.Visibility.Visible;
    /// <summary>Plain-language "what is controlling this fan right now".</summary>
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool Warn { get => _warn; set => Set(ref _warn, value); }
    public System.Windows.Visibility Visibility { get => _visibility; set => Set(ref _visibility, value); }

    public void Update(float rpm, float duty, float input, float min)
    {
        Rpm = rpm; Duty = duty; Input = input; Min = min;
        RpmText = float.IsNaN(rpm) ? "—" : $"{rpm:0} RPM";
        DutyText = float.IsNaN(duty) ? "" : $"{duty:0}%";
    }
}
