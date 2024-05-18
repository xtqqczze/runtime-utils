using Runner;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

try
{
    await RunAsync(args);
}
catch (Exception ex)
{
    Console.WriteLine(ex);
    File.WriteAllText("crash.txt", ex.ToString());
}

static async Task RunAsync(string[] args)
{
    Console.WriteLine("Starting ...");
    string? jobId = Environment.GetEnvironmentVariable("JOB_ID");

    if (string.IsNullOrEmpty(jobId))
    {
        if (args.Length == 1 &&
            args[0] is string eventPath &&
            File.Exists(eventPath))
        {
            JsonDocument document = JsonDocument.Parse(File.ReadAllText(eventPath));
            string? body = document.RootElement.GetProperty("issue").GetProperty("body").GetString();

            if (body is not null)
            {
                // <!-- RUN_AS_GITHUB_ACTION_{ExternalId} -->
                const string Prefix = "RUN_AS_GITHUB_ACTION_";

                int offset = body.IndexOf(Prefix, StringComparison.Ordinal) + Prefix.Length;
                int endOfId = body.IndexOf(' ', offset);

                jobId = body.Substring(offset, endOfId - offset);
            }
        }

        if (string.IsNullOrEmpty(jobId))
        {
            return;
        }
    }

    var client = new HttpClient
    {
        DefaultRequestVersion = HttpVersion.Version20,
        BaseAddress = new Uri("https://mihubot.xyz/api/RuntimeUtils/Jobs/"),
        Timeout = TimeSpan.FromMinutes(5),
    };

    var request = new HttpRequestMessage(HttpMethod.Get, $"Metadata/{jobId}");

    if (Environment.GetEnvironmentVariable("RUNTIME_UTILS_TOKEN") is { Length: > 0 } authToken)
    {
        request.Headers.Add("X-Runtime-Utils-Token", authToken);
    }

    using var response = await client.SendAsync(request);

    var metadata = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>() ?? throw new Exception("Null response");
    metadata = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);

    string jobType = metadata["JobType"];

    Console.WriteLine($"Obtained the job metadata. Job type: {jobType}");

    JobBase job = jobType switch
    {
        nameof(JitDiffJob) => new JitDiffJob(client, metadata),
        nameof(FuzzLibrariesJob) => new FuzzLibrariesJob(client, metadata),
        var type => throw new NotSupportedException(type),
    };

    await job.RunJobAsync();
}