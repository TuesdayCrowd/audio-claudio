using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Cli.Composition;

/// <summary>
/// Thread-safe accumulator for frames drained from a live mic source, snapshotted periodically by a
/// background transcribe loop (the polyphonic live-view prototype, `listen --view --poly` --
/// <see cref="AudioClaudio.Cli.Commands.LivePolyphonicView"/>). One thread calls <see cref="Add"/> for
/// every captured frame; a different thread calls <see cref="Snapshot"/> to get a point-in-time copy
/// to re-transcribe, WITHOUT ever holding the lock during the (slow) inference that follows --
/// <see cref="Snapshot"/> copies and returns immediately, so the drain thread is never blocked waiting
/// on a model run.
/// </summary>
public sealed class FrameAccumulator
{
    private readonly object _gate = new();
    private readonly List<Frame> _frames = new();

    /// <summary>Appends one frame. Safe to call from the mic-draining thread while another thread
    /// concurrently calls <see cref="Snapshot"/>.</summary>
    public void Add(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            _frames.Add(frame);
        }
    }

    /// <summary>Drops every accumulated frame, so one <see cref="FrameAccumulator"/> can be reused
    /// across successive browser Start/Stop takes without carrying a prior take's audio into the next.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _frames.Clear();
        }
    }

    /// <summary>The number of frames accumulated so far.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>A point-in-time copy of every frame added so far, safe to read after the lock is
    /// released (later <see cref="Add"/> calls never mutate a previously returned snapshot).</summary>
    public IReadOnlyList<Frame> Snapshot()
    {
        lock (_gate)
        {
            return _frames.ToArray();
        }
    }
}
