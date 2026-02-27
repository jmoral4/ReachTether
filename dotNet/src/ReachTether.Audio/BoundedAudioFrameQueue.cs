using System.Collections.Concurrent;

namespace ReachTether.Audio;

public sealed class BoundedAudioFrameQueue
{
    private readonly ConcurrentQueue<AudioFrame> _queue = new();
    private readonly int _maxFrames;
    private int _count;

    public BoundedAudioFrameQueue(int maxFrames)
    {
        if (maxFrames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFrames));
        }

        _maxFrames = maxFrames;
    }

    public long DroppedFrames { get; private set; }

    public int Count => Volatile.Read(ref _count);

    public void Enqueue(AudioFrame frame)
    {
        while (Count >= _maxFrames && _queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
            DroppedFrames++;
        }

        _queue.Enqueue(frame);
        Interlocked.Increment(ref _count);
    }

    public bool TryDequeue(out AudioFrame? frame)
    {
        if (_queue.TryDequeue(out frame))
        {
            Interlocked.Decrement(ref _count);
            return true;
        }

        return false;
    }

    public void Clear()
    {
        while (_queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
        }
    }
}
