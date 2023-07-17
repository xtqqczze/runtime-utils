using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Threading.Channels;

string? jobId = Environment.GetEnvironmentVariable("JOB_ID");

Console.WriteLine($"{nameof(jobId)}={jobId}");

if (jobId is null)
{
    return;
}

var job = new Job(jobId);
await job.RunJobAsync();

public class Job
{
    private readonly Stopwatch _jobStartStopwatch = Stopwatch.StartNew();
    private readonly string _jobId;
    private readonly HttpClient _client;
    private readonly Channel<string> _channel;
    private CancellationToken _jobTimeout;
    private readonly Stopwatch _lastLogEntry = new();
    private Dictionary<string, string> _metadata = new();

    public string SourceRepo => _metadata["PrRepo"];
    public string SourceBranch => _metadata["PrBranch"];
    public string CustomArguments => _metadata["CustomArguments"];

    public static bool IsArm => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    private bool TryGetFlag(string name) => CustomArguments.Contains($"-{name}", StringComparison.OrdinalIgnoreCase);

    public Job(string jobId)
    {
        _jobId = jobId;

        _client = new HttpClient
        {
            DefaultRequestVersion = HttpVersion.Version20,
            BaseAddress = new Uri("https://mihubot.xyz/api/RuntimeUtils/Jobs/")
        };

        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(100_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public async Task RunJobAsync()
    {
        _lastLogEntry.Start();

        using var jobCts = new CancellationTokenSource(TimeSpan.FromHours(5));
        _jobTimeout = jobCts.Token;

        Task channelReaderTask = Task.Run(() => ReadChannelAsync());

        try
        {
            _metadata = await GetFromJsonAsync<Dictionary<string, string>>("Metadata");

            await LogAsync($"{nameof(SourceRepo)}={SourceRepo}");
            await LogAsync($"{nameof(SourceBranch)}={SourceBranch}");
            await LogAsync($"{nameof(CustomArguments)}={CustomArguments}");
            await LogAsync($"{nameof(Environment.ProcessorCount)}={Environment.ProcessorCount}");

            await CloneRuntimeAndSetupToolsAsync();

            await BuildRuntimeAsync();

            await InstallRuntimeDotnetSdkAsync();

            await CollectCorelibDiffsAsync();

            await CollectFrameworksDiffsAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Something went wrong: {ex}");
            await LogAsync(ex.ToString());
        }

        try
        {
            _channel.Writer.TryComplete();
            await channelReaderTask.WaitAsync(_jobTimeout);
        }
        catch { }

        await _client.GetStringAsync($"Complete/{_jobId}", CancellationToken.None);
    }

    private async Task CloneRuntimeAndSetupToolsAsync()
    {
        Task cloneRuntimeTask = Task.Run(async () =>
        {
            const string LogPrefix = "Setup runtime";

            string template = await File.ReadAllTextAsync("setup-runtime.sh.template");
            string script = template
                .ReplaceLineEndings()
                .Replace("{{SOURCE_REPOSITORY}}", SourceRepo)
                .Replace("{{SOURCE_BRANCH}}", SourceBranch);

            await LogAsync($"Using runtime setup script:\n{script}");
            await File.WriteAllTextAsync("setup-runtime.sh", script);
            await RunProcessAsync("bash", "-x setup-runtime.sh", logPrefix: LogPrefix);
        });

        Task setupZipAndWgetTask = Task.Run(async () =>
        {
            const string LogPrefix = "Setup zip & wget";
            await RunProcessAsync("apt-get", "install -y zip wget", logPrefix: LogPrefix);
        });

        Task setupJitutilsTask = Task.Run(async () =>
        {
            const string LogPrefix = "Setup jitutils";
            await setupZipAndWgetTask;

            await RunProcessAsync("git", "clone --no-tags --single-branch --progress https://github.com/dotnet/jitutils.git", logPrefix: LogPrefix);

            if (IsArm)
            {
                const string ToolsLink = "https://raw.githubusercontent.com/MihaZupan/runtime-utils/clang-tools";
                Directory.CreateDirectory("jitutils/bin");
                await RunProcessAsync("wget", $"-O jitutils/bin/clang-format {ToolsLink}/clang-format", logPrefix: LogPrefix);
                await RunProcessAsync("wget", $"-O jitutils/bin/clang-tidy {ToolsLink}/clang-tidy", logPrefix: LogPrefix);
                await RunProcessAsync("chmod", "751 jitutils/bin/clang-format", logPrefix: LogPrefix);
                await RunProcessAsync("chmod", "751 jitutils/bin/clang-tidy", logPrefix: LogPrefix);
            }

            await RunProcessAsync("bash", "bootstrap.sh", logPrefix: LogPrefix, workDir: "jitutils");
        });

        Task createDirectoriesTask = Task.Run(() =>
        {
            Directory.CreateDirectory("artifacts-main");
            Directory.CreateDirectory("artifacts-pr");
            Directory.CreateDirectory("clr-checked-main");
            Directory.CreateDirectory("clr-checked-pr");
            Directory.CreateDirectory("jit-diffs");
            Directory.CreateDirectory("jit-diffs/corelib");
            Directory.CreateDirectory("jit-diffs/frameworks");
        });

        await createDirectoriesTask;
        await setupJitutilsTask;
        await setupZipAndWgetTask;
        await cloneRuntimeTask;
    }

    private async Task BuildRuntimeAsync()
    {
        await BuildAndCopyRuntimeBranchBitsAsync("main");
        Task uploadMainBitsTask = ZipAndUploadRuntimeBitsAsync("main");

        await RunProcessAsync("git", "switch pr", workDir: "runtime");

        await BuildAndCopyRuntimeBranchBitsAsync("pr");
        await ZipAndUploadRuntimeBitsAsync("pr");

        await uploadMainBitsTask;

        async Task ZipAndUploadRuntimeBitsAsync(string branch)
        {
            await ZipAndUploadArtifactAsync($"build-artifacts-{branch}", $"artifacts-{branch}");
            await ZipAndUploadArtifactAsync($"build-clr-checked-{branch}", $"clr-checked-{branch}");
        }

        async Task BuildAndCopyRuntimeBranchBitsAsync(string branch)
        {
            string arch = IsArm ? "arm64" : "x64";

            await RunProcessAsync("bash", "build.sh clr+libs -c Release", logPrefix: $"{branch} release", workDir: "runtime");

            Task copyReleaseBitsTask = Task.Run(async () =>
            {
                await RunProcessAsync("cp", $"-r runtime/artifacts/bin/coreclr/linux.{arch}.Release/. artifacts-{branch}", logPrefix: $"{branch} release");
                await RunProcessAsync("cp", $"-r runtime/artifacts/bin/runtime/net8.0-linux-Release-{arch}/. artifacts-{branch}", logPrefix: $"{branch} release");
            });

            await RunProcessAsync("bash", "build.sh clr.jit -c Checked", logPrefix: $"{branch} checked", workDir: "runtime");
            await RunProcessAsync("cp", $"-r runtime/artifacts/bin/coreclr/linux.{arch}.Checked/. clr-checked-{branch}", logPrefix: $"{branch} checked");

            await copyReleaseBitsTask;
        }
    }

    private async Task InstallRuntimeDotnetSdkAsync()
    {
        await RunProcessAsync("wget", "https://dot.net/v1/dotnet-install.sh");
        await RunProcessAsync("bash", "dotnet-install.sh --jsonfile runtime/global.json --install-dir /usr/lib/dotnet");
    }

    private async Task CollectCorelibDiffsAsync()
    {
        await JitDiffAsync(baseline: true, corelib: true);
        await JitDiffAsync(baseline: false, corelib: true);

        Task uploadCorelibDiffsTask = ZipAndUploadArtifactAsync("jit-diffs-corelib", "jit-diffs/corelib");

        string coreLibDiff = await JitAnalyzeAsync("corelib");
        await UploadArtifactAsync("diff-corelib.txt", coreLibDiff);

        await uploadCorelibDiffsTask;
    }

    private async Task CollectFrameworksDiffsAsync()
    {
        if (TryGetFlag("remove-corelib-before-frameworks"))
        {
            // Avoid running diffs for corelib twice
            File.Delete("artifacts-main/System.Private.CoreLib.dll");
            File.Delete("artifacts-pr/System.Private.CoreLib.dll");
        }

        bool runSequential =
            TryGetFlag("force-frameworks-sequential") ? true :
            TryGetFlag("force-frameworks-parallel") ? false :
            await GetSystemMemoryGBAsync() < 16;

        await JitDiffAsync(baseline: true, corelib: false, sequential: runSequential);
        await JitDiffAsync(baseline: false, corelib: false, sequential: runSequential);

        Task uploadFrameworksDiffsTask = ZipAndUploadArtifactAsync("jit-diffs-frameworks", "jit-diffs/frameworks");

        string frameworksDiff = await JitAnalyzeAsync("frameworks");
        await UploadArtifactAsync("diff-frameworks.txt", frameworksDiff);

        await uploadFrameworksDiffsTask;
    }

    private async Task ZipAndUploadArtifactAsync(string zipFileName, string folderPath)
    {
        zipFileName = $"{zipFileName}.zip";
        await RunProcessAsync("zip", $"-3 -r {zipFileName} {folderPath}", logPrefix: zipFileName);
        await UploadArtifactAsync(zipFileName);
    }

    private async Task LogAsync(string message)
    {
        lock (_lastLogEntry)
        {
            _lastLogEntry.Restart();
        }

        try
        {
            TimeSpan elapsed = _jobStartStopwatch.Elapsed;
            int hours = elapsed.Hours;
            int minutes = elapsed.Minutes;
            int seconds = elapsed.Seconds;
            await _channel.Writer.WriteAsync($"[{hours:D2}:{minutes:D2}:{seconds:D2}] {message}", _jobTimeout);
        }
        catch { }
    }

    private async Task ErrorAsync(string message)
    {
        try
        {
            _channel.Writer.TryComplete(new Exception(message));

            await PostAsJsonAsync("Logs", new[] { $"ERROR: {message}" });
        }
        catch { }
    }

    private async Task ReadChannelAsync()
    {
        bool completed = false;

        Task heartbeatTask = Task.Run(async () =>
        {
            try
            {
                while (!Volatile.Read(ref completed))
                {
                    await Task.Delay(100, _jobTimeout);

                    lock (_lastLogEntry)
                    {
                        if (_lastLogEntry.Elapsed.TotalSeconds < 2 * 60)
                        {
                            continue;
                        }
                    }

                    await LogAsync("Heartbeat - I'm still here");
                }
            }
            catch { }
        });

        try
        {
            ChannelReader<string> reader = _channel.Reader;

            while (await reader.WaitToReadAsync(_jobTimeout))
            {
                List<string> messages = new();
                while (reader.TryRead(out var message))
                {
                    messages.Add(message);
                }

                await PostAsJsonAsync("Logs", messages.ToArray());
            }
        }
        catch (Exception ex)
        {
            await ErrorAsync(ex.ToString());
        }

        Volatile.Write(ref completed, true);
        await heartbeatTask.WaitAsync(_jobTimeout);
    }

    private async Task<string> JitAnalyzeAsync(string folder)
    {
        List<string> output = new();

        await RunProcessAsync("jitutils/bin/jit-analyze",
            $"-b jit-diffs/{folder}/dasmset_1/base -d jit-diffs/{folder}/dasmset_2/base -r -c 100",
            output);

        return string.Join('\n', output);
    }

    private async Task JitDiffAsync(bool baseline, bool corelib, bool sequential = false)
    {
        string corelibOrFrameworks = corelib ? "corelib" : "frameworks";
        string artifactsFolder = baseline ? "artifacts-main" : "artifacts-pr";
        string checkedClrFolder = baseline ? "clr-checked-main" : "clr-checked-pr";

        bool useCctors = !TryGetFlag("nocctors");
        bool useTier0 = TryGetFlag("tier0");

        Console.WriteLine($"Using cctors: {useCctors}");
        Console.WriteLine($"Using tier0: {useTier0}");

        await RunProcessAsync("jitutils/bin/jit-diff",
            $"diff " +
            (sequential ? "--sequential " : "") +
            (useCctors ? "--cctors " : "") +
            (useTier0 ? "--tier0 " : "") +
            $"--output jit-diffs/{corelibOrFrameworks} --{corelibOrFrameworks} --pmi " +
            $"--core_root {artifactsFolder} " +
            $"--base {checkedClrFolder}");
    }

    private async Task RunProcessAsync(string fileName, string arguments, List<string>? output = null, string? logPrefix = null, string? workDir = null)
    {
        if (logPrefix is not null)
        {
            logPrefix = $"[{logPrefix}] ";
        }

        await LogAsync($"{logPrefix}Running '{fileName} {arguments}'");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = workDir ?? string.Empty,
            }
        };

        process.Start();

        await Task.WhenAll(
            Task.Run(() => ReadOutputStreamAsync(process.StandardOutput)),
            Task.Run(() => ReadOutputStreamAsync(process.StandardError)),
            Task.Run(async () =>
            {
                try
                {
                    await process.WaitForExitAsync(_jobTimeout);
                }
                catch
                {
                    process.Kill();
                    throw;
                }
            }));

        async Task ReadOutputStreamAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is string line)
            {
                if (output is not null)
                {
                    lock (output)
                    {
                        output.Add(line);
                    }
                }

                await LogAsync($"{logPrefix}{line}");
            }
        }
    }

    private async Task UploadArtifactAsync(string fileName, string contents)
    {
        string filePath = Path.Combine(Path.GetTempPath(), fileName);
        try
        {
            await File.WriteAllTextAsync(filePath, contents);

            await UploadArtifactAsync(filePath);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private async Task UploadArtifactAsync(string path)
    {
        string name = Path.GetFileName(path);

        await LogAsync($"Uploading '{name}'");

        await using FileStream fs = File.OpenRead(path);
        using StreamContent content = new(fs);

        using var response = await _client.PostAsync(
            $"Artifact/{_jobId}/{Uri.EscapeDataString(name)}",
            content,
            _jobTimeout);
    }

    private async Task<T> GetFromJsonAsync<T>(string path)
    {
        try
        {
            return await _client.GetFromJsonAsync<T>($"{path}/{_jobId}", _jobTimeout) ?? throw new Exception("Null response");
        }
        catch (Exception ex)
        {
            await LogAsync($"Failed to fetch resource '{path}': {ex}");
            throw;
        }
    }

    private async Task PostAsJsonAsync(string path, object? value)
    {
        try
        {
            using var response = await _client.PostAsJsonAsync($"{path}/{_jobId}", value, _jobTimeout);
        }
        catch (Exception ex)
        {
            await LogAsync($"Failed to post resource '{path}': {ex}");
            throw;
        }
    }

    private async Task<int> GetSystemMemoryGBAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                foreach (string line in await File.ReadAllLinesAsync("/proc/meminfo", _jobTimeout))
                {
                    if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    {
                        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                        int gbAvailable = (int)(long.Parse(parts[1]) / 1024 / 1024);

                        await LogAsync($"System memory available: {gbAvailable} GB");

                        return gbAvailable;
                    }
                }
            }
            catch (Exception ex)
            {
                await LogAsync($"Failed to get available memory: {ex}");
            }
        }

        return 1;
    }
}