namespace Runner;

internal sealed class JitDiffJob : JobBase
{
    public JitDiffJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        await ChangeWorkingDirectoryToRamDiskAsync();

        await CloneRuntimeAndSetupToolsAsync();

        await BuildRuntimeAsync();

        await RuntimeHelpers.InstallRuntimeDotnetSdkAsync(this);

        await CollectFrameworksDiffsAsync();
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
            Directory.CreateDirectory("jit-diffs/frameworks");
            Directory.CreateDirectory("jit-diffs/frameworks/main");
            Directory.CreateDirectory("jit-diffs/frameworks/pr");
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

    private async Task CollectFrameworksDiffsAsync()
    {
        bool runSequential =
            TryGetFlag("force-frameworks-sequential") ? true :
            TryGetFlag("force-frameworks-parallel") ? false :
            GetTotalSystemMemoryGB() < 12;

        Task baselineTask = JitDiffAsync(baseline: true, sequential: runSequential);
        await JitDiffAsync(baseline: false, sequential: runSequential);
        await baselineTask;

        PendingTasks.Enqueue(ZipAndUploadArtifactAsync("jit-diffs-frameworks", "jit-diffs/frameworks"));

        string frameworksDiff = await JitAnalyzeAsync();

        PendingTasks.Enqueue(UploadTextArtifactAsync("diff-frameworks.txt", frameworksDiff));
    }

    private async Task<string> JitAnalyzeAsync()
    {
        List<string> output = new();

        await RunProcessAsync("jitutils/bin/jit-analyze",
            "-b jit-diffs/frameworks/main/dasmset_1/base -d jit-diffs/frameworks/pr/dasmset_1/base -r -c 100",
            output,
            logPrefix: "jit-analyze",
            checkExitCode: false);

        return string.Join('\n', output);
    }

    private async Task JitDiffAsync(bool baseline, bool sequential = false)
    {
        string artifactsFolder = baseline ? "artifacts-main" : "artifacts-pr";
        string checkedClrFolder = baseline ? "clr-checked-main" : "clr-checked-pr";

        bool useCctors = !TryGetFlag("nocctors");
        bool useTier0 = TryGetFlag("tier0");

        await LogAsync($"Using cctors: {useCctors}");
        await LogAsync($"Using tier0: {useTier0}");

        await RunProcessAsync("jitutils/bin/jit-diff",
            $"diff " +
            (sequential ? "--sequential " : "") +
            (useCctors ? "--cctors " : "") +
            (useTier0 ? "--tier0 " : "") +
            $"--output jit-diffs/frameworks/{(baseline ? "main" : "pr")} --frameworks --pmi " +
            $"--core_root {artifactsFolder} " +
            $"--base {checkedClrFolder}",
            logPrefix: $"jit-diff {(baseline ? "main" : "pr")}");
    }
}
