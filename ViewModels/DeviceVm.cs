using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiBtOut.ViewModels;

/// <summary>One row in the device list: a checkbox bound to an active render endpoint.</summary>
public sealed class DeviceVm : INotifyPropertyChanged
{
    private readonly Action _onSelectionChanged;
    private bool _isSelected;
    private int _volumePercent = 100;

    public DeviceVm(MMDevice device, Action onSelectionChanged)
    {
        Device = device;
        _onSelectionChanged = onSelectionChanged;
        try
        {
            using AudioClient client = device.AudioClient; // probe; must be disposed (COM)
            WaveFormat fmt = client.MixFormat;
            Details = $"{fmt.Channels} ch · {fmt.SampleRate} Hz · {fmt.BitsPerSample}-bit";
        }
        catch
        {
            Details = "";
        }
    }

    public MMDevice Device { get; }

    public string Name => Device.FriendlyName;

    public string Details { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
            _onSelectionChanged();
        }
    }

    /// <summary>Per-device stream volume, 0–100, bound to the slider.</summary>
    public int VolumePercent
    {
        get => _volumePercent;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (_volumePercent == value)
            {
                return;
            }

            _volumePercent = value;
            OnPropertyChanged();
            VolumeChanged?.Invoke(this, value);
        }
    }

    /// <summary>Raised whenever the user moves the volume slider.</summary>
    public event EventHandler<int>? VolumeChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
