namespace AudioClaudio.Domain;

/// <summary>
/// The two pipeline parameters of framing: the window <see cref="Size"/> (N samples) and the
/// <see cref="Hop"/> (H samples) between successive windows. R2.4: these are parameters, never
/// constants scattered through the code. Hop &lt;= Size is the gap-free regime.
/// </summary>
public readonly record struct FrameParameters
{
    public int Size { get; }
    public int Hop { get; }

    public FrameParameters(int size, int hop)
    {
        if (size < 1)
            throw new ArgumentOutOfRangeException(nameof(size), size, "Frame size must be >= 1.");
        if (hop < 1)
            throw new ArgumentOutOfRangeException(nameof(hop), hop, "Hop must be >= 1.");
        Size = size;
        Hop = hop;
    }
}
