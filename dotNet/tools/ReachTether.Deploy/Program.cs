using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

return await DeployApp.RunAsync(args);

internal static class DeployApp
{
    public static async Task<int> RunAsync(string[] args)
    {
        var parseResult = DeployOptions.Parse(args);
        if (parseResult.Error is not null)
        {
            Console.Error.WriteLine($"Error: {parseResult.Error}");
            Console.Error.WriteLine();
            HelpText.Write();
            return 2;
        }

        if (parseResult.ShowHelp)
        {
            HelpText.Write();
            return 0;
        }

        var repoRoot = RepoLocator.FindRoot();
        if (repoRoot is null)
        {
            Console.Error.WriteLine("Could not find the ReachTether repository root (dotNet/ReachTether.slnx). ");
            Console.Error.WriteLine("Run this command from inside the ReachTether checkout.");
            return 2;
        }

        var options = parseResult.Options!.ResolvePaths(repoRoot);
        var ui = new ConsoleUi(options.UseColor);
        ui.WriteBanner();
        ui.WritePlan(options);

        var runId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var logFile = LogFile.Create(repoRoot, runId);
        var logPath = logFile.Path;

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        await using var log = logFile.Writer;
        var runner = new ProcessRunner(repoRoot, log, ui, options.Verbose, options.DryRun);
        var stages = CreateStages(repoRoot, runId, options, runner);
        var results = new List<StageResult>();

        foreach (var stage in stages)
        {
            if (!stage.Enabled)
            {
                results.Add(StageResult.Skipped(stage.Name));
                ui.WriteSkipped(stage.Name, stage.SkipReason);
                continue;
            }

            var result = await RunStageAsync(stage, ui, log, cancellation.Token);
            results.Add(result);
            if (!result.Succeeded)
            {
                ui.WriteFailureSummary(result, logPath);
                return result.Cancelled ? 130 : 1;
            }
        }

        ui.WriteSuccessSummary(results, logPath, options.DryRun);
        return 0;
    }

    private static IReadOnlyList<PipelineStage> CreateStages(
        string repoRoot,
        string runId,
        DeployOptions options,
        ProcessRunner runner)
    {
        var solutionPath = Path.Combine(repoRoot, "dotNet", "ReachTether.slnx");
        var robotProject = Path.Combine(repoRoot, "dotNet", "ReachTether.Robot", "ReachTether.Robot.csproj");
        var testProjects = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "dotNet"), "*.Tests.csproj", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var testResultsDirectory = Path.Combine(Path.GetTempPath(), "reachtether-deploy", runId);
        var remote = $"{options.RemoteUser}@{options.RemoteHost}";

