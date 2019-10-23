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

/* 
[
    {
        "url": "https://xamjenkinsartifact.azureedge.net/build-package-osx-mono/2019-08/137/1b2e536b2238aa6bd86722996bcd1cf0f76cd6f3/MonoFramework-MDK-6.6.0.140.macos10.xamarin.universal.pkg",
        "sha256": "26668943d1c32f09582a73ee8d5e4274dfa8d2aa936520e7f2c4cdaf76a963d6",
        "md5": "bd8df61072a018dfd4d0c72ae5b62349",
        "size": 357293878,
        "productId": "964ebddd-1ffe-47e7-8128-5ce17ffffb05",
        "releaseId": "606000140",
        "version": "6.6.0.140"
    }
]
*/

/*
{
    "url": "https://xamjenkinsartifact.azureedge.net/build-package-osx-mono/2019-06/177/7da9a041b3b69d2f6ac04f8c2b2a1814d4d468f4/MonoFramework-MDK-6.4.0.194.macos10.xamarin.universal.pkg",
    "version": "6.4.0.194",
    "url": "https://xamjenkinsartifact.azureedge.net/build-package-osx-mono/2019-06/180/fe64a4765e6d1dbb41d5c86708fcb02aa519247a/MonoFramework-MDK-6.4.0.198.macos10.xamarin.universal.pkg",
    "version": "6.4.0.198",
    "uploaded": true,
    "repo": "git@github.com:mono/mono",
    "commit": "7da9a041b3b69d2f6ac04f8c2b2a1814d4d468f4",
    "tag": "",
    "productId": "964ebddd-1ffe-47e7-8128-5ce17ffffb05",
    "releaseId": "604000194",
    "sha256": "12834b45db0050b55556291f59f76bb1c0e2b37d336d57eb19c1cd0db147692a",
    "md5": "6efd44319a4fc25dda16b9e6bdf07e8e",
    "size": 353257385,
    "releaseId": "604000198",
    "sha256": "07f3622e5ec47ed000c9668702d45acf19f49602c10b0aaa055cc7e454cdc695",
    "md5": "97c876c536dc5049265577cb4d0f902b",
    "size": 352894286,
}
 */

namespace InsertMonoAddin
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var artifactsUri = args [0];
            string mdAddinsPath = null; //args [1];

            await UpdateMdAddins (artifactsUri, mdAddinsPath);
        }

        public static async Task UpdateMdAddins (string artifactsUri, string mdAddinsPath) {
            var artifactsStream = File.OpenRead (artifactsUri);
            var artifact = (await JsonSerializer.DeserializeAsync<Artifacts[]>(artifactsStream))[0];
            
            var parts = artifact.url.Split (new [] {'/'});
            var commit = parts[parts.Length - 2];
            Console.WriteLine ($"commit = {commit}");
            var mono = new MonoExternal (){
                url = artifact.url,
                version = artifact.version,
                productId = artifact.productId,
                releaseId = artifact.releaseId,
                sha256 = artifact.sha256,
                md5 = artifact.md5,
                size = artifact.size
            };

            Console.WriteLine (JsonSerializer.Serialize(mono, new JsonSerializerOptions () { WriteIndented = true }));
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
        public string tag { get; set; } = "";
        public string productId { get; set; }
        public string releaseId { get; set; }
        public string sha256 { get; set; }
        public string md5 { get; set; }
        public long size { get; set; }
    }
}
