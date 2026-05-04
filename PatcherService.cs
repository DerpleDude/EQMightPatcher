using LibGit2Sharp;
using System.IO;
using System.Security.Cryptography;

namespace EQMightPatcher;

public class PatcherService
{
    private const string RepoUrl = "https://github.com/EQMight/EQMightPatchFiles.git";

    // repo folder names whose contents are copied directly into eqDirectory
    private static readonly string[][] FoldersToCopy =
    [
        ["patchfiles", "client"],
        ["patchfiles", "zones"],
    ];

    private static string RepoPath(string eqDirectory) =>
        Path.Combine(eqDirectory, "EQMightPatcher");

    public async Task SyncAndPatch(string eqDirectory, IProgress<(double Percent, string Status)> progress,
        IProgress<string> log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(eqDirectory) || !Directory.Exists(eqDirectory))
            throw new InvalidOperationException("EQ directory is not set or does not exist.");

        void Report(double pct, string msg) { progress.Report((pct, msg)); log.Report(msg); }

        Report(0, "Checking repository...");
        var repoPath = RepoPath(eqDirectory);

        await Task.Run(() =>
        {
            if (!Directory.Exists(repoPath) || !Repository.IsValid(repoPath))
            {
                if (Directory.Exists(repoPath))
                    Directory.Delete(repoPath, recursive: true);
                Directory.CreateDirectory(repoPath);
                Report(0.05, "Cloning patch repository...");
                var cloneOptions = new CloneOptions();
                cloneOptions.FetchOptions.OnProgress = _ => !ct.IsCancellationRequested;
                cloneOptions.FetchOptions.OnTransferProgress = p =>
                {
                    double pct = p.TotalObjects > 0
                        ? 0.05 + (p.ReceivedObjects / (double)p.TotalObjects) * 0.6
                        : 0.05;
                    progress.Report((pct, $"Cloning... {p.ReceivedObjects}/{p.TotalObjects} objects"));
                    return !ct.IsCancellationRequested;
                };
                Repository.Clone(RepoUrl, repoPath, cloneOptions);
            }
            else
            {
                Report(0.05, "Pulling latest patches...");
                using var repo = new Repository(repoPath);
                var remote = repo.Network.Remotes["origin"];
                var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification);
                Commands.Fetch(repo, remote.Name, refSpecs, null, null);

                var remoteBranch = repo.Branches["origin/main"] ?? repo.Branches["origin/master"];
                if (remoteBranch != null)
                {
                    var branchName = remoteBranch.FriendlyName.Replace("origin/", "");
                    var localBranch = repo.Branches[branchName];
                    if (localBranch == null)
                    {
                        localBranch = repo.CreateBranch(branchName, remoteBranch.Tip);
                        repo.Branches.Update(localBranch, b => b.TrackedBranch = remoteBranch.CanonicalName);
                    }
                    Commands.Checkout(repo, localBranch);
                    repo.Reset(ResetMode.Hard, remoteBranch.Tip);
                    Report(0.65, localBranch.Tip.Sha != remoteBranch.Tip.Sha ? "Repository updated." : "Repository already up to date. Checking files...");
                }
            }

            ct.ThrowIfCancellationRequested();

            var filePairs = new List<(string Src, string Dest)>();
            foreach (var folderParts in FoldersToCopy)
            {
                var srcRoot = Path.Combine([repoPath, .. folderParts]);
                var label = Path.Combine(folderParts);
                if (!Directory.Exists(srcRoot))
                {
                    log.Report($"  [warn] folder not found in repo: {label} (looked in {srcRoot})");
                    continue;
                }
                foreach (var srcFile in Directory.EnumerateFiles(srcRoot, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(srcRoot, srcFile);
                    filePairs.Add((srcFile, Path.Combine(eqDirectory, rel)));
                }
            }

            log.Report($"  {filePairs.Count} file(s) to check");

            int copied = 0;
            for (int i = 0; i < filePairs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (src, dest) = filePairs[i];
                double pct = 0.65 + (i / (double)Math.Max(filePairs.Count, 1)) * 0.35;
                var name = Path.GetFileName(dest);

                if (File.Exists(dest) && Md5Equal(src, dest))
                {
                    progress.Report((pct, $"Checking files..."));
                    continue;
                }

                var label = File.Exists(dest) ? "[update]" : "[new]";
                progress.Report((pct, $"Copying {name}..."));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(src, dest, overwrite: true);
                    log.Report($"OK  {label} {Path.GetFileName(src)} -> {dest}");
                    copied++;
                }
                catch (Exception ex)
                {
                    log.Report($"ERROR  {label} {Path.GetFileName(src)} -> {dest}: {ex.Message}");
                }
            }

            var summary = copied > 0 ? $"Patch complete! ({copied} files updated)" : "Already up to date.";
            Report(1.0, summary);
        }, ct);
    }

    private const string ApiUrl = "https://api.github.com/repos/EQMight/EQMightPatchFiles/commits/HEAD";

    private static (string Sha, string CommitLog) FetchLatestFromApi()
    {
        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "EQMightPatcher");
        http.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        var json = http.GetStringAsync(ApiUrl).GetAwaiter().GetResult();
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        var sha = root.GetProperty("sha").GetString() ?? "";
        var commit = root.GetProperty("commit");
        var author = commit.GetProperty("author");
        var name = author.GetProperty("name").GetString() ?? "";
        var dateStr = author.GetProperty("date").GetString() ?? "";
        var message = commit.GetProperty("message").GetString()?.TrimEnd() ?? "";
        if (DateTimeOffset.TryParse(dateStr, out var dt))
            dateStr = dt.LocalDateTime.ToString("yyyy-MM-dd");
        return (sha, $"[{dateStr}]\n{message}");  // removed author
    }

    public (bool HasNew, string CommitLog) FetchAndCheck(string eqDirectory)
    {
        var repoPath = RepoPath(eqDirectory);

        string remoteSha;
        string commitLog;
        try
        {
            (remoteSha, commitLog) = FetchLatestFromApi();
        }
        catch
        {
            return (true, "Could not reach patch repository.");
        }

        if (!Directory.Exists(repoPath) || !Repository.IsValid(repoPath))
            return (true, commitLog);

        string localSha;
        using (var repo = new Repository(repoPath))
        {
            var branch = repo.Branches["origin/main"] ?? repo.Branches["origin/master"];
            localSha = branch?.Tip?.Sha ?? repo.Head.Tip?.Sha ?? "";
        }

        var hasNew = localSha != remoteSha;
        var filesDiffer = AnyFileDiffers(repoPath, eqDirectory);

        return (hasNew || filesDiffer, commitLog);
    }

    private bool AnyFileDiffers(string repoPath, string eqDirectory)
    {
        foreach (var folderParts in FoldersToCopy)
        {
            var srcRoot = Path.Combine([repoPath, .. folderParts]);
            if (!Directory.Exists(srcRoot)) continue;
            foreach (var srcFile in Directory.EnumerateFiles(srcRoot, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(eqDirectory, Path.GetRelativePath(srcRoot, srcFile));
                if (!File.Exists(dest) || !Md5Equal(srcFile, dest))
                    return true;
            }
        }
        return false;
    }

    private static bool Md5Equal(string pathA, string pathB)
    {
        using var md5 = MD5.Create();
        using var a = File.OpenRead(pathA);
        using var b = File.OpenRead(pathB);
        return md5.ComputeHash(a).SequenceEqual(md5.ComputeHash(b));
    }

}
