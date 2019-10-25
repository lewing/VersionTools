using System;
using System.Xml;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Text.Json;
using Whomp = Newtonsoft.Json;

namespace InsertMonoAddin
{
    class Program
    {
        static HttpClient client = new HttpClient ();

        static async Task Main(string[] args)
        {
            var commit = args [0];
            string mdAddinsPath = args.Length > 1 ? args[1] : null; //args [1];

            await UpdateMdAddins (commit, mdAddinsPath);
        }

        public static async Task UpdateMdAddins (string commitOrBranch, string mdAddinsPath) {
            var status = await GetStatuses (commitOrBranch);
            var artifactsUri = status.statuses.FirstOrDefault (s => s.context == "artifacts.json")?.target_url;
            var commit = status.sha;
            var artifactsStream = await client.GetStreamAsync (artifactsUri);
            var artifact = (await JsonSerializer.DeserializeAsync<Artifacts[]>(artifactsStream))[0];
            Console.WriteLine ($"commit = {commit}");

            var mono = new MonoExternal () {
                url = artifact.url,
                version = artifact.version,
                productId = artifact.productId,
                commit = commit,
                releaseId = artifact.releaseId,
                sha256 = artifact.sha256,
                md5 = artifact.md5,
                size = artifact.size
            };

            var outputStream = new MemoryStream ();
            if (mdAddinsPath != null) {
                await UpdateDependencies (File.OpenRead (Path.Combine (mdAddinsPath, "bot-provisioning", "dependencies.csx")), outputStream, artifact);
                outputStream.Seek (0, SeekOrigin.Begin);
                //Console.Write (reader.ReadToEnd());
                using (var deps = File.Create (Path.Combine (mdAddinsPath, "bot-provisioning", "dependencies.csx"))) {
                    await outputStream.CopyToAsync (deps);
                }
                using (var external = File.Create (Path.Combine (mdAddinsPath, "external-components", "mono.json"))) {
                    //await JsonSerializer.SerializeAsync (external, mono, new JsonSerializerOptions () { WriteIndented = true });
                    using (var sw = new StreamWriter(external))
                    using (var jtw = new Whomp.JsonTextWriter(sw) {
                        Formatting = Whomp.Formatting.Indented,
                        Indentation=4, 
                        IndentChar = ' '})
                            (new Whomp.JsonSerializer()).Serialize(jtw, mono);
                }
            } else {
                Console.WriteLine (JsonSerializer.Serialize(mono, new JsonSerializerOptions () { WriteIndented = true }));
                outputStream.Seek (0, SeekOrigin.Begin);
                var reader = new StreamReader (outputStream);
                Console.Write (reader.ReadToEnd());
            }
        }

        public static async Task UpdateDependencies (Stream inputStream, Stream outputStream, Artifacts artifact) {
            Regex regex = new Regex(@"[\W]?Item \(.(https.*MonoFramework-MDK.*).,");
            using (var reader = new StreamReader (inputStream)) {
                var writer = new StreamWriter (outputStream);
                string line;
                while ((line = await reader.ReadLineAsync ()) != null) {
                    //var line = await reader.ReadLineAsync ();
                    writer.WriteLine (regex.Replace (line, $"Item (\"{artifact.url}\","));
                }
                writer.Flush();
            }
        }

        public static async Task<StatusResult> GetStatuses (string commit)
        {
            client.DefaultRequestHeaders.Add ("Accept", "*/*");
            client.DefaultRequestHeaders.Add ("User-Agent", "curl/7.54.0");
            var location = new Uri($"https://api.github.com/repos/mono/mono/commits/{commit}/status");
            var stream = await client.GetStreamAsync (location);
            var result = await JsonSerializer.DeserializeAsync<StatusResult> (stream);
            return result;
        }
    }

    public class Artifacts {
        public string url { get; set; }
        public string sha256 { get; set; }
        public string md5 { get; set; }
        public long size { get; set; }
        public string productId { get; set; }
        public string releaseId { get; set; }
        public string version { get; set; }
    }

    public class MonoExternal {
        public string url { get; set; }
        public string version { get; set; }
        public bool uploaded { get; set; } = true;
        public string repo { get; set; } = "git@github.com:mono/mono";
        public string commit { get; set; }
        public string tag { get; set; } = "";
        public string productId { get; set; }
        public string releaseId { get; set; }
        public string sha256 { get; set; }
        public string md5 { get; set; }
        public long size { get; set; }
    }

    public class StatusResult {
        public Status[] statuses { get; set; }
        public string sha { get; set; }
    }
    public class Status {
        public string url { get; set; }
        public string avatar_url { get; set; }
        public long id { get; set; }
        public string node_id { get; set; }
        public string state { get; set; }
        public string description { get; set; }
        public string target_url { get; set; }
        public string context { get; set; }
        public string created_at { get; set; }
        public string updated_at { get; set; }
    }
}
