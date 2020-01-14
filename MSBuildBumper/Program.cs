using System;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Options;
using EngUpdater;

using LibGit2Sharp;
using System.Collections.Generic;

#nullable enable

namespace MSBuildBumper
{
    class Configuration {
        public string MonoRepo = "mono/mono";
        public string MonoWorkingDir = String.Empty;
        public string MonoBranch = "master";

        public string MSBuildRepo = "mono/msbuild";
        public string MSBuildBranch = "xplat-master";

        public string VersionsPath = "eng/Versions.props";
        public string VersionDetailsPath = "eng/Version.Details.xml";
        public string MSBuildPyPath = "packaging/MacSDK/msbuild.py";
        public string GitRemoteOriginName = "origin";
        public string GitRemoteName = String.Empty;
        public string GitRemoteUserName = String.Empty;
        public string PersonalAccessToken = String.Empty;
        public string? PullRequestNumber = null;
        public string[]? Reviewers = null;
        public bool DryRun;
        public bool Verbose;
    }

    class Program
    {
        const string VersionsPropsFileName = "eng/Versions.props";
        static readonly string MSBuildPyPath = "packaging/MacSDK/msbuild.py";
        static readonly string BranchPrefix = "bump_msbuild";
        static readonly string NuGetPyPath = "packaging/MacSDK/nuget.py";

        static Configuration ProcessArguments (string [] args)
        {
            var config = new Configuration ();
            var help = false;
            var options = new OptionSet {
                $"Usage: dotnet run -- OPTIONS* [<specific PR number to update>]",
                "",
                "Bump msbuild reference in mono",
                "",
                "Copyright 2019 Microsoft Corporation",
                "",
                "Options:",
                { "h|help|?",
                    "Show this message and exit",
                    v => help = v != null},
                { "mono-repo=",
                    "toolset repository - normally \"mono/mono\"",
                    v => config.MonoRepo = v },
                { "m|mono-branch=",
                    "toolset branch to source from",
                    v => config.MonoBranch = v },
                { "msbuild-repo=",
                    "msbuild repository - normally \"mono/msbuild",
                    v => config.MSBuildRepo = v },
                { "s|msbuild-branch=",
                    "msbuild branch or commit sha",
                    v => config.MSBuildBranch = v },
                {"mono-working-dir=",
                    "mono working directory",
                    v => config.MonoWorkingDir = v },
                {"t|pat=",
                    "github personal access token",
                    v => config.PersonalAccessToken = v },
                {"n|dry-run:",
                    "No changes will get pushed and no will be PRs created",
                    v => config.DryRun = true },
                {"git-user-name=",
                    "github username where the PRs will be created",
                    v => config.GitRemoteUserName = v },
                {"remote-name=",
                    "github remote name in the local working directory. Defaults to the same has git_user_name",
                    v => config.GitRemoteName = v },
                {"r|reviewers=",
                    "comma separated list of reviewer usernames",
                    v => config.Reviewers = v?.Split (",", StringSplitOptions.RemoveEmptyEntries) },
                { "v|verbose",
                    "Output information about progress during the run of the tool",
                    v => config.Verbose = true },

                new ResponseFileSource ()
            };

            var remaining = options.Parse (args);
            config.PullRequestNumber = remaining.Count == 1 ? remaining [0] : null;
            if (config.PullRequestNumber != null && !Int32.TryParse (config.PullRequestNumber, out var _)) {
                Console.WriteLine ($"Error: pr_number ('{config.PullRequestNumber}') should be a number.");
                help = true;
            }

            if (config.GitRemoteName.Length == 0)
                config.GitRemoteName = config.GitRemoteUserName;

            if (help || args.Length< 1 || remaining.Count > 1) {
                options.WriteOptionDescriptions (Console.Out);
                Environment.Exit (0);
            }

            if (!IsConfigValid (config))
                Environment.Exit (0);

            return config;
        }

