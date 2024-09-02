namespace Runner.Helpers;

internal static class RuntimeHelpers
{
    private static void AssertIsLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }
    }

    public static string LibrariesExtraBuildArgs => OperatingSystem.IsLinux()
        ? "-p:RunAnalyzers=false -p:ApiCompatValidateAssemblies=false"
        : "/p:RunAnalyzers=false /p:ApiCompatValidateAssemblies=false";

    public static async Task CloneRuntimeAsync(JobBase job)
    {
        const string LogPrefix = "Setup runtime";

        if (OperatingSystem.IsLinux())
        {
            string script = UpdateMergePlaceholders(
                """
                set -e

                git clone --no-tags --single-branch --progress https://github.com/dotnet/runtime runtime
                cd runtime
                git log -1
                chmod 777 build.sh
                git config --global user.email build@build.foo
                git config --global user.name build

                {{MERGE_BASELINE_BRANCHES}}

                git switch -c pr

                {{MERGE_PR_BRANCHES}}

                git switch main

                eng/install-native-dependencies.sh linux
                """);

            await job.LogAsync($"Using runtime setup script:\n{script}");
            await File.WriteAllTextAsync("setup-runtime.sh", script);
            await job.RunProcessAsync("bash", "-x setup-runtime.sh", logPrefix: LogPrefix);
        }
        else
        {
            string script = UpdateMergePlaceholders(
                """
                git config --system core.longpaths true
                git clone --no-tags --single-branch --progress https://github.com/dotnet/runtime runtime
                cd runtime
                git log -1
                git config --global user.email build@build.foo
                git config --global user.name build

                {{MERGE_BASELINE_BRANCHES}}

                git switch -c pr

                {{MERGE_PR_BRANCHES}}

                git switch main
                """);

            await job.LogAsync($"Using runtime setup script:\n{script}");
            await File.WriteAllTextAsync("clone-runtime.bat", script);
            await job.RunProcessAsync("clone-runtime.bat", string.Empty, logPrefix: LogPrefix);
        }

        string UpdateMergePlaceholders(string template)
        {
            return template
                .ReplaceLineEndings()
                .Replace("{{MERGE_BASELINE_BRANCHES}}", GetMergeScript("dependsOn"), StringComparison.Ordinal)
                .Replace("{{MERGE_PR_BRANCHES}}", GetMergeScript("combineWith"), StringComparison.Ordinal);
        }

        string GetMergeScript(string name)
        {
            int counter = 0;

            List<(string Repo, string Branch)> prList = new(GetPRList(job, name));

            if (name == "combineWith")
            {
                prList.Insert(0, (job.SourceRepo, job.SourceBranch));
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

        static (string Repo, string Branch)[] GetPRList(JobBase job, string name)
        {
            if (job.Metadata.TryGetValue(name, out string? value))
            {
                return value.Split(',').Select(pr =>
                {
                    string[] parts = pr.Split(';');
                    return (parts[0], parts[1]);
                }).ToArray();
            }

            return [];
        }
    }

    public static async Task InstallRuntimeDotnetSdkAsync(JobBase job)
    {
        AssertIsLinux();

        await job.RunProcessAsync("wget", "https://dot.net/v1/dotnet-install.sh");
        await job.RunProcessAsync("bash", "dotnet-install.sh --jsonfile runtime/global.json --install-dir /usr/lib/dotnet");
    }

    public static async Task CopyReleaseArtifactsAsync(JobBase job, string branch, string destination)
    {
        AssertIsLinux();

        string logPrefix = $"{branch} release";

        string arch = JobBase.IsArm ? "arm64" : "x64";

        await job.RunProcessAsync("cp", $"-r runtime/artifacts/bin/coreclr/linux.{arch}.Release/. {destination}", logPrefix: logPrefix);

        const string BaseDirectory = "runtime/artifacts/bin/runtime";

        string folder = Directory.GetDirectories(BaseDirectory)
            .Select(f => Path.GetRelativePath(BaseDirectory, f))
            .Where(f => f.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Contains("Release", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Contains("linux", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Contains(arch, StringComparison.OrdinalIgnoreCase))
            .Single();

        await job.RunProcessAsync("cp", $"-r {BaseDirectory}/{folder}/. {destination}", logPrefix: logPrefix);
    }
}
