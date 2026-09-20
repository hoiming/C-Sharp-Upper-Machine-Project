using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvMonitor.Communication;
using Serilog;

namespace EnvMonitor.App;

public partial class LedTabViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISerialFrameClient _client;
    private readonly ILedService _ledService;

    [ObservableProperty]
    private string status = "Disconnected";

    [ObservableProperty]
    private string selectedPort = string.Empty;

    [ObservableProperty]
    private int baudRate = 115200;

    [ObservableProperty]
    private bool ledOn;

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private string eventMessage = "Select the STM32 COM port.";

    public ObservableCollection<string> AvailablePorts { get; } = [];

    public string ConnectButtonText => IsConnected ? "Disconnect" : "Connect board";

    public LedTabViewModel(ISerialFrameClient client, ILedService ledService)
    {
        _client = client;
        _ledService = ledService;
        RefreshPorts();
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        foreach (var port in SerialPort.GetPortNames().OrderBy(name => name))
        {
            AvailablePorts.Add(port);
        }

        if (string.IsNullOrEmpty(SelectedPort) && AvailablePorts.Count > 0)
        {
            SelectedPort = AvailablePorts[0];
        }

        EventMessage = AvailablePorts.Count == 0
            ? "No COM port detected. Connect the STM32 board first."
            : $"Found {AvailablePorts.Count} COM port(s).";
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedPort))
        {
            Status = "No port";
            EventMessage = "Select a COM port before connecting.";
            return;
        }

        try
        {
            Status = "Connecting";
            EventMessage = $"Opening {SelectedPort} at {BaudRate} baud...";
            await _client.ConnectAsync(SelectedPort, BaudRate);
            IsConnected = true;
            Status = "Connected";
            EventMessage = "STM32 board connected. PC13 LED is ready.";
            OnPropertyChanged(nameof(ConnectButtonText));
            Log.Information("STM32 serial connected on {Port} at {BaudRate}", SelectedPort, BaudRate);
        }
        catch (Exception exception)
        {
            IsConnected = false;
            Status = "Connection failed";
            EventMessage = exception.Message;
            OnPropertyChanged(nameof(ConnectButtonText));
            Log.Error(exception, "STM32 serial connection failed");
        }
    }

    [RelayCommand]
    private async Task ToggleLedAsync()
    {
        var requestedState = LedOn;
        try
        {
            LedOn = await _ledService.SetLedAsync(1, requestedState);
            Status = "Connected";
            EventMessage = $"PC13 LED switched {(LedOn ? "on" : "off")}.";
            Log.Information("STM32 PC13 LED switched {State}", LedOn ? "on" : "off");
        }
        catch (Exception exception)
        {
            LedOn = !requestedState;
            Status = "Command failed";
            EventMessage = exception.Message;
            Log.Error(exception, "STM32 LED command failed");
        }
    }

    private async Task DisconnectAsync()
    {
        await _client.DisconnectAsync();
        IsConnected = false;
        Status = "Disconnected";
        EventMessage = "STM32 board disconnected.";
        OnPropertyChanged(nameof(ConnectButtonText));
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
    }
}