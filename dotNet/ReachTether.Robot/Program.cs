using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Audio;
using ReachTether.Audio.Alsa;
using ReachyMini.Sdk;
using System.Net.Http.Headers;

LoadDotEnvIfPresent();

using var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((_, config) =>
    {
        config.Sources.Clear();
        config.SetBasePath(Directory.GetCurrentDirectory());
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);
    })
    .ConfigureLogging((context, logging) =>
    {
        var appOptions = RobotAppOptions.FromConfiguration(context.Configuration);
        if (appOptions.FileLogging.Enabled)
        {
            logging.AddProvider(new RollingFileLoggerProvider(appOptions.FileLogging));
        }

        logging.AddFilter<RollingFileLoggerProvider>(null, ParseLogLevel(appOptions.FileLogging.MinimumLevel));
        logging.AddFilter<RollingFileLoggerProvider>(
            "System.Net.Http.HttpClient.ReachyMiniClient.LogicalHandler",
            LogLevel.Warning);
        logging.AddFilter<RollingFileLoggerProvider>(
            "System.Net.Http.HttpClient.ReachyMiniClient.ClientHandler",
            LogLevel.Warning);
    })
    .ConfigureServices((context, services) =>
    {
        var appOptions = RobotAppOptions.FromConfiguration(context.Configuration);
        Console.WriteLine(
            $"[Startup] UseRealtimeVoicePipeline={appOptions.UseRealtimeVoicePipeline} " +
            $"(VoicePipeline='{appOptions.VoicePipeline}', ChatModel='{appOptions.ChatModel}', ChatFallbackModel='{appOptions.ChatFallbackModel}', RealtimeModel='{appOptions.RealtimeModel}', " +
            $"PersonalityDefault='{appOptions.Personality.Default}', PersonalityCatalog='{appOptions.Personality.CatalogPath}')");
        services.AddSingleton(appOptions);
        services.AddSingleton<IPersonalityCatalog>(sp =>
            PersonalityCatalog.Load(
                appOptions.Personality.CatalogPath,
                appOptions.Personality.Default,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PersonalityCatalog>>()));

        var openAIApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!appOptions.Vision.ProbeOnly && string.IsNullOrWhiteSpace(openAIApiKey))
        {
            throw new Exception("OPENAI_API_KEY not found in .env file or environment variables.");
        }

        services.AddReachyMiniClient(options =>
        {
            options.BaseUrl = appOptions.ReachyBaseUrl;
            options.Timeout = TimeSpan.FromSeconds(30);
            options.CameraSourceKind = appOptions.Vision.SourceKind;
            options.CameraSourcePath = appOptions.Vision.SourcePath;
            options.CameraWidth = appOptions.Vision.Width;
            options.CameraHeight = appOptions.Vision.Height;
            options.CameraFramerate = appOptions.Vision.Framerate;
            options.CameraCaptureTimeoutSeconds = appOptions.Vision.CaptureTimeoutSeconds;
        });

        services.AddSingleton<VisionStartupProbe>();

        services.AddSingleton(new OpenAIClient(openAIApiKey!));
        services.AddSingleton<IRealtimeVoiceSessionFactory>(
            new OpenAiRealtimeVoiceSessionFactory(openAIApiKey!, appOptions.RealtimeModel));
        services.AddSingleton(sp => new AudioClients(
            sp.GetRequiredService<OpenAIClient>().GetAudioClient(appOptions.TranscriptionModel),
            sp.GetRequiredService<OpenAIClient>().GetAudioClient(appOptions.SpeechModel)));

        const string OpenAiResponsesHttpClientName = "OpenAI.Responses";
        const string ReachTetherServerHttpClientName = "ReachTether.Server";

        services.AddHttpClient(OpenAiResponsesHttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.openai.com/v1/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", openAIApiKey);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });
        services.AddHttpClient(ReachTetherServerHttpClientName);
        services.AddSingleton(sp => new OpenAiResponsesClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(OpenAiResponsesHttpClientName)));
        services.AddHttpClient<IReachTetherServerClient, ReachTetherServerClient>(ReachTetherServerHttpClientName, client =>
        {
            client.BaseAddress = new Uri(appOptions.Server.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(appOptions.Server.TimeoutSeconds);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        services.AddSingleton(sp => new LocalAudioSession(new LocalAudioOptions
        {
            CaptureDevice = appOptions.CaptureDevice,
            PlaybackDevice = appOptions.PlaybackDevice,
            SampleRate = (uint)appOptions.AudioSampleRateHz,
            Channels = (uint)appOptions.AudioChannels
        }));
        services.AddSingleton<IRealtimeAudioOutput, LocalRealtimeAudioOutput>();

        services.AddSingleton<IOpenAiTransport, OpenAiTransport>();
        services.AddSingleton<IPromptContextBuilder, PromptContextBuilder>();
        services.AddSingleton<IServerSessionCoordinator, ServerSessionCoordinator>();
        services.AddSingleton<IInteractionStateMachine, InteractionStateMachine>();
        services.AddSingleton<ICameraSnapshotProvider, CameraSnapshotService>();
        services.AddSingleton<CameraTool>();
        services.AddSingleton<IToolArtifactPublisher, ServerArtifactPublisher>();
        services.AddSingleton<IToolRegistration>(sp => sp.GetRequiredService<CameraTool>());
        services.AddSingleton<IToolRegistration>(sp => new RemoteToolRegistration(
            "scheduler",
            "Create or schedule a reminder or meeting task on the server.",
            RemoteToolSchemas.Scheduler,
            static options => options.Tools.EnableRemoteTools && options.Server.Enabled,
            sp.GetRequiredService<IReachTetherServerClient>(),
            sp.GetRequiredService<RobotAppOptions>()));
        services.AddSingleton<IToolRegistration>(sp => new RemoteToolRegistration(
            "kinect_shot",
            "Capture an image from a server-attached camera for visual questions.",
            RemoteToolSchemas.KinectShot,
            static options => options.Tools.EnableRemoteTools && options.Server.Enabled,
            sp.GetRequiredService<IReachTetherServerClient>(),
            sp.GetRequiredService<RobotAppOptions>()));
        services.AddSingleton<IToolRegistration>(sp => new RemoteToolRegistration(
            "smarty_mode",
            "Ask a slower, smarter server-side model for help with complex reasoning.",
            RemoteToolSchemas.SmartyMode,
            static options => options.Tools.EnableRemoteTools && options.Server.Enabled,
            sp.GetRequiredService<IReachTetherServerClient>(),
            sp.GetRequiredService<RobotAppOptions>()));
        services.AddSingleton<IToolRegistration>(sp => new RemoteToolRegistration(
            "memory_query",
            "Query durable session memory and compact knowledge snippets from the server.",
            RemoteToolSchemas.MemoryQuery,
            static options => options.Tools.EnableRemoteTools && options.Server.Enabled,
            sp.GetRequiredService<IReachTetherServerClient>(),
            sp.GetRequiredService<RobotAppOptions>()));
        services.AddSingleton<IToolRouter, ToolRouter>();
        services.AddSingleton<MotionOrchestrator>();
        services.AddSingleton<IMotionOrchestrator>(sp => sp.GetRequiredService<MotionOrchestrator>());
        services.AddSingleton<IAudioCapturePipeline, AudioCaptureService>();
        services.AddSingleton<IAudioPlaybackPipeline, AudioPlaybackService>();

        services.AddHostedService(sp => sp.GetRequiredService<MotionOrchestrator>());
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

static LogLevel ParseLogLevel(string? value)
{
    return Enum.TryParse<LogLevel>(value, ignoreCase: true, out var parsed)
        ? parsed
        : LogLevel.Debug;
}

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