        static bool IsConfigValid (Configuration config)
        {
            if (config.MonoWorkingDir.Length == 0) {
                Console.WriteLine ("Error: --mono-working-dir required");
                return false;
            }
            if (config.GitRemoteName.Length == 0 || config.GitRemoteUserName.Length == 0) {
                Console.WriteLine ("Error: Remote name and remote user name are required.");
                return false;
            }

            if (!config.DryRun && config.PersonalAccessToken.Length == 0) {
                Console.WriteLine ($"Error: GitHub personal access token required to create pull requests.");
                return false;
            }

            return true;
        }

        // FIXME: doesn't work with git@ remote urls, but does git:// work?
        // FIXME: Add the shortened hash to the commit message

        static async Task Main(string[] args)
        {
            var config = ProcessArguments (args);

            if (config.GitRemoteUserName.Length == 0)
                config.GitRemoteUserName = config.GitRemoteName;

            await CleanupUnusedBranches(config.GitRemoteUserName, config.MonoRepo, BranchPrefix, config.PersonalAccessToken, config.DryRun, config.Verbose);

            PullRequest? pr = await GetPullRequest (config);
            if (pr == null && config.PullRequestNumber != null) {
                return;
            }

            string local_branch_name, remote_branch_name, remote_name, remote_mono_repo;
            if (pr != null) {
                // We want to check if the branch for this PR, needs an update or not
                remote_mono_repo = $"{pr.HeadRepoOwner}/mono";
                remote_branch_name = pr.HeadRef;
                remote_name = config.GitRemoteName;

                // Use the branch name that the PR uses
                local_branch_name = pr.HeadRef;
            } else {
                Trace ($"no pr found, setting remote_mono_repo to {config.MonoRepo}", config.Verbose);

                remote_mono_repo = config.MonoRepo;
                remote_branch_name = config.MonoBranch;
                remote_name = config.GitRemoteOriginName;
                local_branch_name = $"{BranchPrefix}_{config.MonoBranch}_{Guid.NewGuid ()}";
            } 

            string msbuildBranchHead, msbuildRefInMono;
            try {
                bool required;
                (required, msbuildBranchHead, msbuildRefInMono) = await IsMSBuildBumpRequired (
                                                                    msbuild_repo:      config.MSBuildRepo,
                                                                    msbuild_branch:    config.MSBuildBranch,
                                                                    mono_repo:         remote_mono_repo,
                                                                    mono_branch:       remote_branch_name,
                                                                    verbose:           config.Verbose);

                if (!required) {
                    Console.WriteLine($"-> Ref is updated already, nothing to be done.");
                    return;
                }
            } catch (HttpRequestException hre) {
                Console.WriteLine ($"Error checking if bump is required: {hre.Message}");
                return;
            }

            Repository repo = new Repository (config.MonoWorkingDir);
            PrepareMonoWorkingDirectory (repo:                repo,
                                         mono_working_dir:    config.MonoWorkingDir,
                                         remote_name:         remote_name,
                                         remote_branch_name:  remote_branch_name,
                                         local_branch_name:   local_branch_name,
                                         verbose:             config.Verbose);

            if (!(await UpdateMSBuildPy (msbuildBranchHead, config.MonoWorkingDir, repo))) {
                Console.WriteLine ($"Error: unable to update msbuild.py");
                return;
            }

            // git commit
            var commit_msg = $"[{config.MonoBranch}] Bump msbuild to track {config.MSBuildBranch}";
            var sig = repo.Config.BuildSignature (DateTimeOffset.Now);
            repo.Commit (commit_msg, sig, sig);

            if (!GitPush (config.MonoWorkingDir, config.GitRemoteName, local_branch_name, local_branch_name, config.DryRun)) {
                Console.WriteLine ($"Error: git push failed");
                return;
            }

            if (!config.DryRun && pr != null)
                Console.WriteLine ($"---- Updated PR: {pr.HTML_URL} ---");

            if (!config.DryRun && pr == null) {
                // Create new pull request
                var title = $"[{config.MonoBranch}] Bump msbuild to track {config.MSBuildBranch}";

                (var result, var html_url, _) = await GitHub.CreatePullRequest (
                                                                        target_owner_repo:      remote_mono_repo,
                                                                        base_branch_name:       config.MonoBranch,
                                                                        head_ref:               $"{config.GitRemoteUserName}:{local_branch_name}",
                                                                        personal_access_token:  config.PersonalAccessToken,
                                                                        title:                  title,
                                                                        body:                   String.Empty,
                                                                        reviewers:              config.Reviewers,
                                                                        verbose:                config.Verbose);
                if (result) {
                    Console.WriteLine ($"----------- Created new PR at {html_url} ----------");
                } else {
                    Console.WriteLine ("Failed to create PR.");
                }
            }

            await CheckRoslynAndNuGet (config.MonoRepo, config.MonoBranch, config.MSBuildRepo, msbuildRefInMono, msbuildBranchHead, config.Verbose);
        }

