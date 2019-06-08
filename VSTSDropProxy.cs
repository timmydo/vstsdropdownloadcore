using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web;

using Polly;
using System.Net.Sockets;
using System.Threading.Tasks.Dataflow;
using System.Threading;

namespace DropDownloadCore
{
    //use vsts drop rest api for manifest and blob and a PAT to grab urls to files and hackily materialize them.
    public class VSTSDropProxy
    {
        private const int ConcurrentDownloadCount = 50;
        // sigh these went public moths ago. Check if we can use non preview versions
        private const string ManifestAPIVersion = "2.0-preview";
        private const string BlobAPIVersion = "2.1-preview";

        private readonly IDropApi _dropApi = null;
        private readonly HttpClient _contentClient;

        private readonly Uri _VSTSDropUri;
        private readonly string _relativeroot;

        private readonly IList<VstsFile> _files;

        public VSTSDropProxy(string VSTSDropUri, string path, string pat, TimeSpan blobtimeout)
        {
            _dropApi = new RestfulDropApi(pat);
            _contentClient = new HttpClient() { Timeout = blobtimeout };

            if (!Uri.TryCreate(VSTSDropUri, UriKind.Absolute, out _VSTSDropUri))
            {
                throw new ArgumentException($"VSTS drop URI invalid {VSTSDropUri}", nameof(VSTSDropUri));
            }

            if (path == null)
            {
                throw new ArgumentException($"VSTS drop URI must contain a ?root= querystring {_VSTSDropUri}", nameof(VSTSDropUri));
            }

            _relativeroot = path.Replace('\\', Path.DirectorySeparatorChar);
            if (!_relativeroot.StartsWith("/"))
            {
                _relativeroot = $"/{_relativeroot}";
            }

            if (!_relativeroot.EndsWith("/"))
            {
                _relativeroot += "/";
            }

            //move this to a lazy so we can actually be async?
            try
            {
                var manifesturi = Munge(_VSTSDropUri, ManifestAPIVersion);
                _files = _dropApi.GetVstsManifest(manifesturi, BlobAPIVersion, _relativeroot).Result;
            }
            catch (Exception)
            {
                Console.WriteLine($"Not able to get build manifest please check your build '{VSTSDropUri}'");
                throw;
            }

            if (!_files.Any())
            {
                throw new ArgumentException("Encountered empty build drop check your build " + VSTSDropUri);
            }
            //https://1eswiki.com/wiki/CloudBuild_Duplicate_Binplace_Detection
        }

        /// <summary>
        /// Gets the manifest uri from the drop url
        /// </summary>
        /// <param name="vstsDropUri">The drop url.</param>
        /// <param name="apiVersion">API version to request.</param>
        /// <returns>The manifest uri.</returns>
        private static Uri Munge(Uri vstsDropUri, string apiVersion)
        {
            string manifestpath = vstsDropUri.AbsolutePath.Replace("_apis/drop/drops", "_apis/drop/manifests");
            var uriBuilder = new UriBuilder(vstsDropUri.Scheme, vstsDropUri.Host, -1, manifestpath);
            var queryParameters = HttpUtility.ParseQueryString(uriBuilder.Query);
            queryParameters.Add(RestfulDropApi.APIVersionParam, apiVersion);
            uriBuilder.Query = queryParameters.ToString();
            return uriBuilder.Uri;
        }

        private async Task Download(string sasurl, string localpath)
        {
            await Policy
                .Handle<HttpRequestException>()
                .Or<SocketException>()
                .Or<IOException>()
                .Or<TaskCanceledException>()
                .WaitAndRetryAsync(10,
                    retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    (e, t) =>
                    {
                        Console.WriteLine($"Exception {e} on {sasurl} -> {localpath}");
                        if (File.Exists(localpath))
                        {
                            File.Delete(localpath);
                        }
                    })
                .ExecuteAsync(async () =>
                {
                    using (var blob = await _contentClient.GetStreamAsync(sasurl))
                    using (var fileStream = new FileStream(localpath, FileMode.CreateNew))
                    {
                        await blob.CopyToAsync(fileStream);
                    }
                });
        }

        public async Task<Dictionary<string, double>> Materialize(string localDestination)
        {
            var uniqueblobs = _files.GroupBy(keySelector: file => file.Blob.Id, resultSelector: (key, file) => file).ToList();
            Console.WriteLine($"Found {_files.Count} files, {uniqueblobs.Count} unique");
            var metrics = new Dictionary<string, double>
            {
                ["files"] = _files.Count,
                ["uniqueblobs"] = uniqueblobs.Count
            };

            var dltimes = new ConcurrentBag<double>();
            var copytimes = new ConcurrentBag<double>();
            var throttler = new ActionBlock<IEnumerable<VstsFile>>(list => DownloadGrouping(list, localDestination, dltimes, copytimes), new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = ConcurrentDownloadCount });

            foreach (var grouping in uniqueblobs)
            {
                throttler.Post(grouping);
            }

            throttler.Complete();
            await throttler.Completion;

            metrics["AverageDownloadSecs"] = dltimes.Average();
            metrics["MaxDownloadSecs"] = dltimes.Max();
            metrics["AverageCopySecs"] = copytimes.Average();
            metrics["MaxCopySecs"] = copytimes.Max();
            return metrics;
        }

        private async Task DownloadGrouping(IEnumerable<VstsFile> group, string localDestination, ConcurrentBag<double> dltimes, ConcurrentBag<double> copytimes)
        {
            var f = group.First();
            var relativepath = f.Path.Substring(_relativeroot.Length);
            var localPath = Path.Combine(localDestination, relativepath).Replace('\\', Path.DirectorySeparatorChar);
            var sw = Stopwatch.StartNew();
            EnsureDirectory(localPath);
            await Download(f.Blob.Url, localPath);
            sw.Stop();
            var downloadTimeMilliseconds = sw.Elapsed.TotalMilliseconds;
            dltimes.Add(sw.Elapsed.TotalSeconds);

            sw.Restart();
            foreach (var other in group.Skip(1))
            {
                var otherrelativepath = other.Path.Substring(_relativeroot.Length);
                var otherpath = Path.Combine(localDestination, otherrelativepath).Replace('\\', Path.DirectorySeparatorChar);
                EnsureDirectory(otherpath);
                File.Copy(localPath, otherpath);
            }

            copytimes.Add(sw.Elapsed.TotalSeconds);
            Console.WriteLine($"Downloaded {f.Blob.Url} in {downloadTimeMilliseconds} ms");
        }

        private void EnsureDirectory(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
        }
    }

    // Helper classes for parsing VSTS drop exe output lowercase to match json output
    public sealed class VstsFile
    {
        public string Path { get; set; }

        public VstsBlob Blob { get; set; }

        public override int GetHashCode() { return StringComparer.OrdinalIgnoreCase.GetHashCode(Path); }
    }

    public sealed class VstsBlob
    {
        public string Url;

        public string Id;
    }
}
