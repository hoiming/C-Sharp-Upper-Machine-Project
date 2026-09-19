using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvMonitor.Communication;

namespace EnvMonitor.App;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IFrameClient _client;
    private readonly IDeviceService _device;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pollTimer;
    private bool _isPolling;

    [ObservableProperty]
    private string status = "Disconnected";

    [ObservableProperty]
    private string connectButtonText = "Connect";

    [ObservableProperty]
    private string eventMessage = "Application ready.";

    [ObservableProperty]
    private double temperature;

    [ObservableProperty]
    private double humidity;

    [ObservableProperty]
    private double pressure;

    [ObservableProperty]
    private bool relay1;

    [ObservableProperty]
    private bool isConnected;

    public MainViewModel(IFrameClient client, IDeviceService device, Dispatcher dispatcher)
    {
        _client = client;
        _device = device;
        _dispatcher = dispatcher;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _pollTimer.Tick += PollTimer_Tick;
        _client.ConnectionChanged += OnConnectionChanged;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected)
        {
            await _client.DisconnectAsync();
            return;
        }

        try
        {
            Status = "Connecting";
            EventMessage = "Connecting to 127.0.0.1:9000...";
            await _client.ConnectAsync("127.0.0.1", 9000);
        }
        catch (Exception exception)
        {
            Status = $"Connection failed: {exception.Message}";
            EventMessage = "Unable to connect to the simulator.";
        }
    }

    [RelayCommand]
    private async Task ToggleRelayAsync()
    {
        var requestedState = Relay1;

        try
        {
            await _device.SetRelayAsync(1, requestedState);
            EventMessage = $"Relay 1 switched {(requestedState ? "on" : "off")}.";
        }
        catch (Exception exception)
        {
            Relay1 = !requestedState;
            Status = $"Relay error: {exception.Message}";
            EventMessage = "Relay command failed; state rolled back.";
        }
    }

    public async ValueTask DisposeAsync()
    {
        _pollTimer.Stop();
        _pollTimer.Tick -= PollTimer_Tick;
        _client.ConnectionChanged -= OnConnectionChanged;
        await _client.DisposeAsync();
    }

    private void OnConnectionChanged(ConnectionState state)
    {
        if (_dispatcher.CheckAccess())
        {
            ApplyConnectionState(state);
        }
        else
        {
            _dispatcher.Invoke(() => ApplyConnectionState(state));
        }
    }

    private void ApplyConnectionState(ConnectionState state)
    {
        Status = state.ToString();
        IsConnected = state == ConnectionState.Connected;
        ConnectButtonText = IsConnected ? "Disconnect" : "Connect";

        if (IsConnected)
        {
            _pollTimer.Start();
        }
        else
        {
            _pollTimer.Stop();
        }

        EventMessage = state switch
        {
            ConnectionState.Connected => "Device connected.",
            ConnectionState.Reconnecting => "Connection lost; retrying...",
            ConnectionState.Disconnected => "Device disconnected.",
            _ => "Connecting to device..."
        };
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_isPolling || !IsConnected)
        {
            return;
        }

        _isPolling = true;
        try
        {
            var data = await _device.ReadAsync().ConfigureAwait(true);
            if (!IsConnected)
            {
                return;
            }

            Temperature = data.Temp;
            Humidity = data.Humidity;
            Pressure = data.Pressure;
            Relay1 = data.Relay == 1;
            Status = "Connected";
            EventMessage = "Sensor data updated.";
        }
        catch (TimeoutException)
        {
            if (IsConnected)
            {
                Status = "Read timeout";
                EventMessage = "Sensor read timed out.";
            }
        }
        catch (Exception exception)
        {
            if (IsConnected)
            {
                Status = exception.Message;
                EventMessage = "Sensor read failed.";
            }
        }
        finally
        {
            _isPolling = false;
        }
    }
}