        return
        [
            new PipelineStage(
                "Preflight",
                true,
                string.Empty,
                async cancellationToken =>
                {
                    var required = new List<string>();
                    if (options.Build || options.Test || options.Publish)
                    {
                        required.Add("dotnet");
                    }

                    if (options.Deploy)
                    {
                        required.Add("scp");
                    }

                    if (options.Run)
                    {
                        required.Add("ssh");
                    }

                    var missing = required.Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(command => ExecutableLocator.Find(command) is null)
                        .ToArray();
                    if (missing.Length > 0)
                    {
                        return StageExecution.Fail($"Missing from PATH: {string.Join(", ", missing)}");
                    }

                    if (options.Deploy && !options.Publish && !File.Exists(Path.Combine(options.OutputDirectory, "ReachTether.Robot.dll")))
                    {
                        return StageExecution.Fail($"No published bundle found at {options.OutputDirectory}");
                    }

                    await Task.CompletedTask;
                    return StageExecution.Ok($"tools ready; target {remote}");
                }),
            new PipelineStage(
                "Build",
                options.Build,
                "disabled by command-line option",
                cancellationToken => runner.RunAsync(
                    "dotnet",
                    BuildArguments.ForBuild(solutionPath, options.Configuration, options.NoRestore),
                    cancellationToken,
                    successSummary: "solution built")),
            new PipelineStage(
                "Tests",
                options.Test,
                "disabled by command-line option",
                async cancellationToken =>
                {
                    if (testProjects.Length == 0)
                    {
                        return StageExecution.Ok("no test projects found");
                    }

                    if (options.DryRun)
                    {
                        foreach (var project in testProjects)
                        {
                            await runner.RunAsync(
                                "dotnet",
                                ["test", project, "-c", options.Configuration, "--no-build", "--no-restore", "--logger", "trx", "--results-directory", testResultsDirectory],
                                cancellationToken);
                        }

                        return StageExecution.Ok($"would run {testProjects.Length} test project(s)");
                    }

                    if (Directory.Exists(testResultsDirectory))
                    {
                        Directory.Delete(testResultsDirectory, recursive: true);
                    }

                    Directory.CreateDirectory(testResultsDirectory);
                    foreach (var project in testProjects)
                    {
                        var resultFileCountBefore = CountTrxFiles(testResultsDirectory);
                        var processResult = await runner.RunAsync(
                            "dotnet",
                            ["test", project, "-c", options.Configuration, "--no-build", "--no-restore", "--logger", "trx", "--results-directory", testResultsDirectory],
                            cancellationToken);
                        if (processResult.ExitCode != 0)
                        {
                            var failedCounts = TestResultReader.Read(testResultsDirectory);
                            return StageExecution.Fail(failedCounts.HasResults
                                ? failedCounts.ToDisplayString()
                                : $"{Path.GetFileNameWithoutExtension(project)} failed", processResult.OutputTail);
                        }

                        if (CountTrxFiles(testResultsDirectory) == resultFileCountBefore)
                        {
                            var testAssembly = FindTestAssembly(project, options.Configuration);
                            if (testAssembly is null)
                            {
                                return StageExecution.Fail($"{Path.GetFileNameWithoutExtension(project)} produced no test results and its built test assembly was not found");
                            }

                            var vstestResult = await runner.RunAsync(
                                "dotnet",
                                ["vstest", testAssembly, "--Logger:trx", $"--ResultsDirectory:{testResultsDirectory}"],
                                cancellationToken);
                            if (vstestResult.ExitCode != 0)
                            {
                                var failedCounts = TestResultReader.Read(testResultsDirectory);
                                return StageExecution.Fail(failedCounts.HasResults
                                    ? failedCounts.ToDisplayString()
                                    : $"{Path.GetFileNameWithoutExtension(project)} failed", vstestResult.OutputTail);
                            }
                        }
                    }

                    var counts = TestResultReader.Read(testResultsDirectory);
                    return counts.HasResults
                        ? StageExecution.Ok(counts.ToDisplayString())
                        : StageExecution.Fail("test commands completed without producing verifiable results");
                }),
            new PipelineStage(
                "Publish linux-arm64",
                options.Publish,
                "disabled by command-line option",
                async cancellationToken =>
                {
                    var result = await runner.RunAsync(
                        "dotnet",
                        BuildArguments.ForPublish(robotProject, options.Configuration, options.OutputDirectory, options.NoRestore),
                        cancellationToken);
                    if (result.ExitCode != 0)
                    {
                        return result;
                    }

                    if (!options.DryRun && !File.Exists(Path.Combine(options.OutputDirectory, "ReachTether.Robot.dll")))
                    {
                        return StageExecution.Fail("publish finished but ReachTether.Robot.dll is missing");
                    }

                    return StageExecution.Ok(options.DryRun
                        ? $"would publish to {options.OutputDirectory}"
                        : $"bundle ready at {options.OutputDirectory}");
                }),
            new PipelineStage(
                "Deploy",
                options.Deploy,
                "local/publish-only mode",
                cancellationToken => runner.RunAsync(
                    "scp",
                    ["-r", Path.Combine(options.OutputDirectory, "."), $"{remote}:{EnsureTrailingSlash(options.RemoteDirectory)}"],
                    cancellationToken,
                    successSummary: $"copied to {remote}:{options.RemoteDirectory}",
                    attachToConsole: true)),
            new PipelineStage(
                "Run on robot",
                options.Run,
                "use --run to start it",
                cancellationToken => runner.RunAsync(
                    "ssh",
                    ["-t", remote, $"cd {ShellQuote(options.RemoteDirectory)} && exec dotnet ReachTether.Robot.dll"],
                    cancellationToken,
                    successSummary: "remote process exited cleanly",
                    attachToConsole: true))
        ];
    }

    private static async Task<StageResult> RunStageAsync(
        PipelineStage stage,
        ConsoleUi ui,
        StreamWriter log,
        CancellationToken cancellationToken)
    {
        ui.WriteStarted(stage.Name);
        await log.WriteLineAsync($"{Environment.NewLine}=== {stage.Name} ===");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var execution = await stage.Execute(cancellationToken);
            stopwatch.Stop();
            var result = new StageResult(stage.Name, execution.ExitCode == 0, false, execution.Summary, stopwatch.Elapsed, execution.OutputTail);
            if (result.Succeeded)
            {
                ui.WriteSucceeded(result);
            }
            else
            {
                ui.WriteFailed(result);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var result = new StageResult(stage.Name, false, true, "cancelled", stopwatch.Elapsed, []);
            ui.WriteFailed(result);
            return result;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            await log.WriteLineAsync(exception.ToString());
            var result = new StageResult(stage.Name, false, false, exception.Message, stopwatch.Elapsed, [exception.Message]);
            ui.WriteFailed(result);
            return result;
        }
    }

    private static string EnsureTrailingSlash(string path) => path.EndsWith('/') ? path : $"{path}/";

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static int CountTrxFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.trx", SearchOption.AllDirectories).Count()
            : 0;

    private static string? FindTestAssembly(string projectPath, string configuration)
    {
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var outputRoot = Path.Combine(Path.GetDirectoryName(projectPath)!, "bin", configuration);
        if (!Directory.Exists(outputRoot))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(outputRoot, $"{projectName}.dll", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Contains("ref", StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}

internal static class LogFile
{
    public static (StreamWriter Writer, string Path) Create(string repoRoot, string runId)
    {
        var preferredDirectory = Path.Combine(repoRoot, "out", "deploy-logs");
        try
        {
            return CreateIn(preferredDirectory, runId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var fallbackDirectory = Path.Combine(Path.GetTempPath(), "reachtether-deploy-logs");
            Console.Error.WriteLine($"Note: could not open the repo log directory; using {fallbackDirectory}");
            try
            {
                return CreateIn(fallbackDirectory, runId);
            }
            catch (Exception fallbackException) when (fallbackException is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Note: logging is unavailable for this run.");
                return (new StreamWriter(Stream.Null) { AutoFlush = true }, "unavailable");
            }
        }
    }

    private static (StreamWriter Writer, string Path) CreateIn(string directory, string runId)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"deploy-{runId}.log");
        var writer = new StreamWriter(path, append: false, new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        return (writer, path);
    }
}

internal sealed record PipelineStage(
    string Name,
    bool Enabled,
    string SkipReason,
    Func<CancellationToken, Task<StageExecution>> Execute);

internal sealed record StageExecution(int ExitCode, string Summary, IReadOnlyList<string> OutputTail)
{
    public static StageExecution Ok(string summary) => new(0, summary, []);

    public static StageExecution Fail(string summary, IReadOnlyList<string>? outputTail = null) =>
        new(1, summary, outputTail ?? []);
}

internal sealed record StageResult(
    string Name,
    bool Succeeded,
    bool Cancelled,
    string Summary,
    TimeSpan Duration,
    IReadOnlyList<string> OutputTail)
{
    public static StageResult Skipped(string name) => new(name, true, false, "skipped", TimeSpan.Zero, []);
}

internal sealed class ProcessRunner(
    string workingDirectory,
    StreamWriter log,
    ConsoleUi ui,
    bool verbose,
    bool dryRun)
{
    public async Task<StageExecution> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? successSummary = null,
        bool attachToConsole = false)
    {
        var command = CommandFormatter.Format(fileName, arguments);
        await log.WriteLineAsync($"> {command}");
        if (dryRun)
        {
            ui.WriteCommand(command);
            return StageExecution.Ok(successSummary ?? "planned");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = !attachToConsole,
            RedirectStandardError = !attachToConsole,
            CreateNoWindow = !attachToConsole
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            await log.WriteLineAsync(exception.Message);
            return StageExecution.Fail($"could not start {fileName}: {exception.Message}", [exception.Message]);
        }

        if (attachToConsole)
        {
            await log.WriteLineAsync("[output attached to console and is not captured in this log]");
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                throw;
            }

            return process.ExitCode == 0
                ? StageExecution.Ok(successSummary ?? "completed")
                : StageExecution.Fail($"command exited with code {process.ExitCode}");
        }

        var output = new List<string>();
        var outputLock = new object();
        var showOutput = verbose;
        var stdout = PumpAsync(process.StandardOutput, output, outputLock, showOutput, cancellationToken);
        var stderr = PumpAsync(process.StandardError, output, outputLock, showOutput, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var tail = output.TakeLast(24).ToArray();
        return process.ExitCode == 0
            ? StageExecution.Ok(successSummary ?? "completed")
            : StageExecution.Fail($"command exited with code {process.ExitCode}", tail);
    }

    private async Task PumpAsync(
        StreamReader reader,
        List<string> output,
        object outputLock,
        bool showOutput,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lock (outputLock)
            {
                output.Add(line);
                log.WriteLine(line);
            }

            if (showOutput)
            {
                ui.WriteOutput(line);
            }
        }
    }
}

