using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Linq;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

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

            string gh_rest_url = $"/repos/{repo}/git/ref/heads/{branch}";
            try {
                return await GetJsonFromApiRequest<string> (gh_rest_url, json_path: "object/sha", verbose: verbose);
            } catch (KeyNotFoundException knfe) {
                Console.WriteLine ($"Failed to find relevant fields in the reponse: {knfe.ToString ()}, for uri: {gh_rest_url}.");
            } catch (HttpRequestException hre) {
                Console.WriteLine ($"github request failed with: {hre.ToString ()}, for uri: {gh_rest_url}.");
            }

            return null;
        }

        public static async Task<T> GetJsonFromApiRequest<T> (string gh_rest_url,
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

            var uri_string = $"https://api.github.com{gh_rest_url}";
            if (verbose)
                Console.WriteLine ($"GetJsonFromApiRequest {uri_string}");

            var stream = await client.GetStreamAsync (new Uri (uri_string));
            var jdoc = await JsonDocument.ParseAsync(stream);
            return jdoc.RootElement.Get<T>(json_path);
        }

        public static async Task<HttpResponseMessage> PostOrDeleteAPI (HttpMethod method,
                                                                       string gh_rest_url,
                                                                       string personal_access_token,
                                                                       string json_string,
                                                                       bool verbose = false)
        {
            if (method != HttpMethod.Post && method != HttpMethod.Delete)
                throw new ArgumentException($"Only HttpMethod.Post or HttpMethod.Delete are supported. Got {method}");

            if (String.IsNullOrEmpty (personal_access_token))
                throw new ArgumentException (nameof(personal_access_token));

            client.DefaultRequestHeaders.Add ("Accept", "*/*");
            client.DefaultRequestHeaders.Add ("User-Agent", "curl/7.54.0");
            if (!client.DefaultRequestHeaders.Contains ("Authorization"))
                client.DefaultRequestHeaders.Add ("Authorization", $"token {personal_access_token}");

            var uri_string = $"https://api.github.com{gh_rest_url}";
            if (verbose)
                Console.WriteLine ($"{method}: {uri_string}");

            if (method == HttpMethod.Post) {
                var content = new StringContent (json_string, Encoding.UTF8, "application/json");
                return await client.PostAsync(uri_string, content);
            } else if (method == HttpMethod.Delete) {
                return await client.DeleteAsync(uri_string);
            } else {
                throw new NotImplementedException(method.ToString ());
            }
        }

        public static async Task<(bool result, string html_url, int pr_number)> CreatePullRequest (string target_owner_repo,
                                                                                                   string base_branch_name,
                                                                                                   string head_ref,
                                                                                                   string personal_access_token,
                                                                                                   string title,
                                                                                                   string? body = null,
                                                                                                   string[]? reviewers = null,
                                                                                                   bool verbose = false)
        {
            var paramsJson = JsonSerializer.Serialize (new {
                title,
                @base = base_branch_name,
                head = head_ref,
                body
                // draft = true
            });

            string uriString = $"/repos/{target_owner_repo}/pulls";
            var response = await PostOrDeleteAPI (HttpMethod.Post, uriString, personal_access_token, paramsJson, verbose);

            if (!response.IsSuccessStatusCode) {
                Console.WriteLine ($"Error: Failed to create PR: {response.ReasonPhrase} {await response.Content.ReadAsStringAsync ()}, for uri: {uriString}");
            } else {
                var je = JsonDocument.Parse (await response.Content.ReadAsStringAsync ())
                                            .RootElement;

                try
                {
                    var html_url = je.Get<string>("html_url");
                    var pr_number = je.Get<int>("number");

                    if (reviewers != null)
                        await AddReviewers (target_owner_repo, pr_number, personal_access_token, reviewers, verbose);

                    return (result: true, html_url, pr_number);
                } catch (HttpRequestException hre) {
                    if (verbose)
                        Console.WriteLine ($"Adding reviewers failed: {hre.Message}");
                } catch (KeyNotFoundException knfe) {
                    if (verbose)
                        Console.WriteLine ($"Adding reviewers failed: {knfe.Message}");
                }
            }

            return (result: false, String.Empty, -1);
        }

        public static async Task<bool> AddReviewers (string target_owner_repo,
                                                     int pr_number,
                                                     string personal_access_token,
                                                     string[] reviewers,
                                                     bool verbose = false)
        {
            var paramsJson = JsonSerializer.Serialize (new {
                reviewers = reviewers
            });

            // POST /repos/:owner/:repo/pulls/:pull_number/requested_reviewers
            string gh_url = $"/repos/{target_owner_repo}/pulls/{pr_number}/requested_reviewers";
            var response = await PostOrDeleteAPI (HttpMethod.Post, gh_url, personal_access_token, paramsJson, verbose);

            if (response.StatusCode == HttpStatusCode.Created) {
                if (verbose)
                    Console.WriteLine ($"Added reviewers.");
            } else {
                Console.WriteLine ($"Error: Failed to add reviewers. {response.ReasonPhrase} {await response.Content.ReadAsStringAsync ()}, for uri: {gh_url}");
            } 

            return response.StatusCode == HttpStatusCode.Created;
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

        public static async IAsyncEnumerable<PullRequest?> GetPullRequests(string gh_rest_url, bool verbose = false)
        {
            if (String.IsNullOrEmpty (gh_rest_url))
                throw new ArgumentNullException (nameof(gh_rest_url));

            var je = await GitHub.GetJsonFromApiRequest<JsonElement> (gh_rest_url, verbose: verbose);
            if (je.ValueKind == JsonValueKind.Array) {
                foreach (var pe in je.EnumerateArray ())
                    yield return PullRequest.Parse(pe);
            } else {
                yield return PullRequest.Parse(je);
            }
        }
    }

    public static class JsonHelper
    {
        public static T Get<T> (this JsonElement je, string? path)
        {
            if (je.TryGet<T> (path, out var value))
                return value;

            throw new KeyNotFoundException($"Could not find '{je}' part of the path '{path}'");
        }

        public static bool TryGet<T> (this JsonElement je, string? path, [NotNullWhen(true)] out T value)
        {
            JsonElement current = je;
            if (path != null) {
                foreach (var pe in path.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries)) {
                    if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(pe, out var propJsonElement)) {
                        current = propJsonElement;
                    } else {
                        value = default(T)!;
                        return false;
                    }
                }
            }

            if (typeof(T) == typeof(JsonElement)) {
                value = (T)(object)current;
            } else {
                value = (T)Convert.ChangeType(current.GetRawText().Trim('"'), typeof(T));
            }

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

        public static PullRequest? Parse (JsonElement je, bool verbose = false)
        {
            if (je.ValueKind != JsonValueKind.Object)
                return null;

            try
            {
                return new PullRequest(
                   HTML_URL:        je.Get<string> ("html_url"),
                   HeadRepoOwner:   je.Get<string> ("head/repo/owner/login"),
                   Number:          je.Get<int>    ("number"),
                   HeadRef:         je.Get<string> ("head/ref"),
                   BaseRef:         je.Get<string> ("base/ref")
                );
            } catch (KeyNotFoundException knfe) {
                if (verbose)
                    Console.WriteLine ($"Error parsing PR : {knfe.Message}");
            }

            return null;
        }
    }

}
