namespace Runner;

internal sealed class RebaseJob : JobBase
{
    private string SourceRepo => Metadata["PrRepo"];
    private string SourceBranch => Metadata["PrBranch"];

    public RebaseJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        const string ScriptName = "clone-rebase.bat";

        string pushToken = Metadata["MihuBotPushToken"];

        File.WriteAllText(ScriptName,
            $$"""
            git config --system core.longpaths true
            git clone --progress https://github.com/dotnet/runtime runtime
            cd runtime
            git log -1
            git config --global user.email mihubot@mihubot.xyz
            git config --global user.name MihuBot
            git remote add pr https://MihuBot:{{pushToken}}@github.com/{{SourceRepo}}.git
            git fetch pr {{SourceBranch}}
            git checkout {{SourceBranch}}
            git log -1
            git rebase main
            git push pr -f
            """);

        await RunProcessAsync(ScriptName, string.Empty,
            processLogs: line => line.Replace(pushToken, "<REDACTED>", StringComparison.OrdinalIgnoreCase));
    }
}
