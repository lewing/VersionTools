using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Linq;
using System.Text.Json;

#nullable enable

namespace EngUpdater
{
    class GitHub {
        static HttpClient client = new HttpClient();

        public static Task<Stream> GetRaw (string repo, string version, string path, bool verbose = false)
        {
            var uri = new Uri ($"https://raw.githubusercontent.com/{repo}/{version}/{path}");
            if (verbose)
                Console.WriteLine ($"GetRaw {uri}");
            return client.GetStreamAsync (uri);
        }

        static ValueTask<T> DeserializeAsync<T> (Stream stream, T example)
        {
            return JsonSerializer.DeserializeAsync<T> (stream);
        }

        public static async Task<string?> GetBranchHead(string repo, string branch, bool verbose = false)
        {
            if (String.IsNullOrEmpty (repo))
                throw new ArgumentNullException (nameof(repo));
            if (String.IsNullOrEmpty (branch))
                throw new ArgumentNullException (nameof(branch));

            string uriString = $"https://api.github.com/repos/{repo}/git/ref/heads/{branch}";
            try {
                var shaJsonElement = await GetJsonFromApiRequest (uriString, json_path: "object/sha", verbose: verbose);
                return shaJsonElement.GetString();
            } catch (HttpRequestException hre) {
                if (verbose) {
                    Console.WriteLine ($"github request failed with: {hre.ToString ()}, for uri: {uriString}.");
                }

                return null;
            }
        }

        public static async Task<JsonElement> GetJsonFromApiRequest (string uri_string,
                                                                     IDictionary<string, string>? additional_request_headers = null,
                                                                     string? json_path = null,
                                                                     bool verbose = false)
        {
            client.DefaultRequestHeaders.Add ("Accept", "*/*");
            client.DefaultRequestHeaders.Add ("User-Agent", "curl/7.54.0");

            if (additional_request_headers != null) {
                foreach (var kvp in additional_request_headers)
                    client.DefaultRequestHeaders.Add (kvp.Key, kvp.Value);
            }

            var stream = await client.GetStreamAsync (new Uri (uri_string));
            var jdoc = JsonDocument.Parse(stream);
            if (!string.IsNullOrEmpty (json_path) && JsonHelper.TryGetElementByPath (jdoc.RootElement, json_path, out var resultJsonElement))
                return resultJsonElement;
            
            return jdoc.RootElement;
        }

        public static async Task<(bool result, string html_url, int pr_number)> CreatePullRequest (
                                                            string target_owner_repo,
                                                            string base_branch_name,
                                                            string head_ref,
                                                            string personal_access_token,
                                                            string title,
                                                            string? body = null,
                                                            bool verbose = false)
        {
            if (String.IsNullOrEmpty (personal_access_token))
                throw new ArgumentException (nameof(personal_access_token));

            client.DefaultRequestHeaders.Add ("Accept", "*/*");
            client.DefaultRequestHeaders.Add ("User-Agent", "curl/7.54.0");
            client.DefaultRequestHeaders.Add ("Authorization", $"token {personal_access_token}");

            var paramsJson = JsonSerializer.Serialize (new {
                title = title,
                @base = base_branch_name,
                head = head_ref,
                body = body
                // draft = true
            });
            var content = new StringContent(paramsJson, Encoding.UTF8, "application/json");

            string uriString = $"https://api.github.com/repos/{target_owner_repo}/pulls";
            var response = await client.PostAsync (uriString, content);

            if (!response.IsSuccessStatusCode) {
                Console.WriteLine ($"Error: Failed to create PR: {response.ReasonPhrase} {await response.Content.ReadAsStringAsync ()}, for uri: {uriString}");
            } else {
                var jdoc = JsonDocument.Parse (await response.Content.ReadAsStringAsync ());
                if (jdoc.RootElement.TryGetProperty ("html_url", out var urlJsonElement) &&
                    jdoc.RootElement.TryGetProperty ("number", out var numberJsonElement)) {
                    return (result: true, html_url: urlJsonElement.GetString (), pr_number: numberJsonElement.GetInt32 ());
                }
            }

            return (result: false, String.Empty, -1);
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

        public static async IAsyncEnumerable<PullRequest?> GetPullRequests(string api_uri_string, bool verbose = false)
        {
            if (String.IsNullOrEmpty (api_uri_string))
                throw new ArgumentNullException (nameof(api_uri_string));

            var je = await GitHub.GetJsonFromApiRequest (api_uri_string, verbose: verbose);
            if (je.ValueKind == JsonValueKind.Array) {
                foreach (var pe in je.EnumerateArray ())
                    yield return ParsePullRequest (pe);
            } else {
                yield return ParsePullRequest (je);
            }

            PullRequest? ParsePullRequest (JsonElement pe)
            {
                if (pe.ValueKind == JsonValueKind.Object &&
                    pe.TryGetProperty ("html_url", out var htmlUrlJsonElement) &&
                    pe.TryGetProperty ("number", out var numberJsonElement) &&
                    JsonHelper.TryGetElementByPath (pe, "head/ref", out var headRefJsonElement) &&
                    JsonHelper.TryGetElementByPath (pe, "head/repo/owner/login", out var headOwnerJsonElement) &&
                    JsonHelper.TryGetElementByPath (pe, "base/ref", out var baseRefJsonElement)) {
                        
                    return new PullRequest (
                        HTML_URL            : htmlUrlJsonElement.GetString (),
                        HeadRepoOwner       : headOwnerJsonElement.GetString (),
                        Number              : numberJsonElement.GetInt32 (),
                        HeadRef             : headRefJsonElement.GetString (),
                        BaseRef             : baseRefJsonElement.GetString ()
                    );
                }

                return null;
            }
        }
    }

    public class JsonHelper
    {
        public static bool TryGetElementByPath (JsonElement je, string path, out JsonElement resultJsonElement)
        {
            JsonElement current = je;
            foreach (var pe in path.Split(new char[]{'/'}, StringSplitOptions.RemoveEmptyEntries)) {
                if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty (pe, out var propJsonElement)) {
                    current = propJsonElement;
                }
                else {
                    resultJsonElement = current;
                    return false;
                }
            }

            resultJsonElement = current;
            return true;
        }
    }

    public class PullRequest
    {
        public string HTML_URL { get; }
        public string HeadRepoOwner { get; }
        public int Number { get; }
        public string HeadRef { get; }
        public string BaseRef { get; }

        public PullRequest (string HTML_URL, string HeadRepoOwner, int Number, string HeadRef, string BaseRef)
        {
            this.HTML_URL = HTML_URL;
            this.HeadRepoOwner = HeadRepoOwner;
            this.Number = Number;
            this.HeadRef = HeadRef;
            this.BaseRef = BaseRef;
        }
    }

}
