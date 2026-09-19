using EnvMonitor.Simulator;

var port = args.Length > 0 && int.TryParse(args[0], out var parsedPort) ? parsedPort : 9000;
await using var server = new SimulatorServer(port);
await server.StartAsync();

Console.WriteLine($"Simulator listening on 127.0.0.1:{server.Port}");
Console.WriteLine("Press Ctrl+C to stop.");

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
	eventArgs.Cancel = true;
	shutdown.Cancel();
};

try
{
	await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
}
catch (OperationCanceledException)
{
}