internal static class TestResultReader
{
    public static TestCounts Read(string directory)
    {
        var counts = new TestCounts();
        if (!Directory.Exists(directory))
        {
            return counts;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.trx", SearchOption.AllDirectories))
        {
            var counters = XDocument.Load(file)
                .Descendants()
                .LastOrDefault(element => element.Name.LocalName == "Counters");
            if (counters is null)
            {
                continue;
            }

            counts = counts.Add(
                ReadInt(counters, "total"),
                ReadInt(counters, "executed"),
                ReadInt(counters, "passed"),
                ReadInt(counters, "failed"));
        }

        return counts;
    }

    private static int ReadInt(XElement element, string attributeName) =>
        int.TryParse(element.Attribute(attributeName)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
}

internal readonly record struct TestCounts(int Total = 0, int Executed = 0, int Passed = 0, int Failed = 0)
{
    public bool HasResults => Total > 0 || Executed > 0;

    public int Skipped => Math.Max(0, Total - Executed);

    public TestCounts Add(int total, int executed, int passed, int failed) =>
        new(Total + total, Executed + executed, Passed + passed, Failed + failed);

    public string ToDisplayString() => $"{Passed} passed, {Failed} failed, {Skipped} skipped ({Total} total)";
}

internal sealed class ConsoleUi(bool useColor)
{
    private readonly bool useColor = useColor && !Console.IsOutputRedirected;

    public void WriteBanner()
    {
        SetColor(ConsoleColor.Cyan);
        Console.WriteLine("      .--------.");
        Console.WriteLine("     /  o    o  \\");
        Console.WriteLine("    |     __     |");
        Console.WriteLine("     \\  '=='  /");
        Console.WriteLine("   .--| REACH |--.");
        Console.WriteLine("  /   '------'   \\");
        Console.WriteLine("      /|      |\\");
        ResetColor();
        Console.WriteLine("      ReachTether Deploy");
        Console.WriteLine();
    }

    public void WritePlan(DeployOptions options)
    {
        Console.WriteLine($"Target  {options.RemoteUser}@{options.RemoteHost}:{options.RemoteDirectory}");
        Console.WriteLine($"Bundle  {options.OutputDirectory}");
        if (options.DryRun)
        {
            WriteColored("Mode    dry run (no commands will execute)", ConsoleColor.Yellow);
        }

        Console.WriteLine();
    }

    public void WriteStarted(string stage)
    {
        SetColor(ConsoleColor.DarkGray);
        Console.Write("[..]");
        ResetColor();
        Console.WriteLine($" {stage}");
    }

    public void WriteSucceeded(StageResult result)
    {
        SetColor(ConsoleColor.Green);
        Console.Write("[ok]");
        ResetColor();
        Console.WriteLine($" {result.Name} - {result.Summary} ({FormatDuration(result.Duration)})");
    }

    public void WriteFailed(StageResult result)
    {
        SetColor(ConsoleColor.Red);
        Console.Write("[!!]");
        ResetColor();
        Console.WriteLine($" {result.Name} - {result.Summary} ({FormatDuration(result.Duration)})");
    }

    public void WriteSkipped(string stage, string reason)
    {
        SetColor(ConsoleColor.DarkGray);
        Console.Write("[--]");
        ResetColor();
        Console.WriteLine($" {stage} - {reason}");
    }

    public void WriteCommand(string command)
    {
        SetColor(ConsoleColor.DarkGray);
        Console.WriteLine($"     > {command}");
        ResetColor();
    }

    public void WriteOutput(string line)
    {
        SetColor(ConsoleColor.DarkGray);
        Console.WriteLine($"     {line}");
        ResetColor();
    }

    public void WriteFailureSummary(StageResult result, string logPath)
    {
        if (result.OutputTail.Count > 0)
        {
            Console.WriteLine();
            WriteColored("Last output:", ConsoleColor.Yellow);
            foreach (var line in result.OutputTail)
            {
                Console.WriteLine($"  {line}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Full output: {logPath}");
    }

    public void WriteSuccessSummary(IReadOnlyList<StageResult> results, string logPath, bool dryRun)
    {
        var completed = results.Count(result => result.Duration > TimeSpan.Zero && result.Succeeded);
        Console.WriteLine();
        WriteColored(dryRun ? "Flight plan looks good." : $"Deployment pipeline complete. {completed} stage(s) succeeded.", ConsoleColor.Green);
        Console.WriteLine($"Full output: {logPath}");
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalSeconds < 1
        ? $"{duration.TotalMilliseconds:0} ms"
        : $"{duration.TotalSeconds:0.0} s";

    private void WriteColored(string text, ConsoleColor color)
    {
        SetColor(color);
        Console.WriteLine(text);
        ResetColor();
    }

    private void SetColor(ConsoleColor color)
    {
        if (useColor)
        {
            Console.ForegroundColor = color;
        }
    }

    private void ResetColor()
    {
        if (useColor)
        {
            Console.ResetColor();
        }
    }
}

internal sealed record DeployOptions(
    bool Build,
    bool Test,
    bool Publish,
    bool Deploy,
    bool Run,
    bool DryRun,
    bool Verbose,
    bool UseColor,
    bool NoRestore,
    string Configuration,
    string OutputDirectory,
    string RemoteHost,
    string RemoteUser,
    string RemoteDirectory)
{
    public DeployOptions ResolvePaths(string repoRoot) => this with
    {
        OutputDirectory = Path.GetFullPath(OutputDirectory, repoRoot)
    };

    public static ParseResult Parse(string[] args)
    {
        var options = new DeployOptions(
            Build: true,
            Test: true,
            Publish: true,
            Deploy: true,
            Run: false,
            DryRun: false,
            Verbose: false,
            UseColor: true,
            NoRestore: false,
            Configuration: "Release",
            OutputDirectory: Path.Combine("out", "reachrobot"),
            RemoteHost: Environment.GetEnvironmentVariable("REACHTETHER_DEPLOY_HOST") ?? "reachy-mini.local",
            RemoteUser: Environment.GetEnvironmentVariable("REACHTETHER_DEPLOY_USER") ?? "pollen",
            RemoteDirectory: Environment.GetEnvironmentVariable("REACHTETHER_DEPLOY_DIRECTORY") ?? "/home/pollen/reachrobot");

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            string? ReadValue()
            {
                if (++index >= args.Length)
                {
                    return null;
                }

                return args[index];
            }

            switch (argument)
            {
                case "-h":
                case "--help":
                    return ParseResult.Help();
                case "--local":
                case "--publish-only":
                    options = options with { Deploy = false, Run = false };
                    break;
                case "--deploy-only":
                    options = options with { Build = false, Test = false, Publish = false, Deploy = true };
                    break;
                case "--skip-build":
                    options = options with { Build = false };
                    break;
                case "--skip-tests":
                case "--no-tests":
                    options = options with { Test = false };
                    break;
                case "--skip-publish":
                    options = options with { Publish = false };
                    break;
                case "--skip-deploy":
                    options = options with { Deploy = false };
                    break;
                case "--run":
                    options = options with { Run = true };
                    break;
                case "--dry-run":
                    options = options with { DryRun = true };
                    break;
                case "--no-restore":
                    options = options with { NoRestore = true };
                    break;
                case "-v":
                case "--verbose":
                    options = options with { Verbose = true };
                    break;
                case "--no-color":
                    options = options with { UseColor = false };
                    break;
                case "--host":
                    {
                        var value = ReadValue();
                        if (value is null)
                        {
                            return ParseResult.Failed("--host requires a value");
                        }

                        options = options with { RemoteHost = value };
                        break;
                    }
                case "--user":
                    {
                        var value = ReadValue();
                        if (value is null)
                        {
                            return ParseResult.Failed("--user requires a value");
                        }

                        options = options with { RemoteUser = value };
                        break;
                    }
                case "--remote-dir":
                    {
                        var value = ReadValue();
                        if (value is null)
                        {
                            return ParseResult.Failed("--remote-dir requires a value");
                        }

                        options = options with { RemoteDirectory = value };
                        break;
                    }
                case "--output":
                    {
                        var value = ReadValue();
                        if (value is null)
                        {
                            return ParseResult.Failed("--output requires a value");
                        }

                        options = options with { OutputDirectory = value };
                        break;
                    }
                case "--configuration":
                    {
                        var value = ReadValue();
                        if (value is null)
                        {
                            return ParseResult.Failed("--configuration requires a value");
                        }

                        options = options with { Configuration = value };
                        break;
                    }
                default:
                    return ParseResult.Failed($"unknown option '{argument}'");
            }
        }

        if (string.IsNullOrWhiteSpace(options.RemoteHost) || string.IsNullOrWhiteSpace(options.RemoteUser))
        {
            return ParseResult.Failed("robot host and user cannot be empty");
        }

        if (!options.RemoteDirectory.StartsWith('/'))
        {
            return ParseResult.Failed("--remote-dir must be an absolute Linux path");
        }

        return ParseResult.Success(options);
    }
}

internal sealed record ParseResult(DeployOptions? Options, string? Error, bool ShowHelp)
{
    public static ParseResult Success(DeployOptions options) => new(options, null, false);

    public static ParseResult Failed(string error) => new(null, error, false);

    public static ParseResult Help() => new(null, null, true);
}

internal static class RepoLocator
{
    public static string? FindRoot()
    {
        foreach (var startingPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(startingPath);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "dotNet", "ReachTether.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }
}

internal static class ExecutableLocator
{
    public static string? Find(string command)
    {
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : [string.Empty];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim(), command + extension.ToLowerInvariant());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}

internal static class CommandFormatter
{
    public static string Format(string fileName, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { fileName }.Concat(arguments.Select(Quote)));

    private static string Quote(string value) => value.Any(char.IsWhiteSpace) || value.Contains('"')
        ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
        : value;
}

internal static class BuildArguments
{
    public static IReadOnlyList<string> ForBuild(string solutionPath, string configuration, bool noRestore)
    {
        var arguments = new List<string> { "build", solutionPath, "-c", configuration };
        if (noRestore)
        {
            arguments.Add("--no-restore");
        }

        return arguments;
    }

    public static IReadOnlyList<string> ForPublish(
        string projectPath,
        string configuration,
        string outputDirectory,
        bool noRestore)
    {
        var arguments = new List<string>
        {
            "publish", projectPath, "-c", configuration, "-r", "linux-arm64",
            "--self-contained", "false", "-o", outputDirectory
        };
        if (noRestore)
        {
            arguments.Add("--no-restore");
        }

        return arguments;
    }
}

internal static class HelpText
{
    public static void Write()
    {
        Console.WriteLine("ReachTether Deploy - build, test, publish, and ship ReachTether.Robot");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project dotNet/tools/ReachTether.Deploy -- [options]");
        Console.WriteLine();
        Console.WriteLine("Pipeline options:");
        Console.WriteLine("  --local, --publish-only  Build, test, and publish without copying");
        Console.WriteLine("  --deploy-only            Copy an existing out/reachrobot bundle");
        Console.WriteLine("  --skip-build             Skip the solution build");
        Console.WriteLine("  --skip-tests             Skip test execution");
        Console.WriteLine("  --skip-publish           Skip linux-arm64 publish");
        Console.WriteLine("  --skip-deploy            Skip scp");
        Console.WriteLine("  --run                    Start the robot app over attached SSH after deploy");
        Console.WriteLine("  --dry-run                Print commands without executing them");
        Console.WriteLine("  --no-restore             Use already-restored NuGet assets");
        Console.WriteLine();
        Console.WriteLine("Target options:");
        Console.WriteLine("  --host <host>            Robot hostname (default: reachy-mini.local)");
        Console.WriteLine("  --user <user>            SSH user (default: pollen)");
        Console.WriteLine("  --remote-dir <path>      Deploy directory (default: /home/pollen/reachrobot)");
        Console.WriteLine("  --output <path>          Local bundle directory (default: out/reachrobot)");
        Console.WriteLine("  --configuration <name>   Build configuration (default: Release)");
        Console.WriteLine();
        Console.WriteLine("Display options:");
        Console.WriteLine("  -v, --verbose            Stream captured command output");
        Console.WriteLine("  --no-color               Disable colored status output");
        Console.WriteLine("  -h, --help               Show help");
        Console.WriteLine();
        Console.WriteLine("Environment overrides:");
        Console.WriteLine("  REACHTETHER_DEPLOY_HOST, REACHTETHER_DEPLOY_USER, REACHTETHER_DEPLOY_DIRECTORY");
    }
}
