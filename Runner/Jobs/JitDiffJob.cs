namespace Runner.Jobs;

internal sealed class JitDiffJob : JobBase
{
    private const string DiffsDirectory = "jit-diffs/frameworks";
    private const string DiffsMainDirectory = $"{DiffsDirectory}/main";
    private const string DiffsPrDirectory = $"{DiffsDirectory}/pr";
    private const string DasmSubdirectory = "dasmset_1/base";

    public JitDiffJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        await ChangeWorkingDirectoryToRamDiskAsync();

        await CloneRuntimeAndSetupToolsAsync();

        await BuildRuntimeAsync();

        await RuntimeHelpers.InstallRuntimeDotnetSdkAsync(this);

        string diffAnalyzeSummary = await CollectFrameworksDiffsAsync();

        await UploadDiffExamplesAsync(diffAnalyzeSummary, regressions: true);
        await UploadDiffExamplesAsync(diffAnalyzeSummary, regressions: false);
    }

    private async Task CloneRuntimeAndSetupToolsAsync()
    {
        Task cloneRuntimeTask = RuntimeHelpers.CloneRuntimeAsync(this);

        Task setupZipAndWgetTask = RunProcessAsync("apt-get", "install -y zip wget", logPrefix: "Setup zip & wget");

        Task setupJitutilsTask = Task.Run(async () =>
        {
            const string LogPrefix = "Setup jitutils";
            await setupZipAndWgetTask;

            string repo = GetArgument("jitutils-repo", "dotnet/jitutils");
            string branch = GetArgument("jitutils-branch", "main");

            await RunProcessAsync("git", $"clone --no-tags --single-branch -b {branch} --progress https://github.com/{repo}.git", logPrefix: LogPrefix);

            if (IsArm)
            {
                const string ToolsLink = "https://raw.githubusercontent.com/MihaZupan/runtime-utils/clang-tools";
                Directory.CreateDirectory("jitutils/bin");
                await RunProcessAsync("wget", $"-O jitutils/bin/clang-format {ToolsLink}/clang-format", logPrefix: LogPrefix);
                await RunProcessAsync("wget", $"-O jitutils/bin/clang-tidy {ToolsLink}/clang-tidy", logPrefix: LogPrefix);
                await RunProcessAsync("chmod", "751 jitutils/bin/clang-format", logPrefix: LogPrefix);
                await RunProcessAsync("chmod", "751 jitutils/bin/clang-tidy", logPrefix: LogPrefix);
            }

            await RunProcessAsync("bash", "bootstrap.sh", logPrefix: LogPrefix, workDir: "jitutils");
        });

        Task createDirectoriesTask = Task.Run(() =>
        {
            Directory.CreateDirectory("artifacts-main");
            Directory.CreateDirectory("artifacts-pr");
            Directory.CreateDirectory("clr-checked-main");
            Directory.CreateDirectory("clr-checked-pr");
            Directory.CreateDirectory("jit-diffs");
            Directory.CreateDirectory(DiffsDirectory);
            Directory.CreateDirectory(DiffsMainDirectory);
            Directory.CreateDirectory(DiffsPrDirectory);
        });

        await createDirectoriesTask;
        await setupJitutilsTask;
        await setupZipAndWgetTask;
        await cloneRuntimeTask;
    }

    private async Task BuildRuntimeAsync()
    {
        await BuildAndCopyRuntimeBranchBitsAsync("main");

        PendingTasks.Enqueue(ZipAndUploadRuntimeBitsAsync("main"));

        await RunProcessAsync("git", "switch pr", workDir: "runtime");

        await BuildAndCopyRuntimeBranchBitsAsync("pr");

        PendingTasks.Enqueue(ZipAndUploadRuntimeBitsAsync("pr"));

        async Task ZipAndUploadRuntimeBitsAsync(string branch)
        {
            await ZipAndUploadArtifactAsync($"build-artifacts-{branch}", $"artifacts-{branch}");
            await ZipAndUploadArtifactAsync($"build-clr-checked-{branch}", $"clr-checked-{branch}");
        }

        async Task BuildAndCopyRuntimeBranchBitsAsync(string branch)
        {
            string arch = IsArm ? "arm64" : "x64";

            await RunProcessAsync("bash", $"build.sh clr+libs -c Release {RuntimeHelpers.LibrariesExtraBuildArgs}", logPrefix: $"{branch} release", workDir: "runtime");

            Task copyReleaseBitsTask = RuntimeHelpers.CopyReleaseArtifactsAsync(this, branch, $"artifacts-{branch}");

            await RunProcessAsync("bash", "build.sh clr.jit -c Checked", logPrefix: $"{branch} checked", workDir: "runtime");
            await RunProcessAsync("cp", $"-r runtime/artifacts/bin/coreclr/linux.{arch}.Checked/. clr-checked-{branch}", logPrefix: $"{branch} checked");

            await copyReleaseBitsTask;
        }
    }

    private async Task<string> CollectFrameworksDiffsAsync()
    {
        Task baselineTask = JitDiffAsync(baseline: true);
        await JitDiffAsync(baseline: false);
        await baselineTask;

        PendingTasks.Enqueue(ZipAndUploadArtifactAsync("jit-diffs-frameworks", DiffsDirectory));

        string diffAnalyzeSummary = await JitAnalyzeAsync();

        PendingTasks.Enqueue(UploadTextArtifactAsync("diff-frameworks.txt", diffAnalyzeSummary));

        return diffAnalyzeSummary;
    }

    private async Task<string> JitAnalyzeAsync()
    {
        List<string> output = [];

        await RunProcessAsync("jitutils/bin/jit-analyze",
            $"-b {DiffsMainDirectory}/{DasmSubdirectory} -d {DiffsPrDirectory}/{DasmSubdirectory} -r -c 100",
            output,
            logPrefix: "jit-analyze",
            checkExitCode: false);

        return string.Join('\n', output);
    }

    private async Task JitDiffAsync(bool baseline)
    {
        string artifactsFolder = baseline ? "artifacts-main" : "artifacts-pr";
        string checkedClrFolder = baseline ? "clr-checked-main" : "clr-checked-pr";
        string outputFolder = baseline ? DiffsMainDirectory : DiffsPrDirectory;

        bool useCctors = !TryGetFlag("nocctors");
        bool useTier0 = TryGetFlag("tier0");

        await LogAsync($"Using cctors for {artifactsFolder}: {useCctors}");
        await LogAsync($"Using tier0 {artifactsFolder}: {useTier0}");

        await RunProcessAsync("jitutils/bin/jit-diff",
            $"diff " +
            (useCctors ? "--cctors " : "") +
            (useTier0 ? "--tier0 " : "") +
            $"--output {outputFolder} --frameworks --pmi " +
            $"--core_root {artifactsFolder} " +
            $"--base {checkedClrFolder}",
            logPrefix: $"jit-diff {(baseline ? "main" : "pr")}");
    }

    private async Task UploadDiffExamplesAsync(string diffAnalyzeSummary, bool regressions)
    {
        var (diffs, noisyDiffsRemoved) = await GetDiffMarkdownAsync(JitDiffUtils.ParseDiffAnalyzeEntries(diffAnalyzeSummary, regressions));

        string changes = GetCommentMarkdown(diffs, GitHubHelpers.CommentLengthLimit, regressions, out bool truncated);

        await LogAsync($"Found {diffs.Length} changes, comment length={changes.Length} for {nameof(regressions)}={regressions}");

        if (changes.Length != 0)
        {
            if (noisyDiffsRemoved)
            {
                changes = $"{changes}\n\nNote: some changes were skipped as they were likely noise.";
            }

            PendingTasks.Enqueue(UploadTextArtifactAsync($"ShortDiffs{(regressions ? "Regressions" : "Improvements")}.md", changes));

            if (truncated)
            {
                changes = GetCommentMarkdown(diffs, GitHubHelpers.GistLengthLimit, regressions, out _);

                PendingTasks.Enqueue(UploadTextArtifactAsync($"LongDiffs{(regressions ? "Regressions" : "Improvements")}.md", changes));
            }
        }
    }

    private async Task<(string[] Diffs, bool NoisyDiffsRemoved)> GetDiffMarkdownAsync((string Description, string DasmFile, string Name)[] diffs)
    {
        if (diffs.Length == 0)
        {
            return (Array.Empty<string>(), false);
        }

        const string MainDasmDirectory = $"{DiffsMainDirectory}/{DasmSubdirectory}";
        const string PrDasmDirectory = $"{DiffsPrDirectory}/{DasmSubdirectory}";

        bool noisyMethodsRemoved = false;
        bool includeKnownNoise = TryGetFlag("includeKnownNoise");
        bool includeRemovedMethod = TryGetFlag("includeRemovedMethodImprovements");
        bool IncludeNewMethod = TryGetFlag("includeNewMethodRegressions");

        var result = await diffs
            .ToAsyncEnumerable()
            .Where(diff => includeRemovedMethod || !IsRemovedMethod(diff.Description))
            .Where(diff => IncludeNewMethod || !IsNewMethod(diff.Description))
            .SelectAwait(async diff =>
            {
                string mainDiffsFile = $"{MainDasmDirectory}/{diff.DasmFile}";
                string prDiffsFile = $"{PrDasmDirectory}/{diff.DasmFile}";

                await LogAsync($"Generating diffs for {diff.Name}");

                StringBuilder sb = new();

                sb.AppendLine("<details>");
                sb.AppendLine($"<summary>{diff.Description} - {diff.Name}</summary>");
                sb.AppendLine();
                sb.AppendLine("```diff");

                using var baseFile = new TempFile("txt");
                using var prFile = new TempFile("txt");

                await File.WriteAllTextAsync(baseFile.Path, await JitDiffUtils.TryGetMethodDumpAsync(mainDiffsFile, diff.Name));
                await File.WriteAllTextAsync(prFile.Path, await JitDiffUtils.TryGetMethodDumpAsync(prDiffsFile, diff.Name));

                List<string> lines = await GitHelper.DiffAsync(this, baseFile.Path, prFile.Path, fullContext: true);

                if (lines.Count == 0)
                {
                    return string.Empty;
                }
                else
                {
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("; ============================================================", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (!includeKnownNoise && LineIsIndicativeOfKnownNoise(line.AsSpan().TrimStart()))
                        {
                            noisyMethodsRemoved = true;
                            return string.Empty;
                        }

                        sb.AppendLine(line);
                    }
                }

                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();

                return sb.ToString();
            })
            .Where(diff => !string.IsNullOrEmpty(diff))
            .Take(20)
            .ToArrayAsync();

        return (result, noisyMethodsRemoved);

        static bool IsRemovedMethod(ReadOnlySpan<char> description) =>
            description.Contains("-100.", StringComparison.Ordinal);

        static bool IsNewMethod(ReadOnlySpan<char> description) =>
            description.Contains("∞ of base", StringComparison.Ordinal) ||
            description.Contains("Infinity of base", StringComparison.Ordinal);

        static bool LineIsIndicativeOfKnownNoise(ReadOnlySpan<char> line)
        {
            if (line.IsEmpty || line[0] is not ('+' or '-'))
            {
                return false;
            }

            return
                line.Contains("CORINFO_HELP_CLASSINIT_SHARED_DYNAMICCLASS", StringComparison.Ordinal) ||
                line.Contains("ProcessorIdCache:RefreshCurrentProcessorId", StringComparison.Ordinal) ||
                line.Contains("Interop+Sys:SchedGetCpu()", StringComparison.Ordinal);
        }
    }

    private static string GetCommentMarkdown(string[] diffs, int lengthLimit, bool regressions, out bool lengthLimitExceeded)
    {
        lengthLimitExceeded = false;

        if (diffs.Length == 0)
        {
            return string.Empty;
        }

        int currentLength = 0;
        bool someChangesSkipped = false;

        List<string> changesToShow = new();

        foreach (var change in diffs)
        {
            if (change.Length > lengthLimit)
            {
                someChangesSkipped = true;
                lengthLimitExceeded = true;
                continue;
            }

            if ((currentLength += change.Length) > lengthLimit)
            {
                lengthLimitExceeded = true;
                break;
            }

            changesToShow.Add(change);
        }

        StringBuilder sb = new();

        sb.AppendLine($"## Top method {(regressions ? "regressions" : "improvements")}");
        sb.AppendLine();

        foreach (string md in changesToShow)
        {
            sb.AppendLine(md);
        }

        sb.AppendLine();

        if (someChangesSkipped)
        {
            sb.AppendLine("Note: some changes were skipped as they were too large to fit into a comment.");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
