using Hardware.Info;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.IO.Compression;
using Azure.Storage.Blobs;
using System.Collections.Concurrent;

namespace Runner;

public abstract class JobBase
{
    public static bool IsArm => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    protected static readonly TimeSpan MaxJobDuration = TimeSpan.FromHours(5);

    private readonly CancellationTokenSource _jobCts = new(MaxJobDuration);
    private readonly string _jobId;
    private readonly Channel<string> _channel;
    private readonly Stopwatch _lastLogEntry = new();
    private HardwareInfo? _hardwareInfo;
    private volatile bool _completed;

    protected readonly HttpClient HttpClient;
    protected readonly DateTime StartTime;
    protected CancellationToken JobTimeout => _jobCts.Token;
    public Dictionary<string, string> Metadata { get; }
    public readonly string OriginalWorkingDirectory = Environment.CurrentDirectory;

    public TimeSpan ElapsedTime => DateTime.UtcNow - StartTime;

    public string? LastProgressSummary { get; set; }

    public readonly ConcurrentQueue<Task> PendingTasks = new();

    public string CustomArguments => Metadata["CustomArguments"];
    public string SourceRepo => Metadata["PrRepo"];
    public string SourceBranch => Metadata["PrBranch"];

    public bool TryGetFlag(string name) => CustomArguments.Contains($"-{name}", StringComparison.OrdinalIgnoreCase);

    public string GetArgument(string argument, string @default)
    {
        return TryGetArgument(argument, out string? value)
            ? value
            : @default;
    }

    private bool TryGetArgument(string argument, [NotNullWhen(true)] out string? value)
    {
        value = null;

        ReadOnlySpan<char> arguments = CustomArguments;
        argument = $"-{argument} ";

        int offset = arguments.IndexOf(argument, StringComparison.OrdinalIgnoreCase);
        if (offset < 0) return false;

        arguments = arguments.Slice(offset + argument.Length);

        int length = arguments.IndexOf(' ');
        if (length >= 0)
        {
            arguments = arguments.Slice(0, length);
        }

        value = arguments.Trim().ToString();
        return value.Length > 0;
    }

    protected BlobContainerClient PersistentStateClient => new(new Uri(Metadata["PersistentStateSasUri"]));

    public JobBase(HttpClient client, Dictionary<string, string> metadata)
    {
        HttpClient = client;
        Metadata = metadata;
        _jobId = metadata["JobId"];
        StartTime = new DateTime(long.Parse(Metadata["JobStartTime"]), DateTimeKind.Utc);

        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(100_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        if (StartTime >= DateTime.UtcNow)
        {
            _channel.Writer.TryWrite($"Start time ({StartTime.Ticks}) is after current time ({DateTime.UtcNow.Ticks})?");
            StartTime = DateTime.UtcNow;
        }
    }

    protected abstract Task RunJobCoreAsync();

    public async Task RunJobAsync()
    {
        _lastLogEntry.Start();

        Task channelReaderTask = Task.Run(() => ReadChannelAsync());
        Task systemUsageTask = Task.Run(() => StreamSystemHardwareInfoAsync());

        _ = channelReaderTask.ContinueWith(_ => _jobCts.Cancel());

        try
        {
            await LogAsync($"{nameof(CustomArguments)}={CustomArguments}");
            await LogAsync($"{nameof(Environment.ProcessorCount)}={Environment.ProcessorCount}");
            await LogAsync($"{nameof(Environment.CurrentDirectory)}={Environment.CurrentDirectory}");
            await LogAsync($"{nameof(RuntimeInformation.FrameworkDescription)}={RuntimeInformation.FrameworkDescription}");
            await LogAsync($"{nameof(RuntimeInformation.RuntimeIdentifier)}={RuntimeInformation.RuntimeIdentifier}");
            await LogAsync($"{nameof(SourceRepo)}={SourceRepo}");
            await LogAsync($"{nameof(SourceBranch)}={SourceBranch}");

            Console.WriteLine($"Starting {Metadata["JobType"]} ({Metadata["ExternalId"]}) ...");

            await RunJobCoreAsync();

            await WaitForPendingTasksAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Something went wrong: {ex}");
            await ErrorAsync(ex.ToString());
        }

        _completed = true;

        try
        {
            await systemUsageTask.WaitAsync(JobTimeout);
        }
        catch { }

        try
        {
            _channel.Writer.TryComplete();
            await channelReaderTask.WaitAsync(JobTimeout);
        }
        catch { }

        await HttpClient.GetStringAsync($"Complete/{_jobId}", CancellationToken.None);
    }

    protected async Task WaitForPendingTasksAsync()
    {
        while (PendingTasks.TryDequeue(out Task? task))
        {
            await task;
        }
    }

    public async Task ZipAndUploadArtifactAsync(string zipFileName, string folderPath)
    {
        zipFileName = $"{zipFileName}.zip";
        string zipFilePath = Path.GetFullPath(zipFileName);

        if (OperatingSystem.IsWindows())
        {
            await LogAsync($"[{zipFileName}] Compressing {folderPath}");
            try
            {
                ZipFile.CreateFromDirectory(folderPath, zipFilePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            }
            catch (Exception ex)
            {
                await LogAsync($"[{zipFileName}] Failed to zip {folderPath}: {ex}");
                return;
            }
        }
        else
        {
            folderPath = Path.GetFullPath(folderPath);
            string workDir = Path.GetDirectoryName(folderPath) ?? throw new Exception($"No parent folder for '{folderPath}'?");
            folderPath = Path.GetRelativePath(workDir, folderPath);

            await RunProcessAsync("zip", $"-3 -r {zipFilePath} {folderPath}", logPrefix: zipFileName, workDir: workDir, suppressOutputLogs: true);
        }

        await UploadArtifactAsync(zipFilePath);

        File.Delete(zipFilePath);
    }

    public async Task LogAsync(string message)
    {
        lock (_lastLogEntry)
        {
            _lastLogEntry.Restart();
        }

        try
        {
            TimeSpan elapsed = ElapsedTime;
            int hours = elapsed.Hours;
            int minutes = elapsed.Minutes;
            int seconds = elapsed.Seconds;
            await _channel.Writer.WriteAsync($"[{hours:D2}:{minutes:D2}:{seconds:D2}] {message}", JobTimeout);
        }
        catch { }
    }

    private async Task ErrorAsync(string message)
    {
        try
        {
            try
            {
                await PostAsJsonAsync("Logs", new string[] { $"ERROR: {message}" });
            }
            finally
            {
                _channel.Writer.TryComplete(new Exception(message));
            }
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
                TimeSpan lastElapsed = TimeSpan.Zero;

                while (!Volatile.Read(ref completed))
                {
                    await Task.Delay(100, JobTimeout);

                    TimeSpan elapsed;
                    lock (_lastLogEntry)
                    {
                        elapsed = _lastLogEntry.Elapsed;
                    }

                    if (elapsed.TotalSeconds > 30 && elapsed > lastElapsed + TimeSpan.FromSeconds(30))
                    {
                        Console.WriteLine($"Idle for {elapsed.TotalSeconds} seconds");
                        lastElapsed = elapsed;
                    }

                    if (elapsed.TotalSeconds < 2 * 60)
                    {
                        continue;
                    }

                    await LogAsync("Heartbeat - I'm still here");
                }
            }
            catch { }
        });

        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        }
        catch { }

