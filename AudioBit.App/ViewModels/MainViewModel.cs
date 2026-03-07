using System.Collections.ObjectModel;
using System.Windows.Threading;
using AudioBit.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioBit.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AudioSessionService _audioSessionService;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<int, AppAudioViewModel> _viewModelLookup = new();

    private int _refreshInProgress;
    private bool _disposed;
    private string _statusText = "Monitoring default playback device";
    private string _currentDeviceName = "No playback device";
    private bool _isSettingsOpen;
    private bool _isAlwaysOnTop;
    private bool _hideToTrayOnMinimize = true;
    private bool _isEmptyStateVisible = true;

    public MainViewModel(AudioSessionService audioSessionService)
    {
        _audioSessionService = audioSessionService;
        Sessions = new ObservableCollection<AppAudioViewModel>();

        RefreshCommand = new AsyncRelayCommand(RefreshNowAsync);
        ToggleMuteAllCommand = new RelayCommand(ToggleMuteAll);
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsOpen = !IsSettingsOpen);

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _refreshTimer.Tick += RefreshTimerOnTick;
    }

    public ObservableCollection<AppAudioViewModel> Sessions { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IRelayCommand ToggleMuteAllCommand { get; }

    public IRelayCommand ToggleSettingsCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CurrentDeviceName
    {
        get => _currentDeviceName;
        private set => SetProperty(ref _currentDeviceName, value);
    }

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetProperty(ref _isSettingsOpen, value);
    }

    public bool IsAlwaysOnTop
    {
        get => _isAlwaysOnTop;
        set => SetProperty(ref _isAlwaysOnTop, value);
    }

    public bool HideToTrayOnMinimize
    {
        get => _hideToTrayOnMinimize;
        set => SetProperty(ref _hideToTrayOnMinimize, value);
    }

    public bool IsEmptyStateVisible
    {
        get => _isEmptyStateVisible;
        private set => SetProperty(ref _isEmptyStateVisible, value);
    }

    public bool AreAllMuted => Sessions.Count > 0 && Sessions.All(session => session.IsMuted);

    public void Start()
    {
        _refreshTimer.Start();
        _ = RefreshNowAsync();
    }

    public void Stop()
    {
        _refreshTimer.Stop();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimerOnTick;
    }

    private async void RefreshTimerOnTick(object? sender, EventArgs e)
    {
        await RefreshNowAsync();
    }

    private async Task RefreshNowAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            var snapshot = await Task.Run(_audioSessionService.Refresh).ConfigureAwait(true);
            ApplySnapshot(snapshot);

            CurrentDeviceName = _audioSessionService.CurrentDeviceName;
            StatusText = Sessions.Count == 0
                ? $"Waiting for active audio on {CurrentDeviceName}"
                : $"Monitoring {CurrentDeviceName}";
        }
        catch
        {
            StatusText = "Audio session monitoring is temporarily unavailable.";
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInProgress, 0);
            OnPropertyChanged(nameof(AreAllMuted));
        }
    }

    private void ToggleMuteAll()
    {
        var nextMuteState = Sessions.Any(session => !session.IsMuted);
        _audioSessionService.SetAllMuted(nextMuteState);

        foreach (var session in Sessions)
        {
            session.SetMutedVisualState(nextMuteState);
        }

        OnPropertyChanged(nameof(AreAllMuted));
    }

    private void ApplySnapshot(IReadOnlyList<AppAudioModel> models)
    {
        var visibleIds = models.Select(model => model.ProcessId).ToHashSet();
        var staleIds = _viewModelLookup.Keys.Where(id => !visibleIds.Contains(id)).ToArray();

        foreach (var staleId in staleIds)
        {
            if (!_viewModelLookup.TryGetValue(staleId, out var staleViewModel))
            {
                continue;
            }

            Sessions.Remove(staleViewModel);
            _viewModelLookup.Remove(staleId);
        }

        for (var index = 0; index < models.Count; index++)
        {
            var model = models[index];

            if (!_viewModelLookup.TryGetValue(model.ProcessId, out var viewModel))
            {
                viewModel = new AppAudioViewModel(_audioSessionService.SetVolume, _audioSessionService.SetMute);
                _viewModelLookup[model.ProcessId] = viewModel;
                Sessions.Insert(index, viewModel);
            }

            viewModel.Apply(model);

            var currentIndex = Sessions.IndexOf(viewModel);
            if (currentIndex >= 0 && currentIndex != index)
            {
                Sessions.Move(currentIndex, index);
            }
        }

        IsEmptyStateVisible = Sessions.Count == 0;
    }
}
