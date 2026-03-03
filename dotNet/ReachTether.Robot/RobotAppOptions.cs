using Microsoft.Extensions.Configuration;
using OpenAI.Audio;

internal sealed class RobotAppOptions
{
    private enum VoicePipelineMode
    {
        Auto,
        Legacy,
        Realtime
    }

    public sealed class VadSettings
    {
        public int PreRollMs { get; init; } = 300;
        public int StartTriggerFrames { get; init; } = 3;
        public int EndSilenceMs { get; init; } = 700;
        public int MaxUtteranceMs { get; init; } = 8000;
        public int ListenTimeoutMs { get; init; } = 20000;
        public double MinRms { get; init; } = 0.012;
        public double InitialNoiseFloorRms { get; init; } = 0.006;
        public double NoiseFloorAdaptation { get; init; } = 0.05;
        public double NoiseMultiplier { get; init; } = 2.2;
        public double MaxNoiseFloorRms { get; init; } = 0.03;
    }

    public sealed class RealtimeSettings
    {
        public string Model { get; init; } = "gpt-realtime-mini";
        public int ResponseTimeoutMs { get; init; } = 45000;
        public int OutputSampleRateHz { get; init; } = 24000;
    }

    public sealed class PersonalitySettings
    {
        public string CatalogPath { get; init; } = "personalities.json";
        public string Default { get; init; } = "default";
    }

    public string VoicePipeline { get; init; } = "auto";
    public string ChatModel { get; init; } = "gpt-realtime-mini";
    public string TranscriptionModel { get; init; } = "whisper-1";
    public string SpeechModel { get; init; } = "gpt-4o-mini-tts";
    public GeneratedSpeechVoice SpeechVoice { get; init; } = GeneratedSpeechVoice.Alloy;
    public string TranscriptionLanguage { get; init; } = "en";
    public RealtimeSettings Realtime { get; init; } = new();
    public PersonalitySettings Personality { get; init; } = new();
    public VadSettings Vad { get; init; } = new();
    public string ReachyBaseUrl { get; init; } = "http://localhost:8080";
    public string CaptureDevice { get; init; } = "reachymini_audio_src";
    public string PlaybackDevice { get; init; } = "reachymini_audio_sink";
    public int AudioChannels { get; init; } = 2;
    public bool UseRealtimeVoicePipeline => ResolveVoicePipelineMode(VoicePipeline, ChatModel) == VoicePipelineMode.Realtime;
    public string RealtimeModel => string.IsNullOrWhiteSpace(Realtime.Model) ? ChatModel : Realtime.Model;

    public static RobotAppOptions FromConfiguration(IConfiguration configuration)
    {
        var vad = configuration.GetSection("VAD");
        var realtime = configuration.GetSection("OpenAI:Realtime");
        var personality = configuration.GetSection("Personality");
        var chatModel = configuration["OpenAI:ChatModel"] ?? "gpt-realtime-mini";

        return new RobotAppOptions
        {
            VoicePipeline = configuration["OpenAI:VoicePipeline"] ?? "auto",
            ChatModel = chatModel,
            TranscriptionModel = configuration["OpenAI:TranscriptionModel"] ?? "whisper-1",
            SpeechModel = configuration["OpenAI:SpeechModel"] ?? "gpt-4o-mini-tts",
            SpeechVoice = ParseSpeechVoice(configuration["OpenAI:SpeechVoice"] ?? "alloy"),
            TranscriptionLanguage = configuration["OpenAI:TranscriptionLanguage"] ?? "en",
            Realtime = new RealtimeSettings
            {
                Model = realtime["Model"] ?? chatModel,
                ResponseTimeoutMs = Math.Clamp(realtime.GetValue("ResponseTimeoutMs", 45000), 5000, 120000),
                OutputSampleRateHz = Math.Clamp(realtime.GetValue("OutputSampleRateHz", 24000), 8000, 48000)
            },
            Personality = new PersonalitySettings
            {
                CatalogPath = personality["CatalogPath"] ?? "personalities.json",
                Default = personality["Default"] ?? "default"
            },
            Vad = new VadSettings
            {
                PreRollMs = Math.Max(0, vad.GetValue("PreRollMs", 300)),
                StartTriggerFrames = Math.Max(1, vad.GetValue("StartTriggerFrames", 3)),
                EndSilenceMs = Math.Max(100, vad.GetValue("EndSilenceMs", 700)),
                MaxUtteranceMs = Math.Max(1000, vad.GetValue("MaxUtteranceMs", 8000)),
                ListenTimeoutMs = Math.Max(1000, vad.GetValue("ListenTimeoutMs", 20000)),
                MinRms = Clamp(vad.GetValue("MinRms", 0.012), 0.001, 0.25),
                InitialNoiseFloorRms = Clamp(vad.GetValue("InitialNoiseFloorRms", 0.006), 0.0001, 0.2),
                NoiseFloorAdaptation = Clamp(vad.GetValue("NoiseFloorAdaptation", 0.05), 0.001, 0.5),
                NoiseMultiplier = Clamp(vad.GetValue("NoiseMultiplier", 2.2), 1.0, 10.0),
                MaxNoiseFloorRms = Clamp(vad.GetValue("MaxNoiseFloorRms", 0.03), 0.001, 0.5)
            },
            ReachyBaseUrl = configuration["ReachyMini:BaseUrl"] ?? "http://localhost:8080",
            CaptureDevice = configuration["Audio:CaptureDevice"] ?? "reachymini_audio_src",
            PlaybackDevice = configuration["Audio:PlaybackDevice"] ?? "reachymini_audio_sink",
            AudioChannels = Math.Clamp(configuration.GetValue("Audio:Channels", 2), 1, 2)
        };
    }

    private static double Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return min;
        }

        return Math.Clamp(value, min, max);
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

    private static VoicePipelineMode ResolveVoicePipelineMode(string configuredMode, string chatModel)
    {
        var normalizedMode = configuredMode.Trim().ToLowerInvariant();
        return normalizedMode switch
        {
            "realtime" => VoicePipelineMode.Realtime,
            "legacy" or "turnbased" or "turn-based" or "classic" => VoicePipelineMode.Legacy,
            _ => IsRealtimeModel(chatModel) ? VoicePipelineMode.Realtime : VoicePipelineMode.Legacy
        };
    }

    private static bool IsRealtimeModel(string model)
    {
        return model.Contains("realtime", StringComparison.OrdinalIgnoreCase);
    }
}