        static async Task CleanupUnusedBranches (string owner_name,
                                                 string mono_repo_for_prs,
                                                 string branch_prefix,
                                                 string personal_access_token,
                                                 bool dry_run = false,
                                                 bool verbose = false)
        {
            var tasks = new List<Task<HttpResponseMessage>>();
            var branchesJsonElement = await GitHub.GetJsonFromApiRequest<JsonElement>($"/repos/{owner_name}/mono/git/matching-refs/heads/{branch_prefix}");
            foreach (var je in branchesJsonElement.EnumerateArray ()) {
                var branch_ref = je.Get<string>("ref");
                // this should be of the form `refs/heads/branch_name`
                var branch_name = branch_ref.Split("/", 3)[2];

                var gh_rest_url = $"/repos/{mono_repo_for_prs}/pulls?state=open&head={owner_name}:{branch_name}";
                if (await GitHub.GetPullRequests(gh_rest_url, verbose).AnyAsync ())
                    continue;

                // delete the branch
                if (dry_run) {
                    Console.WriteLine($"Would delete {owner_name}:{branch_name}");
                    continue;
                }

                gh_rest_url = $"/repos/radical/mono/git/refs/heads/{branch_name}";
                tasks.Add (GitHub.PostOrDeleteAPI(HttpMethod.Delete, gh_rest_url, personal_access_token, String.Empty, verbose));
                if (verbose)
                    Console.WriteLine ($"- Deleting unused branch {owner_name}:{branch_name}");
            }

            Task.WaitAll(tasks.ToArray ());
        }

        static async Task CheckRoslynAndNuGet (string mono_repo, string mono_branch_head, string msbuild_repo, string old_msbuild_ref, string new_msbuild_ref, bool verbose = false)
        {
            using var new_fs = await GitHub.GetRaw (msbuild_repo, new_msbuild_ref, VersionsPropsFileName, verbose);
            using var old_fs = await GitHub.GetRaw (msbuild_repo, old_msbuild_ref, VersionsPropsFileName, verbose);

            var new_props = await VersionUpdater.ReadProps (new_fs, false);
            var old_props = await VersionUpdater.ReadProps (old_fs, false);

            if ((new_props.TryGetValue (VersionUpdater.RoslynPackagePropertyName, out var new_version) &&
                old_props.TryGetValue (VersionUpdater.RoslynPackagePropertyName, out var old_version))) {
                if (string.Compare (new_version, old_version) != 0) {
                    Console.WriteLine ($"\n** NOTE: Roslyn will need an update. It changed from '{old_version}' to '{new_version}'\n");
                }
            } else {
                Console.WriteLine ($"Error: Could not read roslyn versions.");
            }

            if (new_props.TryGetValue ("NuGetBuildTasksPackageVersion", out var nuget_tasks_version)) {
                // get the version from mono's nuget.py
                var nugetExeSourceStream = await GitHub.GetRaw (mono_repo, mono_branch_head, NuGetPyPath);
                var nugetExeVersion = await GetRegexGroupAsStringAsync (nugetExeSourceStream, VersionUpdater.NuGetPyVersionRegexString);
                Console.WriteLine ($"** NOTE: NuGet.Build.Tasks version: {nuget_tasks_version}");
                Console.WriteLine ($"         nuget.exe version in mono: {nugetExeVersion}");
            }
        }

