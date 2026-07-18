using ReachTether.Audio;
using Xunit;

namespace ReachTether.Tests;

public class BoundedAudioFrameQueueTests
{
    [Fact]
    public void Queue_ShouldEnforceCapacity()
    {
        // Arrange
        var format = new AudioFormat(16000, 1, 16);
        var queue = new BoundedAudioFrameQueue(2);
        var frame1 = new AudioFrame(new byte[320], format, 100); 
        var frame2 = new AudioFrame(new byte[320], format, 110);
        var frame3 = new AudioFrame(new byte[320], format, 120);

        // Act
        queue.Enqueue(frame1);
        queue.Enqueue(frame2);
        queue.Enqueue(frame3); // Should drop frame1

        // Assert
        Assert.Equal(2, queue.Count);
        
        Assert.True(queue.TryDequeue(out var dequeued1));
        Assert.True(queue.TryDequeue(out var dequeued2));

        Assert.Same(frame2, dequeued1);
        Assert.Same(frame3, dequeued2);
    }

    [Fact]
    public void Clear_ShouldEmptyQueue()
    {
        // Arrange
        var format = new AudioFormat(16000, 1, 16);
        var queue = new BoundedAudioFrameQueue(5);
        queue.Enqueue(new AudioFrame(new byte[100], format, 100));
        
        // Act
        queue.Clear();

        // Assert
        Assert.Equal(0, queue.Count);
    }
}
