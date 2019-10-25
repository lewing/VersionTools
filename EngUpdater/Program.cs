using System;
using System.Xml;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Linq;
using System.Text.Json;
using Mono.Options;

namespace EngUpdater
{

    public class VersionDetails {
        public string Name;
        public string Version;
        public string Uri;
        public string Sha;
        public string CoherentParentDependency;
    }

    class Program {
        static string ToolsetRepo = "dotnet/toolset";
        static string ToolsetBranch = "release/3.1.1xx";

        static string MSBuildRepo = "mono/msbuild";
        static string MSBuildBranch = "mono-2019-08";

        static string VersionsPath = "eng/Versions.props";
        static string PackagesPath = "eng/Packages.props";
        static string VersionDetailsPath = "eng/Version.Details.xml";
        static bool Verbose = false;

        static List<string> ProcessArguments (string [] args)
        {
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
                    v => ToolsetRepo = v },
                { "toolset-branch=",
                    "toolset branch to source from",
                    v => ToolsetBranch = v },
                { "msbuild-repo=",
                    "msbuild repository - normally \"mono/msbuild",
                    v => MSBuildRepo = v },
                { "msbuild-branch=",
                    "msbuild branch to source from",
                    v => MSBuildBranch = v },
                { "v|verbose",
                    "Output information about progress during the run of the tool",
                    v => Verbose = true },
            };

            var remaining = options.Parse (args);

            if (help || args.Length< 1) {
                options.WriteOptionDescriptions (Console.Out);
                Environment.Exit (0);
                }