        static async Task<PullRequest?> GetPullRequest (Configuration config)
        {
            PullRequest? pr = null;
            if (config.PullRequestNumber != null) {
                Trace ($"Fetching pull request #{config.PullRequestNumber} in {config.MonoRepo}", config.Verbose);
                try {
                    pr = await GetSinglePullRequest (config.MonoRepo, config.PullRequestNumber, config.Verbose);
                    
                    Console.WriteLine ($"* Using Mono branch as `{pr.BaseRef}`, obtained from the specified PR {pr.HTML_URL}");
                    config.MonoBranch = pr.BaseRef;
                } catch (HttpRequestException hre) {
                    if (config.Verbose)
                        Console.WriteLine ($"Failed to get a pull request numbered #{config.PullRequestNumber}: {hre.Message}");
                }
            } else {
                try {
                    Trace ($"Trying to find a pull request against {config.MonoRepo}/{config.MonoBranch} having a name starting with {BranchPrefix}", config.Verbose);
                    pr = await FindPullRequest (config.MonoRepo, config.MonoBranch, BranchPrefix, config.GitRemoteUserName, config.Verbose);
                    if (pr != null)
                        Trace ($"\tFound PR {pr.HTML_URL}, headrepo: {pr.HeadRepoOwner}/mono", config.Verbose);
                    else
                        Trace ($"\tNo PR found matching the criteria", config.Verbose);
                } catch (HttpRequestException hre) {
                    Console.WriteLine ($"Error: Failed to fetch mono pull requests: {hre.Message}");
                }
            }

            return pr;
        }

        static async Task<(bool required, string msbuild_branch_head, string msbuild_ref_in_mono)> IsMSBuildBumpRequired (
                                                                                              string msbuild_repo,
                                                                                              string msbuild_branch,
                                                                                              string mono_repo,
                                                                                              string mono_branch,
                                                                                              bool verbose)
        {
            Trace ($"Checking msbuild HEAD for {msbuild_repo} {msbuild_branch}, and ref in mono {mono_repo} {mono_branch}", verbose);
            var branchHeadTask = GitHub.GetBranchHead (msbuild_repo, msbuild_branch, verbose);
            var refInMonoTask = GetMSBuildReferenceInMono (mono_repo, mono_branch, verbose);

            var results = await Task.WhenAll (branchHeadTask, refInMonoTask);
            var msbuildBranchHead = results [0];
            var msbuildRefInMono = results [1];

            if (msbuildRefInMono.Length == 0)
                throw new KeyNotFoundException($"Could not find msbuild reference in mono.");

            Trace ($"Expected msbuild reference: {msbuildBranchHead} (from {msbuild_repo}/{msbuild_branch})", verbose);
            Trace ($"                  Mono has: {msbuildRefInMono} in {mono_repo}/{mono_branch}", verbose);
            bool bump_required = String.Compare (msbuildBranchHead, msbuildRefInMono) != 0;
            return (bump_required, msbuild_branch_head: msbuildBranchHead, msbuild_ref_in_mono: msbuildRefInMono);
        }

        static async Task<bool> UpdateMSBuildPy (string commit_sha, string mono_working_dir, Repository repo)
        {
            var msbuild_py_full_path = Path.Combine (mono_working_dir, MSBuildPyPath);
            var contents = await File.ReadAllTextAsync (msbuild_py_full_path);

            var match = Regex.Match (contents, VersionUpdater.MSBuildPyRefRegexString);
            if (match.Success) {
                var old_sha = match.Groups[1].ToString ();

                contents = contents.Replace (old_sha, commit_sha);
                await File.WriteAllTextAsync (msbuild_py_full_path, contents);

                Commands.Stage (repo, MSBuildPyPath);
                return true;
            } else {
                Console.WriteLine($"error: failed to find anything for regex {VersionUpdater.MSBuildPyRefRegexString}");
                return false;
            }
        }

        static async Task<string> GetRegexGroupAsStringAsync (Stream stream, string regex)
        {
            var contents = await new StreamReader (stream).ReadToEndAsync ();
            // Console.WriteLine(contents);

            var result = String.Empty;
            var match = Regex.Match (contents, regex);
            if (match.Success)
                result = match.Groups[1].ToString ();

            return result;
        }

        static async Task<string?> GetMSBuildReferenceInMono (string mono_repo, string mono_branch, bool verbose = false)
            => await GetRegexGroupAsStringAsync(await GitHub.GetRaw(mono_repo, mono_branch, MSBuildPyPath, verbose),
                                                "revision *= *'([0-9a-fA-F]*)'");

