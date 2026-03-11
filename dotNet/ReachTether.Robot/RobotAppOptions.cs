using Microsoft.Extensions.Configuration;
using OpenAI.Audio;

internal sealed class RobotAppOptions
{
    private static readonly string[] DefaultBenignRealtimeErrorCodes =
    [
        "input_audio_buffer_commit_empty",
        "conversation_already_has_active_response"
    ];

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
        public int InputSampleRateHz { get; init; } = 24000;
        public int OutputSampleRateHz { get; init; } = 24000;
        public int SpeechStopMicDisableGraceMs { get; init; } = 300;
        public string InputChannelStrategy { get; init; } = "channel0";
        public bool RequireTranscriptBeforeAssistantAudio { get; init; }
        public bool LogAverageDownmixInputLevels { get; init; }
        public string[] BenignErrorCodes { get; init; } = [.. DefaultBenignRealtimeErrorCodes];
    }

    public sealed class PersonalitySettings
    {
        public string CatalogPath { get; init; } = "personalities.json";
        public string Default { get; init; } = "default";
    }

    public sealed class MotionSettings
    {
        public bool Enabled { get; init; } = true;
        public int LoopHz { get; init; } = 50;
        public int MetricsIntervalSeconds { get; init; } = 10;
        public double CommandThresholdMm { get; init; } = 0.25;
        public double CommandThresholdDeg { get; init; } = 0.35;
        public double MaxTranslationMm { get; init; } = 15.0;
        public double MaxRotationDeg { get; init; } = 20.0;
        public int TalkingSilenceReleaseMs { get; init; } = 150;
        public double TalkingDecaySeconds { get; init; } = 0.25;
    }

    public sealed class VisionSettings
    {
        public bool WarmupOnStartup { get; init; } = true;
        public int WarmupDelayMs { get; init; } = 500;
        public bool ProbeOnStartup { get; init; }
        public bool ProbeOnly { get; init; }
        public int ProbeCaptureCount { get; init; } = 1;
        public int ProbeDelayMs { get; init; } = 1000;
        public string SourceKind { get; init; } = "unix-socket";
        public string SourcePath { get; init; } = "/tmp/reachymini_camera_socket";
        public int Width { get; init; } = 1280;
        public int Height { get; init; } = 720;
        public int Framerate { get; init; } = 30;
        public int CaptureTimeoutSeconds { get; init; } = 20;
    }

    public string VoicePipeline { get; init; } = "auto";
    public string ChatModel { get; init; } = "gpt-realtime-mini";
    public string ChatFallbackModel { get; init; } = "gpt-4o-mini";
    public string TranscriptionModel { get; init; } = "whisper-1";
    public string SpeechModel { get; init; } = "gpt-4o-mini-tts";
    public GeneratedSpeechVoice SpeechVoice { get; init; } = GeneratedSpeechVoice.Alloy;
    public string TranscriptionLanguage { get; init; } = "en";
    public RealtimeSettings Realtime { get; init; } = new();
    public PersonalitySettings Personality { get; init; } = new();
    public MotionSettings Motion { get; init; } = new();
    public VisionSettings Vision { get; init; } = new();
    public VadSettings Vad { get; init; } = new();
    public string ReachyBaseUrl { get; init; } = "http://localhost:8080";
    public string CaptureDevice { get; init; } = "reachymini_audio_src";
    public string PlaybackDevice { get; init; } = "reachymini_audio_sink";
    public int AudioSampleRateHz { get; init; } = 16000;
    public int AudioChannels { get; init; } = 2;
    public bool UseRealtimeVoicePipeline => ResolveVoicePipelineMode(VoicePipeline, ChatModel) == VoicePipelineMode.Realtime;
    public string RealtimeModel => string.IsNullOrWhiteSpace(Realtime.Model) ? ChatModel : Realtime.Model;

    public static RobotAppOptions FromConfiguration(IConfiguration configuration)
    {
        var vad = configuration.GetSection("VAD");
        var realtime = configuration.GetSection("OpenAI:Realtime");
        var personality = configuration.GetSection("Personality");
        var motion = configuration.GetSection("Motion");
        var vision = configuration.GetSection("Vision");
        var chatModel = configuration["OpenAI:ChatModel"] ?? "gpt-realtime-mini";

        return new RobotAppOptions
        {
            VoicePipeline = configuration["OpenAI:VoicePipeline"] ?? "auto",
            ChatModel = chatModel,
            ChatFallbackModel = configuration["OpenAI:FallbackChatModel"] ?? "gpt-4o-mini",
            TranscriptionModel = configuration["OpenAI:TranscriptionModel"] ?? "whisper-1",
            SpeechModel = configuration["OpenAI:SpeechModel"] ?? "gpt-4o-mini-tts",
            SpeechVoice = ParseSpeechVoice(configuration["OpenAI:SpeechVoice"] ?? "alloy"),
            TranscriptionLanguage = configuration["OpenAI:TranscriptionLanguage"] ?? "en",
            Realtime = new RealtimeSettings
            {
                Model = realtime["Model"] ?? chatModel,
                ResponseTimeoutMs = Math.Clamp(realtime.GetValue("ResponseTimeoutMs", 45000), 5000, 120000),
                InputSampleRateHz = Math.Clamp(realtime.GetValue("InputSampleRateHz", 24000), 8000, 48000),
                OutputSampleRateHz = Math.Clamp(realtime.GetValue("OutputSampleRateHz", 24000), 8000, 48000),
                SpeechStopMicDisableGraceMs = Math.Clamp(realtime.GetValue("SpeechStopMicDisableGraceMs", 300), 0, 2000),
                InputChannelStrategy = realtime["InputChannelStrategy"] ?? "channel0",
                RequireTranscriptBeforeAssistantAudio = realtime.GetValue("RequireTranscriptBeforeAssistantAudio", false),
                LogAverageDownmixInputLevels = realtime.GetValue("LogAverageDownmixInputLevels", false),
                BenignErrorCodes = ParseBenignRealtimeErrorCodes(realtime.GetSection("BenignErrorCodes").Get<string[]>())
            },
            Personality = new PersonalitySettings
            {
                CatalogPath = personality["CatalogPath"] ?? "personalities.json",
                Default = personality["Default"] ?? "default"
            },
            Motion = new MotionSettings
            {
                Enabled = motion.GetValue("Enabled", true),
                LoopHz = Math.Clamp(motion.GetValue("LoopHz", 50), 10, 100),
                MetricsIntervalSeconds = Math.Max(1, motion.GetValue("MetricsIntervalSeconds", 10)),
                CommandThresholdMm = Clamp(motion.GetValue("CommandThresholdMm", 0.25), 0.01, 10.0),
                CommandThresholdDeg = Clamp(motion.GetValue("CommandThresholdDeg", 0.35), 0.05, 10.0),
                MaxTranslationMm = Clamp(motion.GetValue("MaxTranslationMm", 15.0), 1.0, 80.0),
                MaxRotationDeg = Clamp(motion.GetValue("MaxRotationDeg", 20.0), 1.0, 80.0),
                TalkingSilenceReleaseMs = Math.Max(50, motion.GetValue("TalkingSilenceReleaseMs", 150)),
                TalkingDecaySeconds = Clamp(motion.GetValue("TalkingDecaySeconds", 0.25), 0.05, 2.0)
            },
            Vision = new VisionSettings
            {
                WarmupOnStartup = vision.GetValue("WarmupOnStartup", true),
                WarmupDelayMs = Math.Clamp(vision.GetValue("WarmupDelayMs", 500), 0, 10000),
                ProbeOnStartup = vision.GetValue("ProbeOnStartup", false),
                ProbeOnly = vision.GetValue("ProbeOnly", false),
                ProbeCaptureCount = Math.Clamp(vision.GetValue("ProbeCaptureCount", 1), 1, 20),
                ProbeDelayMs = Math.Clamp(vision.GetValue("ProbeDelayMs", 1000), 0, 60000),
                SourceKind = vision["SourceKind"] ?? "unix-socket",
                SourcePath = vision["SourcePath"] ?? "/tmp/reachymini_camera_socket",
                Width = Math.Clamp(vision.GetValue("Width", 1280), 16, 8192),
                Height = Math.Clamp(vision.GetValue("Height", 720), 16, 8192),
                Framerate = Math.Clamp(vision.GetValue("Framerate", 30), 1, 120),
                CaptureTimeoutSeconds = Math.Clamp(vision.GetValue("CaptureTimeoutSeconds", 20), 1, 120)
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
            AudioSampleRateHz = Math.Clamp(configuration.GetValue("Audio:SampleRateHz", 16000), 8000, 48000),
            AudioChannels = Math.Clamp(configuration.GetValue("Audio:Channels", 2), 1, 2)
        };
    }

    private static string[] ParseBenignRealtimeErrorCodes(string[]? configuredCodes)
    {
        var source = configuredCodes is { Length: > 0 }
            ? configuredCodes
            : DefaultBenignRealtimeErrorCodes;

        var normalized = source
            .Where(static code => !string.IsNullOrWhiteSpace(code))
            .Select(static code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length > 0
            ? normalized
            : [.. DefaultBenignRealtimeErrorCodes];
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