        try
        {
            ChannelReader<string> reader = _channel.Reader;
            List<string> messages = new();

            while (await reader.WaitToReadAsync(JobTimeout))
            {
                while (reader.TryRead(out var message))
                {
                    messages.Add(message);
                }

                await PostAsJsonAsync("Logs", messages);
                messages.Clear();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Logger failure: {ex}");
            throw;
        }
        finally
        {
            Volatile.Write(ref completed, true);

            if (!JobTimeout.IsCancellationRequested)
            {
                await heartbeatTask.WaitAsync(JobTimeout);
            }
        }
    }

    public async Task<int> RunProcessAsync(
        string fileName, string arguments,
        List<string>? output = null,
        string? logPrefix = null,
        string? workDir = null,
        bool checkExitCode = true,
        Func<string, string>? processLogs = null,
        bool suppressOutputLogs = false,
        bool suppressStartingLog = false,
        ProcessPriorityClass priority = ProcessPriorityClass.Normal,
        CancellationToken cancellationToken = default)
    {
        processLogs ??= i => i;

        if (logPrefix is not null)
        {
            logPrefix = $"[{logPrefix}] ";
        }

        if (suppressOutputLogs && checkExitCode)
        {
            output ??= [];
        }

        if (!suppressStartingLog)
        {
            await LogAsync($"{logPrefix}{processLogs($"Running '{fileName} {arguments}'{(workDir is null ? null : $" from '{workDir}'")}")}");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = workDir ?? string.Empty,
            }
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(JobTimeout, cancellationToken);
        cancellationToken = cts.Token;

        process.Start();

        try
        {
            process.PriorityClass = priority;
        }
        catch (Exception ex)
        {
            if (!process.HasExited)
            {
                await LogAsync($"{logPrefix}{processLogs($"Failed to set process priority: {ex}")}");
            }
        }

        await Task.WhenAll(
            Task.Run(() => ReadOutputStreamAsync(process.StandardOutput), CancellationToken.None),
            Task.Run(() => ReadOutputStreamAsync(process.StandardError), CancellationToken.None),
            Task.Run(async () =>
            {
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch
                {
                    process.Kill(true);
                    throw;
                }
            }, CancellationToken.None));

        if (checkExitCode && process.ExitCode != 0)
        {
            string message = processLogs($"{fileName} {arguments} failed with exit code {process.ExitCode}");

            if (suppressOutputLogs)
            {
                await LogAsync($"{logPrefix}{message}");
                await LogAsync($"{logPrefix}Process output:\n{string.Join('\n', output!)}");
            }

            throw new Exception(message);
        }

        return process.ExitCode;

        async Task ReadOutputStreamAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(cancellationToken) is string line)
            {
                if (output is not null)
                {
                    lock (output)
                    {
                        output.Add(line);
                    }
                }

                if (!suppressOutputLogs)
                {
                    await LogAsync($"{logPrefix}{processLogs(line)}");
                }
            }
        }
    }

    public async Task UploadTextArtifactAsync(string fileName, string contents)
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

    public async Task UploadArtifactAsync(string path, string? fileName = null)
    {
        string name = fileName ?? Path.GetFileName(path);

        await LogAsync($"Uploading '{name}'");

        await using FileStream fs = File.OpenRead(path);
        using StreamContent content = new(fs);

        using var response = await HttpClient.PostAsync(
            $"Artifact/{_jobId}/{Uri.EscapeDataString(name)}",
            content,
            JobTimeout);
    }

    private async Task PostAsJsonAsync(string path, object? value, string? queryArgs = null, int attemptsRemaining = 4)
    {
        int delayMs = 1_000;
        while (true)
        {
            try
            {
                using var response = await HttpClient.PostAsJsonAsync($"{path}/{_jobId}{queryArgs}", value, JobTimeout);

                if (response.Headers.Contains("X-Job-Completed"))
                {
                    _jobCts.Cancel();
                }

                response.EnsureSuccessStatusCode();
                return;
            }
            catch (Exception ex) when (!JobTimeout.IsCancellationRequested)
            {
                await LogAsync($"Failed to post resource '{path}': {ex}");

                if (--attemptsRemaining == 0)
                {
                    throw;
                }

                await Task.Delay(delayMs, JobTimeout);
                delayMs *= 2;
            }
        }
    }

    protected int GetTotalSystemMemoryGB()
    {
        var memory = _hardwareInfo?.MemoryStatus;

        if (memory is null)
        {
            return 1;
        }

        return (int)(memory.TotalPhysical / 1024 / 1024 / 1024);
    }

    protected async Task ChangeWorkingDirectoryToRamOrFastestDiskAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .ToArray();

                if (drives.Length > 1)
                {
                    DriveInfo drive = drives.MaxBy(d => d.AvailableFreeSpace)!;

                    string newWorkDir = Path.Combine(drive.RootDirectory.FullName, "runner-dir");
                    Directory.CreateDirectory(newWorkDir);

                    Environment.CurrentDirectory = newWorkDir;

                    await LogAsync($"Changed working directory from {OriginalWorkingDirectory} to {newWorkDir}");
                }
            }
            catch (Exception ex)
            {
                await LogAsync($"Failed to apply new working directory: {ex}");
            }
            return;
        }

        Stopwatch s = Stopwatch.StartNew();

        do
        {
            if (_hardwareInfo is not null)
            {
                break;
            }

            await Task.Delay(10);
        }
        while (s.Elapsed.TotalSeconds < 5);

        int availableRamGB = GetTotalSystemMemoryGB();

        if (availableRamGB >= 30)
        {
            try
            {
                const string NewWorkDir = "/ramdisk";
                const string LogPrefix = "Prepare RAM disk";

                int ramDiskSize = Math.Min(128, availableRamGB / 4 * 3);
                await RunProcessAsync("mkdir", NewWorkDir, logPrefix: LogPrefix);
                await RunProcessAsync("mount", $"-t tmpfs -o size={ramDiskSize}G tmpfs {NewWorkDir}", logPrefix: LogPrefix);

                Environment.CurrentDirectory = NewWorkDir;

                await LogAsync($"Changed working directory from {OriginalWorkingDirectory} to {NewWorkDir}");
            }
            catch (Exception ex)
            {
                await LogAsync($"Failed to apply new working directory: {ex}");
            }
        }
    }

    private async Task StreamSystemHardwareInfoAsync()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(0.1));

        int failureMessages = 0;

        List<(TimeSpan Elapsed, double CpuUsage, double MemoryUsage)> usageHistory = new();

        while (!_completed && await timer.WaitForNextTickAsync(JobTimeout))
        {
            try
            {
                var info = _hardwareInfo ?? new HardwareInfo();
                info.RefreshMemoryStatus();
                info.RefreshCPUList(includePercentProcessorTime: true);
                _hardwareInfo = info;
            }
            catch (Exception ex)
            {
                failureMessages++;
                if (failureMessages <= 10)
                {
                    await LogAsync($"Failed to obtain hardware info: {ex}");
                }

                await Task.Delay(5_000, JobTimeout);
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

            if (usageHistory.Count == 1)
            {
                await LogAsync($"First hardware info: CpuCoresAvailable={coreCount} MemoryAvailableGB={totalGB}");
            }

            await PostAsJsonAsync("SystemInfo", new
            {
                CpuUsage = totalCpuUsage,
                CpuCoresAvailable = coreCount,
                MemoryUsageGB = usedGB,
                MemoryAvailableGB = totalGB,
            }, LastProgressSummary is { } summary ? $"?progressSummary={Uri.EscapeDataString(summary)}" : null);
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