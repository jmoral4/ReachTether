using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using OpenAI.RealtimeConversation;
using ReachTether.Audio.Alsa;
using ReachyMini.Sdk;
using ReachyMini.Sdk.Configuration;

LoadDotEnvIfPresent();

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((_, config) =>
    {
        config.Sources.Clear();
        config.SetBasePath(Directory.GetCurrentDirectory());
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);
    })
    .ConfigureServices((context, services) =>
    {
        var appOptions = RobotAppOptions.FromConfiguration(context.Configuration);
        services.AddSingleton(appOptions);

        var openAIApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new Exception("OPENAI_API_KEY not found in .env file or environment variables.");

        services.AddSingleton(new OpenAIClient(openAIApiKey));
        services.AddSingleton(sp => sp.GetRequiredService<OpenAIClient>().GetChatClient(appOptions.ChatModel));
        services.AddSingleton(sp => sp.GetRequiredService<OpenAIClient>().GetRealtimeConversationClient(appOptions.RealtimeModel));
        services.AddSingleton(sp => new AudioClients(
            sp.GetRequiredService<OpenAIClient>().GetAudioClient(appOptions.TranscriptionModel),
            sp.GetRequiredService<OpenAIClient>().GetAudioClient(appOptions.SpeechModel)));

        services.AddSingleton(new HttpClient());
        services.AddSingleton(sp =>
        {
            var options = Microsoft.Extensions.Options.Options.Create(new ReachyMiniOptions
            {
                BaseUrl = appOptions.ReachyBaseUrl,
                Timeout = TimeSpan.FromSeconds(30)
            });

            return new ReachyMiniClient(sp.GetRequiredService<HttpClient>(), options);
        });

        services.AddSingleton(sp => new LocalAudioSession(new LocalAudioOptions
        {
            CaptureDevice = appOptions.CaptureDevice,
            PlaybackDevice = appOptions.PlaybackDevice,
            SampleRate = 16000,
            Channels = (uint)appOptions.AudioChannels
        }));

        services.AddSingleton<IOpenAiTransport, OpenAiTransport>();
        services.AddSingleton<IInteractionStateMachine, InteractionStateMachine>();
        services.AddSingleton<IAudioCapturePipeline, AudioCaptureService>();
        services.AddSingleton<IAudioPlaybackPipeline, AudioPlaybackService>();

        services.AddHostedService(sp => (AudioCaptureService)sp.GetRequiredService<IAudioCapturePipeline>());
        services.AddHostedService(sp => (AudioPlaybackService)sp.GetRequiredService<IAudioPlaybackPipeline>());

        if (appOptions.UseRealtimeVoicePipeline)
        {
            services.AddHostedService<RealtimeInteractionOrchestrator>();
        }
        else
        {
            services.AddHostedService<InteractionOrchestrator>();
        }
    })
    .Build();

await host.RunAsync();

static void LoadDotEnvIfPresent()
{
    var candidates = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), ".env"),
        Path.Combine(AppContext.BaseDirectory, ".env")
    };

    foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (!File.Exists(path))
        {
            continue;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            var value = line[(separatorIndex + 1)..].Trim();
            value = value.Trim('"').Trim('\'');

            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        break;
    }
}
