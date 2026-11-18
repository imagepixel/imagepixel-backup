namespace ImagepixelBackup.Core;

/// <summary>
/// Works out how fast a running backup gets through the data. restic reports several
/// times a second how many bytes it has dealt with so far; the speed is the average
/// over the last few seconds, so that it neither jumps with every file nor lags far
/// behind. Data that is already in the backup counts too: restic only has to look at
/// it, which is why a later backup runs much faster than the first one.
/// </summary>
public sealed class TransferSpeed(TimeSpan? window = null)
{
    private static readonly TimeSpan SampleDistance = TimeSpan.FromMilliseconds(200);

    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(5);
    private readonly List<(DateTimeOffset Time, long Bytes)> _samples = [];

    /// <summary>Bytes per second over the last seconds, or null while too little is known.</summary>
    public double? BytesPerSecond { get; private set; }

    /// <summary>Takes note of how many bytes were done at a point in time.</summary>
    public void Add(DateTimeOffset time, long bytesDone)
    {
        // a new run counts from zero again, and a clock that was set back says nothing
        if (_samples.Count > 0 && (bytesDone < _samples[^1].Bytes || time < _samples[^1].Time))
        {
            Reset();
        }

        if (_samples.Count == 0 || time - _samples[^1].Time >= SampleDistance)
        {
            _samples.Add((time, bytesDone));
        }

        // keep one sample that is at least as old as the window, and nothing older
        while (_samples.Count > 2 && time - _samples[1].Time >= _window)
        {
            _samples.RemoveAt(0);
        }

        var (firstTime, firstBytes) = _samples[0];
        var seconds = (time - firstTime).TotalSeconds;
        if (seconds >= 1)
        {
            BytesPerSecond = (bytesDone - firstBytes) / seconds;
        }
    }

    /// <summary>Forgets everything, for the next run.</summary>
    public void Reset()
    {
        _samples.Clear();
        BytesPerSecond = null;
    }
}
