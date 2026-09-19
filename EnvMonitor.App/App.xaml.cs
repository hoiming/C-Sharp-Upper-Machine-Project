using System.IO;
using System.Windows;
using EnvMonitor.Communication;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace EnvMonitor.App;

public partial class App : Application
{
	private MainViewModel? _viewModel;

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		Log.Logger = new LoggerConfiguration()
			.MinimumLevel.Information()
			.WriteTo.File(
				Path.Combine(AppContext.BaseDirectory, "logs", "app-.log"),
				rollingInterval: RollingInterval.Day,
				retainedFileCountLimit: 14,
				shared: true)
			.CreateLogger();

		try
		{
			var configuration = new ConfigurationBuilder()
				.SetBasePath(AppContext.BaseDirectory)
				.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
				.Build();
			var settings = configuration.Get<AppSettings>()
				?? throw new InvalidOperationException("AppSettings could not be loaded.");
			settings.Validate();
			Log.Information("Application starting with device {Host}:{Port}", settings.Device.Host, settings.Device.Port);

			var client = new TcpClientService();
			var device = new DeviceService(client, settings.Device.RequestTimeoutMs);
			_viewModel = new MainViewModel(client, device, Dispatcher, settings);

			MainWindow = new MainWindow(_viewModel);
			MainWindow.Show();
		}
		catch (Exception exception)
		{
			Log.Fatal(exception, "Application startup failed");
			Log.CloseAndFlush();
			throw;
		}
	}

	protected override void OnExit(ExitEventArgs e)
	{
		_viewModel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
		Log.Information("Application stopped");
		Log.CloseAndFlush();
		base.OnExit(e);
	}
}

