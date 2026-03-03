using Microsoft.Extensions.Configuration;
using OpenAI.Audio;

internal sealed class RobotAppOptions
{
    public string ChatModel { get; init; } = "gpt-4o-mini";
    public string TranscriptionModel { get; init; } = "whisper-1";
    public string SpeechModel { get; init; } = "gpt-4o-mini-tts";
    public GeneratedSpeechVoice SpeechVoice { get; init; } = GeneratedSpeechVoice.Alloy;
    public string TranscriptionLanguage { get; init; } = "en";
    public int RecordingSeconds { get; init; } = 5;
    public string ReachyBaseUrl { get; init; } = "http://localhost:8080";
    public string CaptureDevice { get; init; } = "reachymini_audio_src";
    public string PlaybackDevice { get; init; } = "reachymini_audio_sink";

    public static RobotAppOptions FromConfiguration(IConfiguration configuration)
    {
        return new RobotAppOptions
        {
            ChatModel = configuration["OpenAI:ChatModel"] ?? "gpt-4o-mini",
            TranscriptionModel = configuration["OpenAI:TranscriptionModel"] ?? "whisper-1",
            SpeechModel = configuration["OpenAI:SpeechModel"] ?? "gpt-4o-mini-tts",
            SpeechVoice = ParseSpeechVoice(configuration["OpenAI:SpeechVoice"] ?? "alloy"),
            TranscriptionLanguage = configuration["OpenAI:TranscriptionLanguage"] ?? "en",
            RecordingSeconds = Math.Max(1, configuration.GetValue("OpenAI:RecordingSeconds", 5)),
            ReachyBaseUrl = configuration["ReachyMini:BaseUrl"] ?? "http://localhost:8080",
            CaptureDevice = configuration["Audio:CaptureDevice"] ?? "reachymini_audio_src",
            PlaybackDevice = configuration["Audio:PlaybackDevice"] ?? "reachymini_audio_sink"
        };
    }

    private static GeneratedSpeechVoice ParseSpeechVoice(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "alloy" => GeneratedSpeechVoice.Alloy,
            "echo" => GeneratedSpeechVoice.Echo,
            "fable" => GeneratedSpeechVoice.Fable,
            "onyx" => GeneratedSpeechVoice.Onyx,
            "nova" => GeneratedSpeechVoice.Nova,
            "shimmer" => GeneratedSpeechVoice.Shimmer,
            _ => GeneratedSpeechVoice.Alloy
        };
    }
}
