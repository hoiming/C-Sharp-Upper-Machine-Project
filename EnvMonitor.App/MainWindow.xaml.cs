using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace EnvMonitor.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private bool _connected;
    private bool _relayOn;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        _connected = !_connected;
        StatusText.Text = _connected ? "Connected (demo)" : "Disconnected";
        StatusText.Foreground = _connected ? new SolidColorBrush(Color.FromRgb(22, 163, 106)) : new SolidColorBrush(Color.FromRgb(100, 116, 139));
        StatusDot.Fill = _connected ? new SolidColorBrush(Color.FromRgb(22, 163, 106)) : new SolidColorBrush(Color.FromRgb(148, 163, 184));
        ConnectButton.Content = _connected ? "Disconnect" : "Connect demo";
        EventText.Text = _connected ? "Demo device connected. Ready to poll sensor data." : "Demo device disconnected.";
    }

    private void RelayButton_Click(object sender, RoutedEventArgs e)
    {
        _relayOn = !_relayOn;
        RelayText.Text = _relayOn ? "ON" : "OFF";
        RelayButton.Content = _relayOn ? "Turn off" : "Turn on";
        EventText.Text = _relayOn ? "Relay 1 switched on." : "Relay 1 switched off.";
    }
}