#if NET45
// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Linq;
using System.Security.Cryptography;

namespace Microsoft.Framework.PackageManager.Restore.NuGet
{
    internal class HttpSource
    {
        private HttpClient _client;

        private string _baseUri;
        private string _userName;
        private string _password;
        private IReport _report;
        
        public HttpSource(
            string baseUri,
            string userName,
            string password,
            IReport report)
        {
            _baseUri = baseUri + (baseUri.EndsWith("/") ? "" : "/");
            _userName = userName;
            _password = password;
            _report = report;

            var proxy = Environment.GetEnvironmentVariable("http_proxy");
            if (string.IsNullOrEmpty(proxy))
            {
                _client = new HttpClient();
            }
            else
            {
                // To use an authenticated proxy, the proxy address should be in the form of
                // "http://user:password@proxyaddress.com:8888"
                var proxyUriBuilder = new UriBuilder(proxy);
                var webProxy = new WebProxy(proxy);
                if (string.IsNullOrEmpty(proxyUriBuilder.UserName))
                {
                    // If no credentials were specified we use default credentials
                    webProxy.Credentials = CredentialCache.DefaultCredentials;
                }
                else
                {
                    ICredentials credentials = new NetworkCredential(proxyUriBuilder.UserName,
                        proxyUriBuilder.Password);
                    webProxy.Credentials = credentials;
                }

                var handler = new HttpClientHandler
                {
                    Proxy = webProxy,
                    UseProxy = true
                };
                _client = new HttpClient(handler);
            }
        }

        internal async Task<HttpSourceResult> GetAsync(string uri, string cacheKey, TimeSpan cacheAgeLimit)
        {
            Stopwatch sw = new Stopwatch();
            sw.Start();

            var result = TryCache(uri, cacheKey, cacheAgeLimit);
            if (result.Stream != null)
            {
                _report.WriteLine(string.Format("  {0} {1}", "CACHE".Green(), uri));
                return result;
            }

            _report.WriteLine(string.Format("  {0} {1}", "GET".Yellow(), uri));

            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (_userName != null)
            {
                var token = Convert.ToBase64String(Encoding.ASCII.GetBytes(_userName + ":" + _password));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
            };

            var response = await _client.SendAsync(request);

            var newFile = result.CacheFileName + "-new";

            // Zero value of TTL means we always download the latest package
            // So we write to a temp file instead of cache
            if (cacheAgeLimit.Equals(TimeSpan.Zero))
            {
                result.CacheFileName = Path.GetTempFileName();
                newFile = Path.GetTempFileName();
            }

            using (var stream = new FileStream(
                newFile,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 8192,
                useAsync: true))
            {
                await response.Content.CopyToAsync(stream);
                await stream.FlushAsync();
            }

            if (File.Exists(result.CacheFileName))
            {
                File.Delete(result.CacheFileName);
            }

            File.Move(
                newFile,
                result.CacheFileName);

            result.Stream = new FileStream(
                result.CacheFileName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 8192,
                useAsync: true);

            _report.WriteLine(string.Format("  {1} {0} {2}ms", uri, response.StatusCode.ToString().Green(), sw.ElapsedMilliseconds.ToString().Bold()));

            return result;
        }

        private HttpSourceResult TryCache(string uri, string cacheKey, TimeSpan cacheAgeLimit)
        {
            var baseFolderName = RemoveInvalidFileNameChars(ComputeHash(_baseUri));
            var baseFileName = RemoveInvalidFileNameChars(cacheKey) + ".dat";

            var localAppDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var cacheFolder = Path.Combine(localAppDataFolder, "kpm", "cache", baseFolderName);
            var cacheFile = Path.Combine(cacheFolder, baseFileName);

            if (!Directory.Exists(cacheFolder) && !cacheAgeLimit.Equals(TimeSpan.Zero))
            {
                Directory.CreateDirectory(cacheFolder);
            }

            if (File.Exists(cacheFile))
            {
                var fileInfo = new FileInfo(cacheFile);

                var age = DateTime.UtcNow.Subtract(fileInfo.LastWriteTimeUtc);
                if (age < cacheAgeLimit)
                {
                    var stream = new FileStream(
                        cacheFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete,
                        bufferSize: 8192,
                        useAsync: true);

                    return new HttpSourceResult
                    {
                        CacheFileName = cacheFile,
                        Stream = stream,
                    };
                }
            }

            return new HttpSourceResult
            {
                CacheFileName = cacheFile
            };
        }

        private static string ComputeHash(string value)
        {
            var trailing = value.Length > 32 ? value.Substring(value.Length - 32) : value;
            var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var hex = "0123456789abcdef";
            return hash.Aggregate("$" + trailing, (result, ch) => "" + hex[ch / 0x10] + hex[ch % 0x10] + result);
        }

        private static string RemoveInvalidFileNameChars(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new String(
                    value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()
                )
                .Replace("__", "_")
                .Replace("__", "_");
        }
    }
}
#endif