using System.Text;

internal sealed class RealtimeTurnState
{
    public StringBuilder AssistantText { get; } = new();
    public string? UserTranscript { get; set; }
    public string? ActiveResponseId { get; set; }
    public bool SpeechStarted { get; set; }
    public bool SpeechStopped { get; set; }
    public bool ResponseStarted { get; set; }
    public bool StreamOpen { get; set; }
    public bool StreamFinalized { get; set; }
    public bool StreamedAudioPlayback { get; set; }
    public bool DropActiveResponseAudio { get; set; }
    public bool SuppressResponseForShutdownIntent { get; set; }
    public string? TranscriptionFailureReason { get; set; }
    public TimeSpan? SpeechStartTime { get; set; }
    public TimeSpan? SpeechEndTime { get; set; }
    public DateTime ResponseDeadlineUtc { get; set; } = DateTime.MaxValue;
    public int SendAudioEnabled = 1;
    public RealtimeTurnResult? CompletedResult { get; private set; }

    public bool IsCompleted => CompletedResult is not null;

    public void Complete(RealtimeTurnResult result)
    {
        CompletedResult ??= result;
    }
}
