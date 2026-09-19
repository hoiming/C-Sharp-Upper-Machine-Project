using EnvMonitor.Simulator;

var port = 9000;
var faultOptions = new FaultOptions();

for (var index = 0; index < args.Length; index++)
{
	switch (args[index])
	{
		case "--port" when TryReadInt(args, ref index, out var configuredPort):
			port = configuredPort;
			break;
		case "--drop" when TryReadInt(args, ref index, out var drop):
			faultOptions = faultOptions with { DropProbabilityPercent = Math.Clamp(drop, 0, 100) };
			break;
		case "--delay" when TryReadInt(args, ref index, out var delay):
			faultOptions = faultOptions with { ResponseDelay = TimeSpan.FromMilliseconds(Math.Max(0, delay)) };
			break;
		case "--close-after" when TryReadInt(args, ref index, out var closeAfter):
			faultOptions = faultOptions with { CloseAfter = TimeSpan.FromSeconds(Math.Max(0, closeAfter)) };
			break;
		case "--crc-error" when TryReadInt(args, ref index, out var crcError):
			faultOptions = faultOptions with { CrcErrorResponseNumber = Math.Max(0, crcError) };
			break;
		case "--fragment":
			faultOptions = faultOptions with { FragmentResponses = true };
			break;
		case "--coalesce":
			faultOptions = faultOptions with { CoalesceResponses = true };
			break;
		case var positional when index == 0 && int.TryParse(positional, out var positionalPort):
			port = positionalPort;
			break;
		default:
			throw new ArgumentException($"Unknown or invalid argument: {args[index]}");
	}
}

await using var server = new SimulatorServer(port, faultOptions);
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

static bool TryReadInt(string[] arguments, ref int index, out int value)
{
	value = 0;
	return ++index < arguments.Length && int.TryParse(arguments[index], out value);
}
