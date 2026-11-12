namespace InstrumentComponents.Connect;

/// <summary>Options when opening an instrument session.</summary>
public sealed class ConnectOptions
{
    public TimeSpan OpenTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan? PerOpTimeout { get; set; }
    private int? _maxResponseBytes;
    /// <summary>Maximum response payload bytes, excluding SCPI framing; null leaves the raw adapter unbounded. Zero permits only empty payloads.</summary>
    public int? MaxResponseBytes
    {
        get => _maxResponseBytes;
        set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _maxResponseBytes = value; }
    }
    public string Terminator { get; set; } = "\n";
    public AccessMode AccessMode { get; set; } = AccessMode.NoLock;
    public bool ResetOnConnect { get; set; }
    public uint Retries { get; set; } = 2;
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromMilliseconds(100);
    /// <summary>
    /// When true, timed-out writes/queries call reconnect. VISA backends return
    /// unsupported; retry still runs. TCP reconnect needs a SOCKET transport (not VISA).
    /// </summary>
    public bool ReconnectOnFailure { get; set; } = true;

    /// <summary>Single I/O timeout for backends (VISA) that cannot split read vs write.</summary>
    public TimeSpan IoTimeout() => PerOpTimeout ?? (ReadTimeout > WriteTimeout ? ReadTimeout : WriteTimeout);

    public ConnectOptions WithReadTimeout(TimeSpan timeout)
    {
        ReadTimeout = timeout;
        return this;
    }

    public ConnectOptions WithPerOpTimeout(TimeSpan timeout)
    {
        PerOpTimeout = timeout;
        return this;
    }
}
