namespace EnvMonitor.App;

public sealed class AppSettings
{
    public DeviceSettings Device { get; set; } = new();

    public ThresholdSettings Thresholds { get; set; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Device.Host))
        {
            throw new InvalidOperationException("Device.Host must be configured.");
        }

        if (Device.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Device.Port must be between 1 and 65535.");
        }

        if (Device.PollIntervalMs < 100 || Device.RequestTimeoutMs < 1)
        {
            throw new InvalidOperationException("Polling and timeout values are invalid.");
        }
    }
}

public sealed class DeviceSettings
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 9000;

    public int PollIntervalMs { get; set; } = 1000;

    public int RequestTimeoutMs { get; set; } = 1000;
}

public sealed class ThresholdSettings
{
    public double Temperature { get; set; } = 30;

    public double Humidity { get; set; } = 70;
}