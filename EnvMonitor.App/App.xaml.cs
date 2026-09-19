using System.Windows;
using EnvMonitor.Communication;

namespace EnvMonitor.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
	private MainViewModel? _viewModel;

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		var client = new TcpClientService();
		var device = new DeviceService(client);
		_viewModel = new MainViewModel(client, device, Dispatcher);

		MainWindow = new MainWindow(_viewModel);
		MainWindow.Show();
	}

	protected override void OnExit(ExitEventArgs e)
	{
		_viewModel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
		base.OnExit(e);
	}
}

