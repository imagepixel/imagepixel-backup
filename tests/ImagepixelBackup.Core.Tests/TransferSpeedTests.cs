namespace ImagepixelBackup.Core.Tests;

public class TransferSpeedTests
{
    private const long Mb = 1024 * 1024;

    private static readonly DateTimeOffset Start = new(2026, 11, 18, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_is_known_at_the_start()
    {
        var speed = new TransferSpeed();
        Assert.Null(speed.BytesPerSecond);

        // less than a second says too little
        speed.Add(Start, 0);
        speed.Add(Start.AddMilliseconds(500), 3 * Mb);
        Assert.Null(speed.BytesPerSecond);
    }

    [Fact]
    public void Steady_progress_gives_its_rate()
    {
        var speed = new TransferSpeed();

        // 2 MB every 250 ms
        for (var step = 0; step <= 12; step++)
        {
            speed.Add(Start.AddMilliseconds(250 * step), 2 * Mb * step);
        }

        Assert.Equal(8.0 * Mb, speed.BytesPerSecond!.Value, 1.0);
    }

    [Fact]
    public void Only_the_last_seconds_count()
    {
        var speed = new TransferSpeed(TimeSpan.FromSeconds(5));

        // ten slow seconds at 1 MB/s, then ten fast ones at 50 MB/s, as when data is already in the backup
        long done = 0;
        for (var step = 1; step <= 40; step++)
        {
            done += Mb / 4;
            speed.Add(Start.AddMilliseconds(250 * step), done);
        }

        Assert.Equal(1.0 * Mb, speed.BytesPerSecond!.Value, 1.0);

        for (var step = 41; step <= 80; step++)
        {
            done += 50 * Mb / 4;
            speed.Add(Start.AddMilliseconds(250 * step), done);
        }

        Assert.Equal(50.0 * Mb, speed.BytesPerSecond!.Value, 1.0);
    }

    [Fact]
    public void A_pause_shows_as_a_falling_speed()
    {
        var speed = new TransferSpeed(TimeSpan.FromSeconds(5));
        for (var step = 0; step <= 8; step++)
        {
            speed.Add(Start.AddMilliseconds(250 * step), 4 * Mb * step);
        }

        Assert.Equal(16.0 * Mb, speed.BytesPerSecond!.Value, 1.0);

        // restic keeps reporting, but nothing moves for ten seconds
        for (var step = 9; step <= 48; step++)
        {
            speed.Add(Start.AddMilliseconds(250 * step), 32 * Mb);
        }

        Assert.Equal(0, speed.BytesPerSecond!.Value, 1.0);
    }

    [Fact]
    public void A_new_run_starts_from_scratch()
    {
        var speed = new TransferSpeed();
        speed.Add(Start, 0);
        speed.Add(Start.AddSeconds(2), 20 * Mb);
        Assert.Equal(10.0 * Mb, speed.BytesPerSecond!.Value, 1.0);

        // the next backup counts from zero again
        speed.Add(Start.AddSeconds(60), 0);
        Assert.Null(speed.BytesPerSecond);

        speed.Add(Start.AddSeconds(62), 4 * Mb);
        Assert.Equal(2.0 * Mb, speed.BytesPerSecond!.Value, 1.0);
    }
}
