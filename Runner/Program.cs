using Hardware.Info;
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
    private HardwareInfo? _hardwareInfo;

    private volatile bool _completed;

    public string SourceRepo => _metadata["PrRepo"];
    public string SourceBranch => _metadata["PrBranch"];
    public string CustomArguments => _metadata["CustomArguments"];

    public (string Repo, string Branch)[] GetPRList(string name)
    {
        if (_metadata.TryGetValue(name, out string? value))
        {
            return value.Split(',').Select(pr =>
            {
                string[] parts = pr.Split(';');
                return (parts[0], parts[1]);
            }).ToArray();
        }

        return Array.Empty<(string, string)>();
    }

    public static bool IsArm => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    private bool TryGetFlag(string name) => CustomArguments.Contains($"-{name}", StringComparison.OrdinalIgnoreCase);

    public Job(string jobId)
    {
        _jobId = jobId;

        _client = new HttpClient
        {
            DefaultRequestVersion = HttpVersion.Version20,
            BaseAddress = new Uri("https://mihubot.xyz/api/RuntimeUtils/Jobs/"),
            Timeout = TimeSpan.FromMinutes(5),
        };

        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(100_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    }

    public async Task RunJobAsync()
    {
        _lastLogEntry.Start();

        using var jobCts = new CancellationTokenSource(TimeSpan.FromHours(5));
        _jobTimeout = jobCts.Token;

        Task channelReaderTask = Task.Run(() => ReadChannelAsync());
        Task systemUsageTask = Task.Run(() => StreamSystemHardwareInfoAsync());

        try
        {
            _metadata = await GetFromJsonAsync<Dictionary<string, string>>("Metadata");
            _metadata = new Dictionary<string, string>(_metadata, StringComparer.OrdinalIgnoreCase);

            await LogAsync($"{nameof(SourceRepo)}={SourceRepo}");
            await LogAsync($"{nameof(SourceBranch)}={SourceBranch}");
            await LogAsync($"{nameof(CustomArguments)}={CustomArguments}");
            await LogAsync($"{nameof(Environment.ProcessorCount)}={Environment.ProcessorCount}");

            await CloneRuntimeAndSetupToolsAsync();

            await BuildRuntimeAsync();

            await InstallRuntimeDotnetSdkAsync();

            await CollectFrameworksDiffsAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Something went wrong: {ex}");
            await LogAsync(ex.ToString());
        }

        _completed = true;

        try
        {
            await systemUsageTask.WaitAsync(_jobTimeout);
        }
        catch { }

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
                .Replace("{{MERGE_BASELINE_BRANCHES}}", GetMergeScript("dependsOn"))
                .Replace("{{MERGE_PR_BRANCHES}}", GetMergeScript("combineWith"));

            await LogAsync($"Using runtime setup script:\n{script}");
            await File.WriteAllTextAsync("setup-runtime.sh", script);
            await RunProcessAsync("bash", "-x setup-runtime.sh", logPrefix: LogPrefix);

            string GetMergeScript(string name)
            {
                int counter = 0;

                List<(string Repo, string Branch)> prList = new(GetPRList(name));

                if (name == "combineWith")
                {
                    prList.Insert(0, (SourceRepo, SourceBranch));
                }

                return string.Join('\n', prList
                    .Select(pr =>
                    {
                        int index = ++counter;
                        string remoteName = $"{name}{index}";

                        return
                            $"git remote add {remoteName} https://github.com/{pr.Repo}\n" +
                            $"git fetch {remoteName} {pr.Branch}\n" +
                            $"git log {remoteName}/{pr.Branch} -1\n" +
                            $"git merge --no-edit {remoteName}/{pr.Branch}\n" +
                            $"git log -1\n";
                    }));
            };
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

                const string BaseDirectory = "runtime/artifacts/bin/runtime";

                string folder = Directory.GetDirectories(BaseDirectory)
                    .Select(f => Path.GetRelativePath(BaseDirectory, f))
                    .Where(f => f.StartsWith("net", StringComparison.OrdinalIgnoreCase))
                    .Where(f => f.Contains("Release", StringComparison.OrdinalIgnoreCase))
                    .Where(f => f.Contains("linux", StringComparison.OrdinalIgnoreCase))
                    .Where(f => f.Contains(arch, StringComparison.OrdinalIgnoreCase))
                    .Single();

                await RunProcessAsync("cp", $"-r {BaseDirectory}/{folder}/. artifacts-{branch}", logPrefix: $"{branch} release");
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

    private async Task CollectFrameworksDiffsAsync()
    {
        bool runSequential =
            TryGetFlag("force-frameworks-sequential") ? true :
            TryGetFlag("force-frameworks-parallel") ? false :
            GetTotalSystemMemoryGB() < 16;

        await JitDiffAsync(baseline: true, sequential: runSequential);
        await JitDiffAsync(baseline: false, sequential: runSequential);

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
            output,
            checkExitCode: false);

        return string.Join('\n', output);
    }

    private async Task JitDiffAsync(bool baseline, bool sequential = false)
    {
        string artifactsFolder = baseline ? "artifacts-main" : "artifacts-pr";
        string checkedClrFolder = baseline ? "clr-checked-main" : "clr-checked-pr";

        bool useCctors = !TryGetFlag("nocctors");
        bool useTier0 = TryGetFlag("tier0");

        await LogAsync($"Using cctors: {useCctors}");
        await LogAsync($"Using tier0: {useTier0}");

        await RunProcessAsync("jitutils/bin/jit-diff",
            $"diff " +
            (sequential ? "--sequential " : "") +
            (useCctors ? "--cctors " : "") +
            (useTier0 ? "--tier0 " : "") +
            $"--output jit-diffs/frameworks --frameworks --pmi " +
            $"--core_root {artifactsFolder} " +
            $"--base {checkedClrFolder}");
    }

    private async Task RunProcessAsync(
        string fileName, string arguments,
        List<string>? output = null,
        string? logPrefix = null,
        string? workDir = null,
        bool checkExitCode = true)
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

        if (checkExitCode && process.ExitCode != 0)
        {
            throw new Exception($"{fileName} {arguments} failed with exit code {process.ExitCode}");
        }

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

    private int GetTotalSystemMemoryGB()
    {
        var memory = _hardwareInfo?.MemoryStatus;

        if (memory is null)
        {
            return 1;
        }

        return (int)(memory.TotalPhysical / 1024 / 1024 / 1024);
    }

    private async Task StreamSystemHardwareInfoAsync()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(0.1));

        int failureMessages = 0;

        List<(TimeSpan Elapsed, double CpuUsage, double MemoryUsage)> usageHistory = new();

        while (!_completed && await timer.WaitForNextTickAsync(_jobTimeout))
        {
            try
            {
                _hardwareInfo ??= new HardwareInfo();
                _hardwareInfo.RefreshMemoryStatus();
                _hardwareInfo.RefreshCPUList(includePercentProcessorTime: true);
            }
            catch (Exception ex)
            {
                failureMessages++;
                if (failureMessages <= 10)
                {
                    await LogAsync($"Failed to obtain hardware info: {ex}");
                }

                await Task.Delay(5_000, _jobTimeout);
                continue;
            }

            var elapsed = stopwatch.Elapsed;
            stopwatch.Restart();

            var cores = _hardwareInfo.CpuList.First().CpuCoreList;
            var totalCpuUsage = cores.Sum(c => (double)c.PercentProcessorTime) / 100;
            var coreCount = cores.Count;

            var memory = _hardwareInfo.MemoryStatus;
            var availableGB = (double)memory.AvailablePhysical / 1024 / 1024 / 1024;
            var totalGB = (double)memory.TotalPhysical / 1024 / 1024 / 1024;
            var usedGB = totalGB - availableGB;

            usageHistory.Add((elapsed, totalCpuUsage / coreCount, usedGB / totalGB));

            await PostAsJsonAsync("SystemInfo", new
            {
                CpuUsage = totalCpuUsage,
                CpuCoresAvailable = coreCount,
                MemoryUsageGB = usedGB,
                MemoryAvailableGB = totalGB,
            });
        }

        if (failureMessages == 0 && usageHistory.Count > 0)
        {
            long durationTicks = usageHistory.Sum(h => h.Elapsed.Ticks);
            double averageCpuUsage = usageHistory.Sum(h => h.CpuUsage * h.Elapsed.Ticks) / durationTicks;
            double averageMemoryUsage = usageHistory.Sum(h => h.MemoryUsage * h.Elapsed.Ticks) / durationTicks;
            averageCpuUsage *= 100;
            averageMemoryUsage *= 100;

            await LogAsync($"Average overall CPU usage estimate: {(int)averageCpuUsage} %");
            await LogAsync($"Average overall memory usage estimate: {(int)averageMemoryUsage} %");
        }
    }
}
