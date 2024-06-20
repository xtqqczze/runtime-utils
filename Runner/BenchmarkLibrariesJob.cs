﻿using System.Text.RegularExpressions;
using System.Text;

namespace Runner;

internal sealed partial class BenchmarkLibrariesJob : JobBase
{
    public BenchmarkLibrariesJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        await ChangeWorkingDirectoryToRamDiskAsync();

        await CloneRuntimeAndPerformanceAndSetupToolsAsync();

        await BuildRuntimeAsync();

        await RuntimeHelpers.InstallRuntimeDotnetSdkAsync(this);

        await WaitForPendingTasksAsync();

        await RunBenchmarksAsync();
    }

    private async Task CloneRuntimeAndPerformanceAndSetupToolsAsync()
    {
        Task cloneRuntimeTask = RuntimeHelpers.CloneRuntimeAsync(this);

        Task clonePerformanceTask = RunProcessAsync("git", "clone --no-tags --depth=1 --progress https://github.com/dotnet/performance performance", logPrefix: "Clone performance");

        Task setupZipAndWgetTask = RunProcessAsync("apt-get", "install -y zip wget", logPrefix: "Setup zip & wget");

        Directory.CreateDirectory("artifacts-main");
        Directory.CreateDirectory("artifacts-pr");

        await setupZipAndWgetTask;
        await clonePerformanceTask;
        await cloneRuntimeTask;
    }

    private async Task BuildRuntimeAsync()
    {
        await BuildAndCopyRuntimeBranchBitsAsync("main");

        PendingTasks.Enqueue(ZipAndUploadArtifactAsync("build-artifacts-main", "artifacts-main"));

        await RunProcessAsync("git", "switch pr", workDir: "runtime");

        await BuildAndCopyRuntimeBranchBitsAsync("pr");

        PendingTasks.Enqueue(ZipAndUploadArtifactAsync("build-artifacts-pr", "artifacts-pr"));

        async Task BuildAndCopyRuntimeBranchBitsAsync(string branch)
        {
            string arch = IsArm ? "arm64" : "x64";

            await RunProcessAsync("bash", $"build.sh clr+libs -c Release {RuntimeHelpers.LibrariesExtraBuildArgs}", logPrefix: $"{branch} release", workDir: "runtime");

            await RuntimeHelpers.CopyReleaseArtifactsAsync(this, branch, $"artifacts-{branch}");
        }
    }

    private async Task RunBenchmarksAsync()
    {
        const string HiddenColumns = "Job StdDev RatioSD Median Min Max";

        string filter = FilterNameRegex().Match(CustomArguments).Groups[1].Value;

        // "version": "9.0.100-preview.5.24307.3",
        char dotnetVersion = File.ReadAllLines("runtime/global.json")
            .First(line => line.Contains("version", StringComparison.OrdinalIgnoreCase))
            .Split(':')[1].TrimStart(' ', '"')[0];

        string? artifactsDir = null;

        await RunProcessAsync("dotnet",
            $"run -c Release --framework net{dotnetVersion}.0 -- --filter {filter} -h {HiddenColumns} --corerun artifacts-main/corerun artifacts-pr/corerun",
            workDir: "performance/src/benchmarks/micro",
            processLogs: line =>
            {
                // Example:
                // /somePath/BenchmarkDotNet.Artifacts/results/SomeTestName-report-github.md
                // we want /somePath/BenchmarkDotNet.Artifacts
                if (artifactsDir is null &&
                    line.AsSpan().TrimEnd().EndsWith("-report-github.md", StringComparison.Ordinal) &&
                    Path.GetDirectoryName(Path.GetDirectoryName(line.AsSpan().Trim())).TrimEnd(['/', '\\']).ToString() is { } dir &&
                    dir.EndsWith("BenchmarkDotNet.Artifacts", StringComparison.Ordinal))
                {
                    artifactsDir = dir;
                }

                return line;
            });

        if (string.IsNullOrEmpty(artifactsDir))
        {
            throw new Exception("Couldn't find the artifacts directory");
        }

        await ZipAndUploadArtifactAsync("BDN_Artifacts.zip", artifactsDir);

        StringBuilder results = new();
        foreach (var resultsMd in Directory.GetFiles(artifactsDir, "*-report-github.md", SearchOption.AllDirectories))
        {
            await LogAsync($"Reading {resultsMd} ...");

            foreach (string rawLine in await File.ReadAllLinesAsync(resultsMd))
            {
                string line = rawLine.Trim();

                if (string.IsNullOrEmpty(line) ||
                    line.StartsWith(".NET SDK ", StringComparison.Ordinal) ||
                    line.StartsWith("[Host]", StringComparison.Ordinal) ||
                    line.StartsWith("PowerPlanMode", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("MinIterationCount", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (line.StartsWith("Job-"))
                    line = "  " + line;

                // Workaround for BDN's bug: https://github.com/dotnet/BenchmarkDotNet/issues/2545
                if (line.EndsWith(":|-"))
                    line = line.Remove(line.Length - 1);

                line = line.Replace("artifacts-main/corerun", "Main");
                line = line.Replace("artifacts-pr/corerun", "PR");

                results.AppendLine(line);
            }

            results.AppendLine();
        }

        await UploadTextArtifactAsync("results.md", results.ToString());
    }

    [GeneratedRegex(@"^benchmark ([^ ]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FilterNameRegex();
}
