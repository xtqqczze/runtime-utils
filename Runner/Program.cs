﻿using Runner;
using System.Net;
using System.Net.Http.Json;

string? jobId = Environment.GetEnvironmentVariable("JOB_ID");

Console.WriteLine($"{nameof(jobId)}={jobId}");

if (jobId is null)
{
    return;
}

var client = new HttpClient
{
    DefaultRequestVersion = HttpVersion.Version20,
    BaseAddress = new Uri("https://mihubot.xyz/api/RuntimeUtils/Jobs/"),
    Timeout = TimeSpan.FromMinutes(5),
};

var metadata = await client.GetFromJsonAsync<Dictionary<string, string>>($"Metadata/{jobId}") ?? throw new Exception("Null response");
metadata = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);

JobBase job = metadata["JobType"] switch
{
    nameof(JitDiffJob) => new JitDiffJob(client, metadata),
    nameof(FuzzLibrariesJob) => new JitDiffJob(client, metadata),
    var type => throw new NotSupportedException(type),
};

await job.RunJobAsync();
