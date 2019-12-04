using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using Mono.Options;

namespace EngUpdater
{
    class Configuration {
        public string ToolsetRepo = "dotnet/toolset";
        public string ToolsetBranch = "release/3.1.1xx";

        public string MSBuildRepo = "mono/msbuild";
        public string MSBuildBranch = "mono-2019-08";

        public string VersionsPath = "eng/Versions.props";
        public string PackagesPath = "eng/Packages.props";
        public string VersionDetailsPath = "eng/Version.Details.xml";
        public bool Verbose = false;
    }

    class Program
    {
        static (List<string>, Configuration) ProcessArguments (string [] args)
        {
            var config = new Configuration ();
            var help = false;
            var options = new OptionSet {
                $"Usage: dotnet run -- OPTIONS* <msbuild repository>",
                "",
                "Automates version updates for mono/msbuild",
                "",
                "Copyright 2019 Microsoft Corporation",
                "",
                "Options:",
                { "h|help|?",
                    "Show this message and exit",
                    v => help = v != null},
                { "toolset-repo=",
                    "toolset repository - normally \"dotnet/toolset\"",
                    v => config.ToolsetRepo = v },
                { "toolset-branch=",
                    "toolset branch or commit sha to source from",
                    v => config.ToolsetBranch = v },
                { "msbuild-repo=",
                    "msbuild repository - normally \"mono/msbuild",
                    v => config.MSBuildRepo = v },
                { "msbuild-branch=",
                    "msbuild branch or commit sha to source from",
                    v => config.MSBuildBranch = v },
                { "v|verbose",
                    "Output information about progress during the run of the tool",
                    v => config.Verbose = true },
            };

            var remaining = options.Parse (args);

            if (help || args.Length< 1) {
                options.WriteOptionDescriptions (Console.Out);
                Environment.Exit (0);
            }

            return (remaining, config);
        }

        static async Task Main(string[] args)
        {
            string msbuildPath = null;

            var (remaining, config) = ProcessArguments (args);
            switch (remaining.Count) {
                case 1:
                msbuildPath = remaining[0];
                break;
                case 2:
                config.ToolsetBranch = remaining[0];
                msbuildPath = remaining[1];
                break;
                default:
                break;
            }

            await UpdateXml (msbuildPath, config);
        }

        public static async Task UpdateXml (string path, Configuration config)
        {
            var toolsetBranchHead = await GitHub.GetBranchHead(config.ToolsetRepo, config.ToolsetBranch, config.Verbose);
            if (String.IsNullOrEmpty (toolsetBranchHead)) {
                // failed to get the branch head. Maybe ToolsetBranch is a commit sha
                toolsetBranchHead = config.ToolsetBranch;
            }

            var versionsSourceStream = await GitHub.GetRaw (config.ToolsetRepo, toolsetBranchHead, config.VersionsPath);
            var detailsSourceStream = await GitHub.GetRaw (config.ToolsetRepo, toolsetBranchHead, config.VersionDetailsPath);

            var versionsTargetStream = await GitHub.GetRaw (config.MSBuildRepo, config.MSBuildBranch, config.VersionsPath);
            var packagesTargetStream = await GitHub.GetRaw (config.MSBuildRepo, config.MSBuildBranch, config.PackagesPath);
            var detailsTargetStream = await GitHub.GetRaw (config.MSBuildRepo, config.MSBuildBranch, config.VersionDetailsPath);

            var details = await VersionUpdater.ReadVersionDetails (detailsSourceStream);
            var versions = await VersionUpdater.ReadProps (versionsSourceStream, config.Verbose);

            versions.Remove ("PackageVersionPrefix");
            versions.Remove ("VersionPrefix");

            foreach (var detail in details.Values) {
                // translate Versions.Details name into props name
                var vkey = $"{detail.Name.Replace (".", "")}PackageVersion";

                if (versions.TryGetValue (vkey, out var version)) {
                    if (config.Verbose) Console.WriteLine ($"{vkey}: {version} ====> {detail.Version} - {detail.Uri} - {detail.Sha}");
                } else {
                    if (config.Verbose) Console.WriteLine ($"No match for {vkey}");
                }
            }

            /* Alias nuget here?? */
            if (versions.TryGetValue ("NuGetBuildTasksPackageVersion", out var ver))
                versions ["NuGetPackagePackageVersion"] = ver;
            if (versions.TryGetValue ("MicrosoftNETCoreCompilersPackageVersion", out var roslyn_ver))
                versions [VersionUpdater.RoslynPackagePropertyName] = roslyn_ver;

            Stream detailsOutputStream = null;
            Stream versionsOutputStream = null;
            Stream packagesOutputStream = null;

            if (path != null) {
                detailsOutputStream = File.Create (Path.Combine (path, "eng/Version.Details.xml"));
                versionsOutputStream = File.Create (Path.Combine (path, "eng/Versions.props"));
                packagesOutputStream = File.Create (Path.Combine (path, "eng/Packages.props"));
            }

            var updatedVersions = await VersionUpdater.UpdateProps (versionsTargetStream, versionsOutputStream, versions, true);
            var updatedDetails = await VersionUpdater.UpdateDetails (detailsTargetStream, detailsOutputStream, details);
            var updatedPackages = await VersionUpdater.UpdateProps (packagesTargetStream, packagesOutputStream, versions, true);

            Console.WriteLine ($"Bump versions from {config.ToolsetRepo} at {config.ToolsetBranch}");
            Console.WriteLine ();
            Console.WriteLine ($"Based on commit {toolsetBranchHead}");
            Console.WriteLine ("```");
            foreach (var name in updatedDetails.Keys) {
                Console.WriteLine ($"{name,-40}: {details[name].Version} (from {updatedDetails[name].Version})");
            }
            if (updatedVersions.TryGetValue (VersionUpdater.RoslynPackagePropertyName, out var compiler_ver))
                Console.WriteLine ($"{Environment.NewLine}{"Microsoft.Net.Compilers/Roslyn",-40}: {compiler_ver}");
            Console.WriteLine ("```");
        }
    }

}
