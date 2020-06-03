using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using Mono.Options;
using EngUpdater;

namespace EmscriptenVerBumper
{
    class Configuration
    {
        public string VersionsRepo = "dotnet/versions";
        public string VersionsBranch = "master";
        public string MasterPath = "build-info/docker/image-info.dotnet-dotnet-buildtools-prereqs-docker-master.json";

        public string RuntimeRepo = "dotnet/runtime";
        public string RuntimeBranch = "master";
        public string RuntimeWorkingDir = String.Empty;
        public string EmsdkVer = String.Empty;
        public string PlatformPath = "eng/pipelines/common/platform-matrix.yml";
        public string GitRemoteOriginName = "origin";
        public string GitRemoteName = String.Empty;
        public string GitRemoteUserName = String.Empty;
        public string PersonalAccessToken = String.Empty;
        public string[]? Reviewers = null;
        public string RegexPattern = "ubuntu-[0-9.]+-webassembly-([0-9-a-zA-Z]+)";
        public bool DryRun;
        public bool Verbose;
    }

    class Program 
    {
        static readonly string BranchPrefix = "bump_emsdk";

        static Configuration ProcessArguments (string[] args) 
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
                { "verions-repo=",
                    "toolset repository - normally \"mono/mono\"",
                    v => config.VersionsRepo = v },
                { "m|versions-branch=",
                    "toolset branch to source from",
                    v => config.VersionsBranch = v },
                { "runtime-repo=",
                    "runtime repository - normally \"dotnet/versions",
                    v => config.RuntimeRepo = v },
                { "s|runtime-branch=",
                    "runtime branch or commit sha",
                    v => config.RuntimeBranch = v },
                { "runtime-working-dir=",
                    "runtime woring directory",
                    v => config.RuntimeWorkingDir = v},
                { "emsdk-ver=",
                    "Emscripten version",
                    v => config.EmsdkVer = v},
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

            if (help || args.Length< 1 || remaining.Count > 1) {
                options.WriteOptionDescriptions (Console.Out);
                Environment.Exit (0);
            }

            if (config.GitRemoteName.Length == 0)
                config.GitRemoteName = config.GitRemoteUserName;

            if (!IsConfigValid (config))
                Environment.Exit (0);

            return config;
        }

        static bool IsConfigValid (Configuration config)
        {
            if (config.RuntimeWorkingDir.Length == 0) {
                Console.WriteLine ("Error: --runtime-working-dir required");
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

        public static async Task<string> GetDockerImage (Configuration config)
        {
            string path = config.MasterPath;
            var versionsBranchHead = await GitHub.GetBranchHead (config.VersionsRepo, config.VersionsBranch, config.Verbose);
            if (String.IsNullOrEmpty (versionsBranchHead)) {
                versionsBranchHead = config.VersionsBranch;
            }

            var masterJsonStream = await GitHub.GetRaw (config.VersionsRepo, versionsBranchHead, config.MasterPath);
            Console.WriteLine ($"From mono repo {config.VersionsRepo}, branch {config.VersionsBranch} at commit {versionsBranchHead}:");

            var contents = await new StreamReader (masterJsonStream).ReadToEndAsync ();
            var result = String.Empty;
            
            //  Find udpated docker version
            var match = Regex.Match (contents, config.RegexPattern);
            if (match.Success) {
                result = match.Groups[0].ToString();
            } else {
                Console.WriteLine($"error: failed to find anything for regex");
            }

            Console.WriteLine ($"Docker image: {result}");

            return result;
        }

        public static async Task<bool> UpdateYml (string updatedVer, Repository repo, Configuration config)
        {
            string path = Path.Combine(config.RuntimeWorkingDir, config.PlatformPath);

            //  Get current contents of Yml file from Github
            var contents = await File.ReadAllTextAsync (path);
            var match = Regex.Match (contents, config.RegexPattern);
            if (match.Success) {
                // Replace docker image with new version
                contents = Regex.Replace(contents, config.RegexPattern, updatedVer);

                //  Write to working directory
                await File.WriteAllTextAsync (path, contents);

                Commands.Stage (repo, config.PlatformPath);
                return true;
            } else {
                Console.WriteLine($"error: failed to find anything for regex {config.RegexPattern}");
                return false;
            }

            
        }

        static async Task Main (string[] args)
        {
            var config = ProcessArguments (args);

            if (config.GitRemoteUserName.Length == 0)
                config.GitRemoteUserName = config.GitRemoteName;

            await GitHub.CleanupUnusedBranches(config.GitRemoteUserName, config.RuntimeRepo, BranchPrefix, config.PersonalAccessToken, config.DryRun, config.Verbose);

            Repository repo = new Repository (config.RuntimeWorkingDir);
            string remote_name, remote_branch_name, local_branch_name, remote_runtime_repo;
            remote_name = config.GitRemoteName;
            remote_branch_name = config.RuntimeBranch;
            remote_runtime_repo = "dotnet/runtime";
            local_branch_name = $"{BranchPrefix}_{config.RuntimeBranch}_{Guid.NewGuid ()}";

            GitHub.PrepareMonoWorkingDirectory (
                                         repo:                repo,
                                         mono_working_dir:    config.RuntimeWorkingDir,
                                         remote_name:         remote_name,
                                         remote_branch_name:  remote_branch_name,
                                         local_branch_name:   local_branch_name,
                                         verbose:             config.Verbose);

            string DockerImage = await GetDockerImage (config);
            Console.WriteLine(DockerImage);

            if (!await UpdateYml (DockerImage, repo, config)) {
                Console.WriteLine ($"Error: unable to update platform-matrix.yml");
                return;
            }

            var commit_msg =  $"[{config.RuntimeBranch}] Bump Docker image with Emscripten {config.EmsdkVer}";

            var sig = repo.Config.BuildSignature (DateTimeOffset.Now);

            repo.Commit (commit_msg, sig, sig);

            if (!GitHub.GitPush (config.RuntimeWorkingDir, config.GitRemoteName, local_branch_name, local_branch_name, config.DryRun)) {
                Console.WriteLine ($"Error: git push failed");
                return;
            }

            if (!config.DryRun) {
                // Create new pull request
                var title = $"[{config.RuntimeBranch}] Bump Docker image with Emscripten {config.EmsdkVer}";

                (var result, var html_url, _) = await GitHub.CreatePullRequest (
                                                                        target_owner_repo:      remote_runtime_repo,
                                                                        base_branch_name:       config.RuntimeBranch,
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
        }
    }
}
