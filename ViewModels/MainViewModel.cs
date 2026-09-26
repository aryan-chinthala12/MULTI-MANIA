using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using NAudio.CoreAudioApi;
using MultiBtOut.Audio;

namespace MultiBtOut.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly RouterEngine _engine = new();
    private readonly SynchronizationContext _ui;
    private readonly StringBuilder _log = new();
    private bool _busy;
    private bool _disposed;

    public MainViewModel()
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        RefreshCommand = new RelayCommand(_ => RefreshDevices(), _ => !IsBusy);
        StartCommand = new RelayCommand(
            _ => _ = StartRoutingAsync(),
            _ => !IsBusy && !_engine.IsRunning && SelectedCount > 0);
        StopCommand = new RelayCommand(
            _ => _ = StopRoutingAsync(),
            _ => !IsBusy && _engine.IsRunning);

        _engine.Log += msg => AppendLog(msg);
        _engine.HealthSample += () => _ui.Post(_ => RefreshHealth(), null);

        RefreshDevices();
    }

    public ObservableCollection<DeviceVm> Devices { get; } = new();

    public ObservableCollection<string> HealthLines { get; } = new();

    public ICommand RefreshCommand { get; }

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public int SelectedCount => Devices.Count(d => d.IsSelected);

    public string SelectionHint => SelectedCount switch
    {
        0 => "Select one or more output devices to mirror system audio to (3–4 Bluetooth sinks is the target scenario).",
        _ => $"{SelectedCount} device(s) selected.",
    };

    public string Log
    {
        get { lock (_log) { return _log.ToString(); } }
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (_busy != value)
            {
                _busy = value;
                OnPropertyChanged();
            }
        }
    }

    public void RefreshDevices()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            var devices = RouterEngine.GetActiveRenderDevices();
            Devices.Clear();
            foreach (MMDevice device in devices)
            {
                var vm = new DeviceVm(device, OnDeviceSelectionChanged);
                vm.VolumeChanged += (sender, percent) =>
                {
                    var row = (DeviceVm)sender!;
                    _engine.SetDeviceVolume(row.Device.ID, percent / 100f);
                };
                Devices.Add(vm);
            }

            AppendLog($"Found {devices.Count} active render endpoint(s).");
        }
        catch (Exception ex)
        {
            AppendLog($"Device enumeration failed: {ex.Message}");
        }
    }

    private async Task StartRoutingAsync()
    {
        var selected = Devices.Where(d => d.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        // Carry the slider positions into the new routers so pre-start volume
        // adjustments are not silently reset to 100 %.
        var volumes = selected.ToDictionary(d => d.Device.ID, d => d.VolumePercent / 100f);

        // Pass endpoint ID strings, never MMDevice objects: those were created on the STA
        // UI thread and cannot cross to the engine's MTA worker threads (COM apartments).
        var deviceIds = selected.Select(d => d.Device.ID).ToList();

        IsBusy = true;
        try
        {
            await Task.Run(() => _engine.StartAsync(deviceIds, volumes)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppendLog($"START FAILED: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopRoutingAsync()
    {
        IsBusy = true;
        try
        {
            await Task.Run(_engine.StopAsync).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppendLog($"Stop failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnDeviceSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionHint));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshHealth()
    {
        if (_disposed)
        {
            return;
        }

        HealthLines.Clear();
        string? tap = _engine.GetTapSummary();
        if (tap is not null)
        {
            HealthLines.Add(tap);
        }

        foreach (DeviceRouter router in _engine.Routers)
        {
            HealthLines.Add($"{router.DisplayName} — {router.Status}");
        }
    }

    private void AppendLog(string message)
    {
        void Apply()
        {
            lock (_log)
            {
                _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
                if (_log.Length > 32_000)
                {
                    string text = _log.ToString();
                    int cut = text.LastIndexOf('\n', text.Length - 24_000);
                    _log.Clear().Append(cut > 0 ? text[(cut + 1)..] : text[^24_000..]);
                }
            }

            OnPropertyChanged(nameof(Log));
        }

        if (SynchronizationContext.Current == _ui)
        {
            Apply();
        }
        else
        {
            _ui.Post(_ => Apply(), null);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine.Dispose();
    }
}