        public static async Task<PullRequest> GetSinglePullRequest (string base_repo, string pr_number, bool verbose = false)
            => await GitHub.GetPullRequests ($"/repos/{base_repo}/pulls/{pr_number}", verbose)
                        .FirstOrDefaultAsync ();

        public static async Task<PullRequest> FindPullRequest (string base_repo, string base_branch, string branch_prefix, string remote_user_name, bool verbose = false)
            => await GitHub.GetPullRequests ($"/repos/{base_repo}/pulls?base={base_branch}", verbose)
                        .Where (pr => pr != null &&
                                        string.Compare (pr.HeadRepoOwner, remote_user_name) == 0 &&
                                        pr.HeadRef.StartsWith (branch_prefix, StringComparison.InvariantCultureIgnoreCase))
                        .FirstOrDefaultAsync ();

        static void PrepareMonoWorkingDirectory (Repository repo,
                                                   string mono_working_dir,
                                                   string remote_name,
                                                   string remote_branch_name,
                                                   string local_branch_name,
                                                   bool verbose)
        {
            // git reset --hard + git-clean
            Console.WriteLine ($"Reseting working dir {mono_working_dir}");
            ResetAndCleanWorkingDirectory (repo);

            // git fetch origin
            Console.WriteLine ($"Fetching from {remote_name}");
            // FIXME: how can we use git@ ?
            // repo.Network.Remotes.Add ("origin-https", $"https://github.com/{mono_repo}");

            // Commands.Fetch (repo, remote_name, new string[0], null, null);
            GitCommand ($"fetch {remote_name}", repo.Info.Path, dry_run: false);

            var remote_ref = $"{remote_name}/{remote_branch_name}";
            var branch = repo.Branches [local_branch_name];
            if (branch != null) {
                Trace ($"git checkout {local_branch_name}", verbose);
                Commands.Checkout (repo, local_branch_name);

                Trace ($"git reset --hard {remote_ref}", verbose);
                repo.Reset (ResetMode.Hard, remote_ref);
            } else {
                Trace ($"git branch {local_branch_name} {remote_ref}", verbose);
                repo.Branches.Add (local_branch_name, $"{remote_name}/{remote_branch_name}");
                
                Trace ($"git checkout {local_branch_name}", verbose);
                Commands.Checkout (repo, local_branch_name);
            }
        }

        /// <summary>
        /// Reset and clean current working directory. This will ensure that the current
        /// working directory matches the current Head commit.
        /// </summary>
        /// <param name="repo">Repository whose current working directory should be operated on.</param>
        static void ResetAndCleanWorkingDirectory(IRepository repo)
        {
            // Reset the index and the working tree.
            repo.Reset(ResetMode.Hard);

            // Clean the working directory.
            repo.RemoveUntrackedFiles();
        }

        static bool GitCommand (string command_line, string working_dir, bool dry_run)
            => RunCommand ("git", command_line + (dry_run ? " -n" : String.Empty), working_dir);

        static bool GitPush (string mono_work_dir, string remote_name, string remote_branch_name, string local_branch_name, bool dry_run)
            => GitCommand ($"push {remote_name} {local_branch_name}:{remote_branch_name}", mono_work_dir, dry_run);

        static bool RunCommand (string command_name, string command_line, string working_dir)
        {
            Console.WriteLine ($"$ {command_name} {command_line}");
            var p = Process.Start (new ProcessStartInfo {
                            FileName = command_name,
                            Arguments = command_line,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            WorkingDirectory = working_dir
                        });

            p.WaitForExit ();

            var stdout_str = p.StandardOutput.ReadToEnd ();
            var stderr_str = p.StandardError.ReadToEnd ();
            if (stdout_str.Length > 0)
                Console.WriteLine (stdout_str);
            if (stderr_str.Length > 0)
                Console.WriteLine (stderr_str);

            if (p.ExitCode != 0)
                Console.WriteLine ($"Error: exitcode: {p.ExitCode}");

            return p.ExitCode == 0;
        }
        
        static void Trace (string message, bool verbose)
        {
            if (verbose)
                Console.WriteLine (message);
        }

    }
}
