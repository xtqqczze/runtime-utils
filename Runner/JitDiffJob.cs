using System.Runtime.InteropServices;

namespace Runner;

internal sealed class JitDiffJob : JobBase
{
    private static bool IsArm => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    private readonly string _originalWorkingDirectory = Environment.CurrentDirectory;

    private string SourceRepo => Metadata["PrRepo"];
    private string SourceBranch => Metadata["PrBranch"];

    public JitDiffJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        await LogAsync($"{nameof(SourceRepo)}={SourceRepo}");
        await LogAsync($"{nameof(SourceBranch)}={SourceBranch}");

        await ChangeWorkingDirectoryToLargestDiskAsync();

        await CloneRuntimeAndSetupToolsAsync();

        await BuildRuntimeAsync();

        await InstallRuntimeDotnetSdkAsync();

        await CollectFrameworksDiffsAsync();
    }

    private async Task ChangeWorkingDirectoryToLargestDiskAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        int availableRamGB = await GetTotalSystemMemoryGBAsync(TimeSpan.FromSeconds(5));

        if (availableRamGB >= 30)
        {
            if (await TryApplyAsync(async () =>
            {
                const string NewWorkDir = "/ramdisk";
                const string LogPrefix = "Prepare RAM disk";

                int ramDiskSize = Math.Min(128, availableRamGB / 4 * 3);
                await RunProcessAsync("mkdir", NewWorkDir, logPrefix: LogPrefix);
                await RunProcessAsync("mount", $"-t tmpfs -o size={ramDiskSize}G tmpfs {NewWorkDir}", logPrefix: LogPrefix);
                return NewWorkDir;
            }))
            {
                return;
            }
        }

        await TryApplyAsync(() =>
        {
            const string NewWorkDir = "/mnt/runner";
            Directory.CreateDirectory(NewWorkDir);
            return Task.FromResult(NewWorkDir);
        });

        async Task<bool> TryApplyAsync(Func<Task<string>> action)
        {
            try
            {
                string newDirectory = await action();
                Environment.CurrentDirectory = newDirectory;

                await LogAsync($"Changed working directory to {newDirectory}");
                return true;
            }
            catch (Exception ex)
            {
                await LogAsync($"Failed to apply new working directory: {ex}");
                return false;
            }
        }
    }

    private async Task CloneRuntimeAndSetupToolsAsync()
    {
        Task cloneRuntimeTask = Task.Run(async () =>
        {
            const string LogPrefix = "Setup runtime";

            string template = await File.ReadAllTextAsync(Path.Combine(_originalWorkingDirectory, "setup-runtime.sh.template"));
            string script = template
                .ReplaceLineEndings()
                .Replace("{{MERGE_BASELINE_BRANCHES}}", GetMergeScript("dependsOn"))
                .Replace("{{MERGE_PR_BRANCHES}}", GetMergeScript("combineWith"));

            await LogAsync($"Using runtime setup script:\n{script}");
            await File.WriteAllTextAsync("setup-runtime.sh", script);
            await RunProcessAsync("bash", "-x setup-runtime.sh", logPrefix: LogPrefix);

            string GetMergeScript(string name)
            {
                int counter = 0;

                List<(string Repo, string Branch)> prList = new(GetPRList(name));

                if (name == "combineWith")
                {
                    prList.Insert(0, (SourceRepo, SourceBranch));
                }

                return string.Join('\n', prList
                    .Select(pr =>
                    {
                        int index = ++counter;
                        string remoteName = $"{name}{index}";

                        return
                            $"git remote add {remoteName} https://github.com/{pr.Repo}\n" +
                            $"git fetch {remoteName} {pr.Branch}\n" +
                            $"git log {remoteName}/{pr.Branch} -1\n" +
                            $"git merge --no-edit {remoteName}/{pr.Branch}\n" +
                            $"git log -1\n";
                    }));
            };
        });

        Task setupZipAndWgetTask = Task.Run(async () =>
        {
            const string LogPrefix = "Setup zip & wget";
            await RunProcessAsync("apt-get", "install -y zip wget", logPrefix: LogPrefix);
        });

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
        });

        await createDirectoriesTask;
        await setupJitutilsTask;
        await setupZipAndWgetTask;
        await cloneRuntimeTask;
    }

    private (string Repo, string Branch)[] GetPRList(string name)
    {
        if (Metadata.TryGetValue(name, out string? value))
        {
            return value.Split(',').Select(pr =>
            {
                string[] parts = pr.Split(';');
                return (parts[0], parts[1]);
            }).ToArray();
        }

        return Array.Empty<(string, string)>();
    }

    private async Task BuildRuntimeAsync()
    {
        await BuildAndCopyRuntimeBranchBitsAsync("main");
        Task uploadMainBitsTask = ZipAndUploadRuntimeBitsAsync("main");

        await RunProcessAsync("git", "switch pr", workDir: "runtime");

        await BuildAndCopyRuntimeBranchBitsAsync("pr");
        await ZipAndUploadRuntimeBitsAsync("pr");

        await uploadMainBitsTask;

        async Task ZipAndUploadRuntimeBitsAsync(string branch)
        {
            await ZipAndUploadArtifactAsync($"build-artifacts-{branch}", $"artifacts-{branch}");
            await ZipAndUploadArtifactAsync($"build-clr-checked-{branch}", $"clr-checked-{branch}");
        }

        async Task BuildAndCopyRuntimeBranchBitsAsync(string branch)
        {
            string arch = IsArm ? "arm64" : "x64";

            await RunProcessAsync("bash", "build.sh clr+libs -c Release -p:RunAnalyzers=false -p:ApiCompatValidateAssemblies=false", logPrefix: $"{branch} release", workDir: "runtime");

            Task copyReleaseBitsTask = Task.Run(async () =>
            {
                await RunProcessAsync("cp", $"-r runtime/artifacts/bin/coreclr/linux.{arch}.Release/. artifacts-{branch}", logPrefix: $"{branch} release");

                const string BaseDirectory = "runtime/artifacts/bin/runtime";

                string folder = Directory.GetDirectories(BaseDirectory)
                    .Select(f => Path.GetRelativePath(BaseDirectory, f))
                    .Where(f => f.StartsWith("net", StringComparison.OrdinalIgnoreCase))
                    .Where(f => f.Contains("Release", StringComparison.OrdinalIgnoreCase))
                    .Where(f => f.Contains("linux", StringComparison.OrdinalIgnoreCase))
                    .Where(f => f.Contains(arch, StringComparison.OrdinalIgnoreCase))
                    .Single();

                await RunProcessAsync("cp", $"-r {BaseDirectory}/{folder}/. artifacts-{branch}", logPrefix: $"{branch} release");
            });

            await RunProcessAsync("bash", "build.sh clr.jit -c Checked", logPrefix: $"{branch} checked", workDir: "runtime");
            await RunProcessAsync("cp", $"-r runtime/artifacts/bin/coreclr/linux.{arch}.Checked/. clr-checked-{branch}", logPrefix: $"{branch} checked");

            await copyReleaseBitsTask;
        }
    }

    private async Task InstallRuntimeDotnetSdkAsync()
    {
        await RunProcessAsync("wget", "https://dot.net/v1/dotnet-install.sh");
        await RunProcessAsync("bash", "dotnet-install.sh --jsonfile runtime/global.json --install-dir /usr/lib/dotnet");
    }

    private async Task CollectFrameworksDiffsAsync()
    {
        bool runSequential =
            TryGetFlag("force-frameworks-sequential") ? true :
            TryGetFlag("force-frameworks-parallel") ? false :
            GetTotalSystemMemoryGB() < 12;

        await JitDiffAsync(baseline: true, sequential: runSequential);
        await JitDiffAsync(baseline: false, sequential: runSequential);

        Task uploadFrameworksDiffsTask = ZipAndUploadArtifactAsync("jit-diffs-frameworks", "jit-diffs/frameworks");

        string frameworksDiff = await JitAnalyzeAsync();
        await UploadTextArtifactAsync("diff-frameworks.txt", frameworksDiff);

        await uploadFrameworksDiffsTask;
    }

    private async Task<string> JitAnalyzeAsync()
    {
        List<string> output = new();

        await RunProcessAsync("jitutils/bin/jit-analyze",
            "-b jit-diffs/frameworks/dasmset_1/base -d jit-diffs/frameworks/dasmset_2/base -r -c 100",
            output,
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
            $"--output jit-diffs/frameworks --frameworks --pmi " +
            $"--core_root {artifactsFolder} " +
            $"--base {checkedClrFolder}");
    }
}
