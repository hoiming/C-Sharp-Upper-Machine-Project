namespace EnvMonitor.Protocol;

public static class Crc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;

        foreach (var value in data)
        {
            crc ^= value;

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0
                    ? (ushort)(crc >> 1)
                    : (ushort)((crc >> 1) ^ 0xA001);
            }
        }

        return crc;
    }
}