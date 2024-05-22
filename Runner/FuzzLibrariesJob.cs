using System.Text.RegularExpressions;

namespace Runner;

internal sealed partial class FuzzLibrariesJob : JobBase
{
    private const string DeploymentPath = "runtime/src/libraries/Fuzzing/DotnetFuzzing/deployment";

    private string SourceRepo => Metadata["PrRepo"];
    private string SourceBranch => Metadata["PrBranch"];

    public FuzzLibrariesJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new Exception("This job is only supported on Windows");
        }

        Match match = FuzzerNameRegex().Match(CustomArguments);
        if (!match.Success)
        {
            throw new Exception("Invalid arguments. Expected 'fuzz <fuzzer name>'");
        }

        string fuzzerNamePattern = match.Groups[1].Value;
        if (string.IsNullOrEmpty(fuzzerNamePattern))
        {
            throw new Exception("Invalid fuzzer name. Expected something like 'fuzz HttpHeaders'");
        }

        await CloneRuntimeAndPrepareFuzzerAsync();

        await RunFuzzersAsync(fuzzerNamePattern);
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

            call .\build.cmd clr+libs+packs+host -rc Checked -c Debug

            cd src\libraries\Fuzzing\DotnetFuzzing
            ..\..\..\..\.dotnet\dotnet publish -o publish
            ..\..\..\..\.dotnet\dotnet tool install --tool-path . SharpFuzz.CommandLine
            publish\DotnetFuzzing.exe prepare-onefuzz deployment
            """);

        await RunProcessAsync(ScriptName, string.Empty);
    }

    private async Task RunFuzzersAsync(string fuzzerNamePattern)
    {
        string[] availableFuzzers = Directory.GetDirectories(DeploymentPath)
            .Select(Path.GetFileName)
            .ToArray()!;

        await LogAsync($"Available fuzzers: {string.Join(", ", availableFuzzers)}");

        var matchingFuzzers = availableFuzzers
            .Where(f => f.Contains(fuzzerNamePattern, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matchingFuzzers.Length == 0)
        {
            try
            {
                var pattern = new Regex(fuzzerNamePattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                matchingFuzzers = availableFuzzers
                    .Where(f => pattern.IsMatch(f))
                    .ToArray();
            }
            catch { }
        }

        if (matchingFuzzers.Length == 0)
        {
            throw new Exception($"Fuzzer '{fuzzerNamePattern}' not found. Available fuzzers: {string.Join(", ", availableFuzzers)}");
        }

        await LogAsync($"Matched: {string.Join(", ", matchingFuzzers)}");

        int durationSeconds = Math.Min(3600, 4 * 3600 / matchingFuzzers.Length);

        foreach (string fuzzerName in matchingFuzzers)
        {
            if (!await RunFuzzerAsync(fuzzerName, durationSeconds))
            {
                await LogAsync($"{fuzzerName} failed. Skipping any other fuzzers");
                break;
            }
        }
    }

    private async Task<bool> RunFuzzerAsync(string fuzzerName, int durationSeconds)
    {
        string nameWithoutFuzzerSuffix = fuzzerName.EndsWith("Fuzzer", StringComparison.OrdinalIgnoreCase)
            ? fuzzerName.Substring(0, fuzzerName.Length - "Fuzzer".Length)
            : fuzzerName;

        string fuzzerDirectory = $"{DeploymentPath}/{fuzzerName}";
        string inputsDirectory = $"{fuzzerName}-inputs";

        Directory.CreateDirectory(inputsDirectory);

        string artifactPathPrefix = $"{fuzzerName}-artifact-";

        int parallelism = Environment.ProcessorCount;

        await LogAsync($"Starting {parallelism} parallel fuzzer instances");

        using CancellationTokenSource failureCts = new();

        int failureStackUploaded = 0;

        await Parallel.ForEachAsync(Enumerable.Range(1, parallelism), async (i, _) =>
        {
            List<string> output = [];
            string number = i.ToString().PadLeft(parallelism.ToString().Length, '0');
            string artifactPath = $"{artifactPathPrefix}{number}";

            try
            {
                await RunProcessAsync(
                    $"{fuzzerDirectory}/local-run.bat",
                    $"-timeout=60 -max_total_time={durationSeconds} {inputsDirectory} -exact_artifact_path={artifactPath} -print_final_stats=1",
                    output,
                    $"{nameWithoutFuzzerSuffix} {number}",
                    cancellationToken: failureCts.Token);
            }
            catch (Exception ex)
            {
                await LogAsync($"Fuzzer {i} failed: {ex.Message}");

                if (ex is not OperationCanceledException &&
                    !failureCts.IsCancellationRequested &&
                    File.Exists(artifactPath))
                {
                    string[] stack = output
                        .AsEnumerable()
                        .Reverse()
                        .TakeWhile(line => !(line.Contains("cov: ", StringComparison.Ordinal) && line.Contains("exec/s: ", StringComparison.Ordinal)))
                        .SkipWhile(line => string.IsNullOrWhiteSpace(line) || line.StartsWith("stat::", StringComparison.Ordinal))
                        .Reverse()
                        .ToArray();

                    if (stack.Length > 0 && Interlocked.Exchange(ref failureStackUploaded, 1) == 0)
                    {
                        await UploadTextArtifactAsync("stack.txt", string.Join('\n', stack));
                        await UploadArtifactAsync(artifactPath, $"{fuzzerName}-input.bin");
                    }
                }

                failureCts.Cancel();
            }
        });

        if (Directory.EnumerateFiles(inputsDirectory).Any())
        {
            await ZipAndUploadArtifactAsync($"{fuzzerName}-inputs", inputsDirectory);
        }

        return failureStackUploaded == 0;
    }

    [GeneratedRegex("^fuzz ?([a-z]*)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FuzzerNameRegex();
}
