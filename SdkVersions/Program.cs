using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Mono.Options;

namespace EngUpdater
{
    class Configuration {
        public string MonoRepo = "mono/mono";
        public string MonoBranch = "master";

        public string MSBuildRepo = "mono/msbuild";
        public string MSBuildBranchOrSha = null;

        public string VersionsPath = "eng/Versions.props";
        public string VersionDetailsPath = "eng/Version.Details.xml";
        public string NuGetPyPath = "packaging/MacSDK/nuget.py";
        public string MSBuildPyPath = "packaging/MacSDK/msbuild.py";
        public bool Verbose = false;
    }

    class Program
    {

        static (List<string>, Configuration) ProcessArguments (string [] args)
        {
            var config = new Configuration ();
            var help = false;
            var options = new OptionSet {
                $"Usage: dotnet run -- OPTIONS*",
                "",
                "Prints the SDK+nuget.exe+roslyn versions corresponding to a mono branch/commit",
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
                { "mono-branch=",
                    "toolset branch to source from",
                    v => config.MonoBranch = v },
                { "msbuild-repo=",
                    "msbuild repository - normally \"mono/msbuild",
                    v => config.MSBuildRepo = v },
                { "msbuild-branch=",
                    "msbuild branch or commit sha",
                    v => config.MSBuildBranchOrSha = v },
                { "v|verbose",
                    "Output information about progress during the run of the tool",
                    v => config.Verbose = true },
            };

            var remaining = options.Parse (args);

            if (help) {//} || args.Length< 1) {
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
                config.MonoBranch = remaining[0];
                msbuildPath = remaining[1];
                break;
                default:
                break;
            }

            await UpdateXml (msbuildPath, config);
        }

        public static async Task UpdateXml (string path, Configuration config)
        {
            var monoBranchHead = await GitHub.GetBranchHead (config.MonoRepo, config.MonoBranch, config.Verbose);
            if (String.IsNullOrEmpty (monoBranchHead)) {
                // failed to get the branch head. Maybe MonoBranch is a commit sha
                monoBranchHead = config.MonoBranch;
            }

            var nugetExeSourceStream = await GitHub.GetRaw (config.MonoRepo, monoBranchHead, config.NuGetPyPath);
            var msbuildPySourceStream = await GitHub.GetRaw (config.MonoRepo, monoBranchHead, config.MSBuildPyPath);

            Console.WriteLine ($"From mono repo {config.MonoRepo}, branch {config.MonoBranch} at commit {monoBranchHead}:");
            var nugetExeVersion = await GetRegexGroupAsString(nugetExeSourceStream, "version='([0-9\\.]*)'");
            Console.WriteLine ($"nuget.exe: {nugetExeVersion}");

            if (String.IsNullOrEmpty (config.MSBuildBranchOrSha))
                config.MSBuildBranchOrSha = await GetRegexGroupAsString(msbuildPySourceStream, "revision *= *'([0-9a-fA-F]*)'");
            Console.WriteLine($"{Environment.NewLine}From msbuild commit: {config.MSBuildBranchOrSha}");

            var detailsSourceStream = await GitHub.GetRaw (config.MSBuildRepo, config.MSBuildBranchOrSha, config.VersionDetailsPath);
            var versionsSourceStream = await GitHub.GetRaw (config.MSBuildRepo, config.MSBuildBranchOrSha, config.VersionsPath);

            Console.WriteLine($"-- SDKs --");
            var details = await VersionUpdater.ReadVersionDetails (detailsSourceStream);
            foreach (var kvp in details)
                Console.WriteLine($"{kvp.Value.Name}: {kvp.Value.Version}");

            Console.WriteLine();
            var versions = await VersionUpdater.ReadProps (versionsSourceStream, config.Verbose);
            if (versions.TryGetValue ("MicrosoftNetCompilersPackageVersion", out string microsoftNetCompilersVersion))
                Console.WriteLine($"Roslyn version in msbuild (only for building): {microsoftNetCompilersVersion}");
            else
                Console.WriteLine($"Roslyn version in msbuild (only for building): Not found!");
        }

        public static async Task<string> GetRegexGroupAsString (Stream stream, string regex)
        {
            var contents = await new StreamReader (stream).ReadToEndAsync ();
            // Console.WriteLine(contents);

            var result = String.Empty;
            var match = Regex.Match (contents, regex);
            if (match.Success) {
                result = match.Groups[1].ToString ();
            } else {
                Console.WriteLine("error: failed to find anything for regex {regex}");
            }

            return result;
        }
    }
}
