using System.Text.RegularExpressions;
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
        bool uploadCoreruns = TryGetFlag("UploadCoreruns");

        await BuildAndCopyRuntimeBranchBitsAsync("main");

        if (uploadCoreruns)
        {
            PendingTasks.Enqueue(ZipAndUploadArtifactAsync("build-artifacts-main", "artifacts-main"));
        }

        await RunProcessAsync("git", "switch pr", workDir: "runtime");

        await BuildAndCopyRuntimeBranchBitsAsync("pr");

        if (uploadCoreruns)
        {
            PendingTasks.Enqueue(ZipAndUploadArtifactAsync("build-artifacts-pr", "artifacts-pr"));
        }

        async Task BuildAndCopyRuntimeBranchBitsAsync(string branch)
        {
            string arch = IsArm ? "arm64" : "x64";

            await RunProcessAsync("bash", $"build.sh clr+libs -c Release {RuntimeHelpers.LibrariesExtraBuildArgs}", logPrefix: $"{branch} release", workDir: "runtime");

            await RuntimeHelpers.CopyReleaseArtifactsAsync(this, branch, $"artifacts-{branch}");
        }
    }

    private async Task RunBenchmarksAsync()
    {
        const string HiddenColumns = "Job StdDev RatioSD Median Min Max OutlierMode MemoryRandomization";

        string filter = FilterNameRegex().Match(CustomArguments).Groups[1].Value;
        filter = filter.Trim().Trim('`').Trim();

        // "version": "9.0.100-preview.5.24307.3",
        char dotnetVersion = File.ReadAllLines("runtime/global.json")
            .First(line => line.Contains("version", StringComparison.OrdinalIgnoreCase))
            .Split(':')[1].TrimStart(' ', '"')[0];

        string corerunMain = Path.GetFullPath("artifacts-main/corerun");
        string corerunPr = Path.GetFullPath("artifacts-pr/corerun");

        string? artifactsDir = null;

        await RunProcessAsync("dotnet",
            $"run -c Release --framework net{dotnetVersion}.0 -- --filter {filter} -h {HiddenColumns} --corerun {corerunMain} {corerunPr}",
            workDir: "performance/src/benchmarks/micro",
            processLogs: line =>
            {
                // Example:
                // ramdisk/performance/artifacts/bin/MicroBenchmarks/Release/net9.0/BenchmarkDotNet.Artifacts/results/TestName-report-github.md
                // we want performance/artifacts/bin/MicroBenchmarks/Release/net9.0/BenchmarkDotNet.Artifacts
                if (artifactsDir is null &&
                    line.AsSpan().TrimEnd().EndsWith("-report-github.md", StringComparison.Ordinal) &&
                    Path.GetDirectoryName(Path.GetDirectoryName(line.AsSpan().Trim())).TrimEnd(['/', '\\']).ToString() is { } dir &&
                    dir.EndsWith("BenchmarkDotNet.Artifacts", StringComparison.Ordinal))
                {
                    const string PerformanceDir = "/performance/";

                    if (dir.Contains(PerformanceDir, StringComparison.Ordinal))
                    {
                        dir = dir.Substring(dir.IndexOf(PerformanceDir, StringComparison.Ordinal) + 1);
                    }

                    artifactsDir = dir;
                }

                // ** Remained 420 (74.5 %) benchmark(s) to run. Estimated finish 2024-06-20 2:54 (0h 40m from now) **
                if (line.Contains("benchmark(s) to run. Estimated finish", StringComparison.Ordinal) &&
                    BdnProgressSummaryRegex().Match(line) is { Success: true } match)
                {
                    LastProgressSummary = $"{match.Groups[1].ValueSpan} ({match.Groups[2].ValueSpan} %) benchmarks remain. Estimated time: {match.Groups[3].ValueSpan}";
                }

                return line;
            });

        if (string.IsNullOrEmpty(artifactsDir))
        {
            throw new Exception("Couldn't find the artifacts directory");
        }

        await ZipAndUploadArtifactAsync("BDN_Artifacts", Path.GetFullPath(artifactsDir), Path.GetDirectoryName(artifactsDir));

        List<string> results = new();

        foreach (var resultsMd in Directory.GetFiles(artifactsDir, "*-report-github.md", SearchOption.AllDirectories))
        {
            await LogAsync($"Reading {resultsMd} ...");

            StringBuilder result = new();

            string friendlyName = Path.GetFileName(resultsMd);
            friendlyName = friendlyName.Substring(0, friendlyName.Length - "-report-github.md".Length);

            result.AppendLine("<details>");
            result.AppendLine($"<summary>{friendlyName}</summary>");
            result.AppendLine();

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

                line = line.Replace("/artifacts-main/corerun", "Main");
                line = line.Replace("/artifacts-pr/corerun", "PR");

                result.AppendLine(line);
            }

            result.AppendLine();
            result.AppendLine("</details>");

            results.Add(result.ToString());
        }

        string combinedMarkdown = string.Join("\n\n", results);

        await UploadTextArtifactAsync("results.md", combinedMarkdown);
    }

    [GeneratedRegex(@"^benchmark ([^ ]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FilterNameRegex();

    // ** Remained 420 (74.5 %) benchmark(s) to run. Estimated finish 2024-06-20 2:54 (0h 40m from now) **
    // 420    74.5    0h 40m
    [GeneratedRegex(@"Remained (\d+) \((.*?) %\).*?\(([\dhms ]+) from", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BdnProgressSummaryRegex();
}
