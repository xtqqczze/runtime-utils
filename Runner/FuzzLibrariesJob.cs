using System.Text.RegularExpressions;

namespace Runner;

internal sealed class FuzzLibrariesJob : JobBase
{
    private static readonly Regex _fuzzMatchRegex = new("^fuzz ?([a-z]*)", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private string SourceRepo => Metadata["PrRepo"];
    private string SourceBranch => Metadata["PrBranch"];

    public FuzzLibrariesJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new Exception("This job is only supported on Windows");
        }

        Match match = _fuzzMatchRegex.Match(CustomArguments);
        if (!match.Success)
        {
            throw new Exception("Invalid arguments. Expected 'fuzz <fuzzer name>'");
        }

        string fuzzerName = match.Groups[1].Value;
        if (fuzzerName.Length < 7)
        {
            throw new Exception("Invalid fuzzer name. Expected something like 'fuzz HttpHeadersFuzzer'");
        }

        await CloneRuntimeAndPrepareFuzzerAsync();

        await RunFuzzerAsync(fuzzerName);
    }

    private async Task CloneRuntimeAndPrepareFuzzerAsync()
    {
        const string ScriptName = "clone-build-runtime.bat";

        File.WriteAllText(ScriptName,
            $$"""
            git config --system core.longpaths true
            git clone --progress https://github.com/dotnet/runtime runtime
            cd runtime
            git log -1
            git config --global user.email build@build.foo
            git config --global user.name build
            git remote add pr https://github.com/{{SourceRepo}}
            git fetch pr {{SourceBranch}}
            git log pr/{{SourceBranch}} -1
            git merge --no-edit pr/{{SourceBranch}}

            ./build.cmd clr+libs+packs+host -rc Checked -c Debug

            cd src/libraries/Fuzzing/DotnetFuzzing
            ../../../../.dotnet/dotnet publish -o publish
            ../../../../.dotnet/dotnet tool install --tool-path . SharpFuzz.CommandLine
            publish/DotnetFuzzing.exe prepare-onefuzz deployment
            """);

        await RunProcessAsync(ScriptName, string.Empty);
    }

    private async Task RunFuzzerAsync(string fuzzerName)
    {
        const string DeploymentPath = "runtime/src/libraries/Fuzzing/DotnetFuzzing/deployment";

        string[] availableFuzzers = Directory.GetDirectories(DeploymentPath)
            .Select(Path.GetFileName)
            .ToArray()!;

        await LogAsync($"Available fuzzers: {string.Join(", ", availableFuzzers)}");

        var matchingFuzzers = availableFuzzers
            .Where(f => f.Contains(fuzzerName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matchingFuzzers.Length == 0)
        {
            throw new Exception($"Fuzzer '{fuzzerName}' not found. Available fuzzers: {string.Join(", ", availableFuzzers)}");
        }

        if (matchingFuzzers.Length > 1)
        {
            throw new Exception($"'{fuzzerName}' matches multiple fuzzers: {string.Join(", ", matchingFuzzers)}");
        }

        fuzzerName = matchingFuzzers[0];

        string fuzzerDirectory = $"{DeploymentPath}/{fuzzerName}";

        const string InputsDirectory = "Inputs";
        Directory.CreateDirectory(InputsDirectory);

        const string ArtifactPathPrefix = "fuzz-artifact-";

        int parallelism = Math.Max(1, Environment.ProcessorCount / 2);

        await LogAsync($"Starting {parallelism} parallel fuzzer instances");

        using CancellationTokenSource failureCts = new();

        Task fuzzersTask = Parallel.ForEachAsync(Enumerable.Range(1, parallelism), async (i, _) =>
        {
            try
            {
                await RunProcessAsync(
                    $"{fuzzerDirectory}/local-run.bat",
                    $"-timeout=60 -max_total_time=3600 {InputsDirectory} -exact_artifact_path={ArtifactPathPrefix}{i} -print_final_stats=1",
                    logPrefix: $"Fuzzer {i}",
                    cancellationToken: failureCts.Token);
            }
            catch (Exception ex)
            {
                await LogAsync($"Fuzzer {i} failed: {ex.Message}");
                failureCts.Cancel();
            }
        });

        try
        {
            await fuzzersTask;
        }
        catch when (!JobTimeout.IsCancellationRequested) { }

        string[] artifacts = Directory.GetFiles(Environment.CurrentDirectory)
            .Where(d => Path.GetFileName(d).StartsWith(ArtifactPathPrefix))
            .ToArray();

        foreach (string artifact in artifacts)
        {
            await UploadArtifactAsync(artifact);
        }
    }
}
