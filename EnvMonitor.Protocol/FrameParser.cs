namespace EnvMonitor.Protocol;

public sealed class FrameParser
{
    private readonly List<byte> _buffer = [];

    public IEnumerable<Frame> Feed(byte[] data)
    {
        _buffer.AddRange(data);

        while (true)
        {
            var headerIndex = FindHeader();
            if (headerIndex < 0)
            {
                KeepPossibleHeaderPrefix();
                yield break;
            }

            if (headerIndex > 0)
            {
                _buffer.RemoveRange(0, headerIndex);
            }

            if (_buffer.Count < 4)
            {
                yield break;
            }

            var declaredLength = (_buffer[2] << 8) | _buffer[3];
            if (declaredLength < Frame.MinimumLength || declaredLength > Frame.MaximumLength)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            if (_buffer.Count < declaredLength)
            {
                yield break;
            }

            var encoded = _buffer.GetRange(0, declaredLength).ToArray();
            Frame? frame = null;
            try
            {
                frame = Frame.Decode(encoded);
            }
            catch (ArgumentException)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            _buffer.RemoveRange(0, declaredLength);
            yield return frame;
        }
    }

    private int FindHeader()
    {
        for (var index = 0; index < _buffer.Count - 1; index++)
        {
            if (_buffer[index] == Frame.HeaderFirstByte && _buffer[index + 1] == Frame.HeaderSecondByte)
            {
                return index;
            }
        }

        return -1;
    }

    private void KeepPossibleHeaderPrefix()
    {
        if (_buffer.Count > 0 && _buffer[^1] == Frame.HeaderFirstByte)
        {
            _buffer.RemoveRange(0, _buffer.Count - 1);
        }
        else
        {
            _buffer.Clear();
        }
    }
}