            return remaining;
        }

        static async Task Main(string[] args)
        {
            string msbuildPath = null;

            var remaining = ProcessArguments (args);
            switch (remaining.Count) {
                case 1:
                msbuildPath = remaining[0];
                break;
                case 2:
                ToolsetBranch = remaining[0];
                msbuildPath = remaining[1];
                break;
                default:
                break;
            }

            await UpdateXml (msbuildPath);
        }

        public static async Task UpdateXml (string path)
        {
            var versionsSourceStream = await GitHub.GetRaw (ToolsetRepo, ToolsetBranch, VersionsPath);
            var detailsSourceStream = await GitHub.GetRaw (ToolsetRepo, ToolsetBranch, VersionDetailsPath);

            var versionsTargetStream = await GitHub.GetRaw (MSBuildRepo, MSBuildBranch, VersionsPath);
            var packagesTargetStream = await GitHub.GetRaw (MSBuildRepo, MSBuildBranch, PackagesPath);
            var detailsTargetStream = await GitHub.GetRaw (MSBuildRepo, MSBuildBranch, VersionDetailsPath);
            
            var details = await ReadVersionDetails (detailsSourceStream);
            var versions = await ReadProps (versionsSourceStream);

            versions.Remove ("PackageVersionPrefix");
            versions.Remove ("VersionPrefix");

            foreach (var detail in details.Values) {
                // translate Versions.Details name into props name
                var vkey = $"{detail.Name.Replace (".", "")}PackageVersion";

                if (versions.TryGetValue (vkey, out var version)) {
                    Console.WriteLine ($"{vkey}: {version} ====> {detail.Version} - {detail.Uri} - {detail.Sha}");
                } else {
                    Console.WriteLine ($"No match for {vkey}");
                }
            }

            /* Alias nuget here?? */
            if (versions.TryGetValue ("NuGetBuildTasksPackageVersion", out var ver))
                versions ["NuGetPackagePackageVersion"] = ver;

            Stream detailsOutputStream = null;
            Stream versionsOutputStream = null;
            Stream packagesOutputStream = null;

            if (path != null) {
                detailsOutputStream = File.Create (Path.Combine (path, "eng/Version.Details.xml"));
                versionsOutputStream = File.Create (Path.Combine (path, "eng/Versions.props"));
                packagesOutputStream = File.Create (Path.Combine (path, "eng/Packages.props"));
            }

            await UpdateProps (versionsTargetStream, versionsOutputStream, versions, true);
            await UpdateDetails (detailsTargetStream, detailsOutputStream, details);
            await UpdateProps (packagesTargetStream, packagesOutputStream, versions, true);
        }

        public static async Task<Dictionary<string,VersionDetails>> ReadVersionDetails (Stream stream)
        {
            var versions = new Dictionary<string,VersionDetails> ();
            using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings () { Async = true }))
            {
                while (await reader.ReadAsync()) {
                    switch (reader.NodeType) {
                    case XmlNodeType.Element when reader.Name == "Dependency":
                        var details = reader.ReadDependency ();
                        versions [details.Name] = details;
                        //Console.WriteLine ($"{details.Name} ==> {details.Version}");
                        break;
                    case XmlNodeType.Element when reader.Name == "ProductDependencies":
                    case XmlNodeType.Element when reader.Name == "Dependencies":
                        break;
                    case XmlNodeType.Element:
                        await reader.SkipAsync ();
                        break;
                    default:
                        break;
                    }
                }
                return versions;
            }
        }

        static async Task<Dictionary<string,string>> ReadProps (Stream stream)
        {
            using (XmlReader reader = XmlReader.Create (stream, new XmlReaderSettings () { Async = true }))
            {
                var settings = new Dictionary<string,string>();
                bool inGroup = false;
                while (await reader.ReadAsync ()) {
                    switch (reader.NodeType) {
                    case XmlNodeType.Element when reader.Name == "PropertyGroup":
                        inGroup = true;
                        break;
                    case XmlNodeType.Element when reader.Name == "Project":
                        break;
                    case XmlNodeType.Element when inGroup:
                        var key = reader.Name;
                        await reader.ReadAsync ();
                        var value = await reader.GetValueAsync ();
                        if (key.Contains ("Version") && Char.IsDigit (value[0])) {
                            var packageKey = !key.EndsWith ("PackageVersion") ? key.Replace ("Version", "PackageVersion") : key;
                            settings[packageKey] = value;
                            Console.WriteLine ($"{packageKey} => {value}");
                        }
                        break;
                    case XmlNodeType.Element:
                        await reader.SkipAsync ();
                        break;
                    case XmlNodeType.EndElement when reader.Name == "PropertyGroup":
                        inGroup = false;
                        break;
                    default:
                        break;
                    }
                }
                return settings;
            }
        }

        public static async Task UpdateProps (Stream inputStream, Stream outputStream, Dictionary<string,string> versions, bool stripPackage = false)
        {
            XmlWriterSettings settings = new XmlWriterSettings ();
            settings.Encoding = new UTF8Encoding (false);
            settings.Indent = true;

            using (var reader = XmlReader.Create (inputStream, new XmlReaderSettings { Async = true })) {
                using (var writer = outputStream != null ? XmlWriter.Create (outputStream, settings) : XmlWriter.Create (Console.Out)) {
                    while (await reader.ReadAsync ()) {
                        var name = stripPackage ? reader.Name.Replace ("Version", "PackageVersion") : reader.Name;
                        if (reader.NodeType == XmlNodeType.Element && versions.TryGetValue (name, out var value)) {
                            writer.WriteUpdatedElementString (reader, value);
                        } else {
                            writer.WriteNode (reader);
                        }
                    }
                }
            }
        }

        public static async Task UpdateDetails (Stream inputStream, Stream outputStream, Dictionary<string,VersionDetails> versions)
        {
            XmlWriterSettings settings = new XmlWriterSettings ();
            settings.Indent = true;
            settings.Encoding = new UTF8Encoding (false);

            using (var reader = XmlReader.Create (inputStream, new XmlReaderSettings { Async = true })) {
                using (var writer = outputStream != null ? XmlWriter.Create (outputStream, settings) : XmlWriter.Create (Console.Out)) {
                    while (await reader.ReadAsync ()) {
                        if (reader.NodeType == XmlNodeType.Element && reader.Name == "Dependency") {
                            var name = reader.GetAttribute ("Name");
                            if (versions.TryGetValue (name, out var details)) {
                                var oldDetails = writer.WriteDependency (reader, details);
                            } else {
                                writer.WriteNode (reader);
                            }
                        } else {
                            writer.WriteNode (reader);
                        }
                    }
                }
            }
        }
    }

    public static class XmlIOExtensions {
        public static string WriteUpdatedElementString (this XmlWriter writer, XmlReader reader, string value)
        {
            writer.WriteNode (reader);
            reader.Read ();
            var oldValue = reader.Value;

            if (Char.IsDigit (oldValue[0])) {
                writer.WriteString (value);
            } else {
                writer.WriteNode (reader);
            }

            reader.Read ();
            writer.WriteNode (reader);

            return oldValue;
        }

        public static VersionDetails ReadDependency (this XmlReader reader)
        {
            var details = new VersionDetails ();
            
            while (reader.MoveToNextAttribute ()) {
                ReadDetails (reader, details);
            }

            while (reader.Read ()) {
                switch (reader.NodeType) {
                case XmlNodeType.Element:
                    ReadDetails (reader, details);
                    break;
                case XmlNodeType.EndElement when reader.Name == "Dependency":
                    return details;
                }
            }

            void ReadDetails (XmlReader read, VersionDetails detail) 
            {
                var name = read.Name;
                if (read.NodeType == XmlNodeType.Element)
                    read.Read ();

                switch (name) {
                case "Name":
                    detail.Name = read.Value;
                    break;
                case nameof (VersionDetails.Version):
                    detail.Version = read.Value;
                    break;
                case nameof (VersionDetails.Uri):
                    detail.Uri = read.Value;
                    break;
                case nameof (VersionDetails.Sha):
                    detail.Sha = read.Value;
                    break;
                case nameof (VersionDetails.CoherentParentDependency):
                    detail.CoherentParentDependency = read.Value;
                    break;
                default:
                    read.Skip ();
                    break;
                }
            }

            return details;
        }

        public static VersionDetails WriteDependency (this XmlWriter writer, XmlReader reader, VersionDetails details)
        {
            var oldDetails = new VersionDetails ();
            writer.WriteStartElement (reader.Prefix, reader.LocalName, reader.NamespaceURI);
            writer.WriteAttributeString ("Name", reader.NamespaceURI, details.Name);
            writer.WriteAttributeString ("Version", reader.NamespaceURI, details.Version);

            oldDetails.Name = reader.GetAttribute ("Name");
            oldDetails.Version = reader.GetAttribute ("Version");
            if (details.CoherentParentDependency != null)
                writer.WriteAttributeString ("CoherentParentDependency", reader.NamespaceURI, details.CoherentParentDependency);

            while (reader.Read()) {
                switch (reader.NodeType) {
                case XmlNodeType.Element:
                    if (reader.Name == "Uri") {
                        oldDetails.Uri = writer.WriteUpdatedElementString (reader, details.Uri);
                    } else if (reader.Name == "Sha") {
                        oldDetails.Sha = writer.WriteUpdatedElementString (reader, details.Sha);
                    } else {
                        writer.WriteNode (reader);
                    }
                    break;
                case XmlNodeType.EndElement:
                    writer.WriteNode (reader);
                    if (reader.Name == "Dependency")
                        return oldDetails;

                    break;
                default:
                    writer.WriteNode (reader);
                    break;
                }
            }
            return oldDetails;
        }

        public static void WriteNode (this XmlWriter writer, XmlReader reader)
        {
            switch (reader.NodeType) {
            case XmlNodeType.Element:
                writer.WriteStartElement (reader.Prefix, reader.LocalName, reader.NamespaceURI);
                writer.WriteAttributes (reader, true );

                if (reader.IsEmptyElement)
                    writer.WriteEndElement ();

                break;
            case XmlNodeType.EndElement:
                writer.WriteFullEndElement ();
                break;
            case XmlNodeType.Text:
                writer.WriteString (reader.Value);
                break;
            case XmlNodeType.Whitespace:
            case XmlNodeType.SignificantWhitespace:
                writer.WriteWhitespace (reader.Value);
                break;
            case XmlNodeType.CDATA:
                writer.WriteCData (reader.Value);
                break;
            case XmlNodeType.EntityReference:
                writer.WriteEntityRef (reader.Name);
                break;
            case XmlNodeType.XmlDeclaration:
            case XmlNodeType.ProcessingInstruction:
                writer.WriteProcessingInstruction (reader.Name, reader.Value);
                break;
            case XmlNodeType.DocumentType:
                writer.WriteDocType (reader.Name, reader.GetAttribute ("PUBLIC"), reader.GetAttribute ("SYSTEM"), reader.Value);
                break;
            case XmlNodeType.Comment:
                writer.WriteComment (reader.Value);
                break;
            }
        }
    }

    class GitHub {
        static HttpClient client = new HttpClient();

        public static Task<Stream> GetRaw (string repo, string version, string path)
        {
            var location = new Uri($"https://raw.githubusercontent.com/{repo}/{version}/{path}");
            return client.GetStreamAsync (location);
        }

        static ValueTask<T> DeserializeAsync<T> (Stream stream, T example)
        {
            return JsonSerializer.DeserializeAsync<T> (stream);
        }

        public static async Task<(string sha, DateTimeOffset date) []> GetCommits(string repo, string path)
        {
            try {
                client.DefaultRequestHeaders.Add ("Accept", "*/*");
                client.DefaultRequestHeaders.Add ("User-Agent", "curl/7.54.0");

                var stream = await client.GetStreamAsync (new Uri ($"https://api.github.com/repos/{repo}/commits?path={path}"));

                var obj = new [] {
                    new {
                        sha = "",
                        commit = new {
                            author = new {
                                date = DateTimeOffset.Now
                            }
                        }
                    }
                };

                return (await DeserializeAsync (stream, obj)).Select (o => ValueTuple.Create (o.sha, o.commit.author.date)).ToArray ();
            } catch (Exception e) {
                Console.WriteLine (e.ToString ());
                return Array.Empty<(string sha, DateTimeOffset date)> ();
            }
        }
    }
}
