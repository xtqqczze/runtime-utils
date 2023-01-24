using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;

string? jobId = Environment.GetEnvironmentVariable("JOB_ID");
string? sourceRepository = Environment.GetEnvironmentVariable("JOB_PR_REPO");
string? sourceBranch = Environment.GetEnvironmentVariable("JOB_PR_BRANCH");

Console.WriteLine($"{nameof(jobId)}={jobId}");
Console.WriteLine($"{nameof(sourceRepository)}={sourceRepository}");
Console.WriteLine($"{nameof(sourceBranch)}={sourceBranch}");

if (jobId is null || sourceRepository is null || sourceBranch is null)
{
    return;
}

var job = new Job(jobId, sourceRepository, sourceBranch);
await job.RunJobAsync();

public class Job
{
    private readonly string _jobId;
    private readonly string _sourceRepository;
    private readonly string _sourceBranch;
    private readonly HttpClient _client;
    private readonly Channel<string> _channel;
    private CancellationToken _jobTimeout;

    public Job(string jobId, string sourceRepository, string sourceBranch)
    {
        _jobId = jobId;
        _sourceRepository = sourceRepository;
        _sourceBranch = sourceBranch;

        _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
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
        using var jobCts = new CancellationTokenSource(TimeSpan.FromHours(2));
        _jobTimeout = jobCts.Token;

        Task channelReaderTask = Task.Run(() => ReadChannelAsync());

        await RunJobAsyncCore();

        _channel.Writer.TryComplete();
        await channelReaderTask.WaitAsync(_jobTimeout);
    }

    private async Task RunJobAsyncCore()
    {
        string template = await File.ReadAllTextAsync("script.sh.template");
        string script = template
            .ReplaceLineEndings()
            .Replace("{{SOURCE_REPOSITORY}}", _sourceRepository)
            .Replace("{{SOURCE_BRANCH}}", _sourceBranch);

        await LogAsync($"Using script {script}");

        await File.WriteAllTextAsync("script.sh", script);

        await RunProcessAsync("bash", "-x script.sh");

        string coreLibDiff = await JitAnalyzeAsync("corelib");
        await UploadArtifactAsync("diff-corelib.txt", coreLibDiff);

        string frameworksDiff = await JitAnalyzeAsync("frameworks");
        await UploadArtifactAsync("diff-frameworks.txt", frameworksDiff);

        //await RunProcessAsync("zip", "-r jit-diffs.zip jit-diffs");
        //await UploadArtifactAsync("jit-diffs.zip");
    }

    private async Task LogAsync(string message)
    {
        await _channel.Writer.WriteAsync($"[{DateTime.UtcNow:HH:mm:ss}] {message}", _jobTimeout);
    }

    private async Task ErrorAsync(string message)
    {
        try
        {
            _channel.Writer.TryComplete(new Exception(message));

            using var response = await _client.PostAsJsonAsync(
                $"Logs/{_jobId}",
                new[] { $"ERROR: {message}" },
                _jobTimeout);
        }
        catch { }
    }

    private async Task ReadChannelAsync()
    {
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

                using var response = await _client.PostAsJsonAsync(
                    $"Logs/{_jobId}",
                    messages.ToArray(),
                    _jobTimeout);
            }
        }
        catch (Exception ex)
        {
            await ErrorAsync(ex.ToString());
        }
    }

    private async Task<string> JitAnalyzeAsync(string folder)
    {
        List<string> output = new();

        await RunProcessAsync(
            "bin/jit-analyze",
            $"-b jit-diffs/{folder}/dasmset_1/base -d jit-diffs/{folder}/dasmset_2/base -r -c 100",
            output);

        return string.Join('\n', output);
    }

    private async Task RunProcessAsync(string fileName, string arguments, List<string>? output = null)
    {
        await LogAsync($"Running '{fileName} {arguments}'");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            }
        };

        process.Start();

        await Task.WhenAll(
            Task.Run(() => ReadOutputStreamAsync(process.StandardOutput)),
            Task.Run(() => ReadOutputStreamAsync(process.StandardError)),
            process.WaitForExitAsync(_jobTimeout));

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

                await LogAsync(line);
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
}