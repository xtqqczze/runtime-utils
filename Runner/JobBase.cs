using Hardware.Info;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.IO.Compression;
using Azure.Storage.Blobs;

namespace Runner;

public abstract class JobBase
{
    protected static readonly TimeSpan MaxJobDuration = TimeSpan.FromHours(5);

    private readonly string _jobId;
    private readonly HttpClient _client;
    private readonly Channel<string> _channel;
    private readonly Stopwatch _lastLogEntry = new();
    private HardwareInfo? _hardwareInfo;
    private volatile bool _completed;

    protected readonly Stopwatch JobStopwatch = Stopwatch.StartNew();
    protected CancellationToken JobTimeout { get; private set; }
    protected Dictionary<string, string> Metadata { get; }

    public string CustomArguments => Metadata["CustomArguments"];

    protected bool TryGetFlag(string name) => CustomArguments.Contains($"-{name}", StringComparison.OrdinalIgnoreCase);

    protected string GetArgument(string argument, string @default)
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
        _client = client;
        Metadata = metadata;
        _jobId = metadata["JobId"];

        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(100_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    }

    protected abstract Task RunJobCoreAsync();

    public async Task RunJobAsync()
    {
        _lastLogEntry.Start();

        using var jobCts = new CancellationTokenSource(MaxJobDuration);
        JobTimeout = jobCts.Token;

        Task channelReaderTask = Task.Run(() => ReadChannelAsync());
        Task systemUsageTask = Task.Run(() => StreamSystemHardwareInfoAsync());

        _ = channelReaderTask.ContinueWith(_ => jobCts.Cancel());

        try
        {
            await LogAsync($"{nameof(CustomArguments)}={CustomArguments}");
            await LogAsync($"{nameof(Environment.ProcessorCount)}={Environment.ProcessorCount}");
            await LogAsync($"{nameof(Environment.CurrentDirectory)}={Environment.CurrentDirectory}");
            await LogAsync($"{nameof(RuntimeInformation.FrameworkDescription)}={RuntimeInformation.FrameworkDescription}");
            await LogAsync($"{nameof(RuntimeInformation.RuntimeIdentifier)}={RuntimeInformation.RuntimeIdentifier}");

            Console.WriteLine($"Starting {Metadata["JobType"]} ({Metadata["ExternalId"]}) ...");

            await RunJobCoreAsync();
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

        await _client.GetStringAsync($"Complete/{_jobId}", CancellationToken.None);
    }

    protected async Task ZipAndUploadArtifactAsync(string zipFileName, string folderPath)
    {
        zipFileName = $"{zipFileName}.zip";

        if (OperatingSystem.IsWindows())
        {
            await LogAsync($"[{zipFileName}] Compressing {folderPath}");
            try
            {
                ZipFile.CreateFromDirectory(folderPath, zipFileName, CompressionLevel.Optimal, includeBaseDirectory: false);
            }
            catch (Exception ex)
            {
                await LogAsync($"[{zipFileName}] Failed to zip {folderPath}: {ex}");
                return;
            }
        }
        else
        {
            await RunProcessAsync("zip", $"-3 -r {zipFileName} {folderPath}", logPrefix: zipFileName);
        }

        await UploadArtifactAsync(zipFileName);
    }

    protected async Task LogAsync(string message)
    {
        lock (_lastLogEntry)
        {
            _lastLogEntry.Restart();
        }

        try
        {
            TimeSpan elapsed = JobStopwatch.Elapsed;
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
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
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
            await heartbeatTask.WaitAsync(JobTimeout);
        }
    }

    protected async Task RunProcessAsync(
        string fileName, string arguments,
        List<string>? output = null,
        string? logPrefix = null,
        string? workDir = null,
        bool checkExitCode = true,
        CancellationToken cancellationToken = default)
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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(JobTimeout, cancellationToken);

        process.Start();

        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch { }

        await Task.WhenAll(
            Task.Run(() => ReadOutputStreamAsync(process.StandardOutput)),
            Task.Run(() => ReadOutputStreamAsync(process.StandardError)),
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

    protected async Task UploadTextArtifactAsync(string fileName, string contents)
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

    protected async Task UploadArtifactAsync(string path, string? fileName = null)
    {
        string name = fileName ?? Path.GetFileName(path);

        await LogAsync($"Uploading '{name}'");

        await using FileStream fs = File.OpenRead(path);
        using StreamContent content = new(fs);

        using var response = await _client.PostAsync(
            $"Artifact/{_jobId}/{Uri.EscapeDataString(name)}",
            content,
            JobTimeout);
    }

    private async Task PostAsJsonAsync(string path, object? value, int attemptsRemaining = 4)
    {
        int delayMs = 1_000;
        while (true)
        {
            try
            {
                using var response = await _client.PostAsJsonAsync($"{path}/{_jobId}", value, JobTimeout);
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

    protected async ValueTask<int> GetTotalSystemMemoryGBAsync(TimeSpan timeout)
    {
        Stopwatch s = Stopwatch.StartNew();

        do
        {
            if (_hardwareInfo is not null)
            {
                return GetTotalSystemMemoryGB();
            }

            await Task.Delay(10);
        }
        while (s.Elapsed < timeout);

        return 1;
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