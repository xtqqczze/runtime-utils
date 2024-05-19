using System.Text.RegularExpressions;

namespace Runner;

internal sealed partial class FuzzLibrariesJob : JobBase
{
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

            call .\build.cmd clr+libs+packs+host -rc Checked -c Debug

            cd src\libraries\Fuzzing\DotnetFuzzing
            ..\..\..\..\.dotnet\dotnet publish -o publish
            ..\..\..\..\.dotnet\dotnet tool install --tool-path . SharpFuzz.CommandLine
            publish\DotnetFuzzing.exe prepare-onefuzz deployment
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

        int failureStackUploaded = 0;

        await Parallel.ForEachAsync(Enumerable.Range(1, parallelism), async (i, _) =>
        {
            List<string> output = new();
            string number = i.ToString().PadLeft(parallelism.ToString().Length, '0');
            string artifactPath = $"{ArtifactPathPrefix}{number}";

            try
            {
                await RunProcessAsync(
                    $"{fuzzerDirectory}/local-run.bat",
                    $"-timeout=60 -max_total_time=3600 {InputsDirectory} -exact_artifact_path={artifactPath} -print_final_stats=1",
                    output,
                    $"Fuzzer {number}",
                    cancellationToken: failureCts.Token);
            }
            catch (Exception ex)
            {
                await LogAsync($"Fuzzer {i} failed: {ex.Message}");

                if (ex is not OperationCanceledException && !failureCts.IsCancellationRequested)
                {
                    string[] stack = output
                        .AsEnumerable()
                        .Reverse()
                        .TakeWhile(line => !(line.Contains("cov: ", StringComparison.Ordinal) && line.Contains("exec/s: ", StringComparison.Ordinal)))
                        .Reverse()
                        .ToArray();

                    if (stack.Length is > 1 and < 100 &&
                        stack.Any(s => s.Contains("at DotnetFuzzing.Fuzzers", StringComparison.OrdinalIgnoreCase)) &&
                        stack.Any(s => s.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) &&
                        File.Exists(artifactPath) &&
                        Interlocked.Exchange(ref failureStackUploaded, 1) == 0)
                    {
                        await UploadTextArtifactAsync("stack.txt", string.Join('\n', stack));
                        await UploadArtifactAsync(artifactPath, $"{fuzzerName}-input.bin");
                    }
                }

                failureCts.Cancel();
            }
        });

        if (Directory.EnumerateFiles(InputsDirectory).Any())
        {
            await ZipAndUploadArtifactAsync("inputs", InputsDirectory);
        }
    }

    [GeneratedRegex("^fuzz ?([a-z]*)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FuzzerNameRegex();
}
