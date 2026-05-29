using System;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using test.CommonFunctions.AzureFunction.PiiMasking.Internal;

namespace PiiMaskingFunction.Tests
{
    // Lightweight performance checks guarded by environment variable to avoid accidental live calls.
    public class PerformanceTests
    {
        private static (string endpoint, string key) LoadCredentialsLocal()
        {
            var endpoint = Environment.GetEnvironmentVariable("TEXT_ANALYTICS_ENDPOINT");
            var key = Environment.GetEnvironmentVariable("TEXT_ANALYTICS_KEY");
            if (!string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(key)) return (endpoint, key);

            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null)
            {
                var localPath = Path.Combine(dir.FullName, "src", "PiiMaskingFunction", "local.settings.json");
                if (File.Exists(localPath))
                {
                    var text = File.ReadAllText(localPath);
                    var j = JObject.Parse(text);
                    var vals = j["Values"] as JObject;
                    if (vals != null)
                    {
                        endpoint = endpoint ?? vals.Value<string>("TEXT_ANALYTICS_ENDPOINT");
                        key = key ?? vals.Value<string>("TEXT_ANALYTICS_KEY");
                    }
                    break;
                }

                var localPathRoot = Path.Combine(dir.FullName, "local.settings.json");
                if (File.Exists(localPathRoot))
                {
                    var text = File.ReadAllText(localPathRoot);
                    var j = JObject.Parse(text);
                    var vals = j["Values"] as JObject;
                    if (vals != null)
                    {
                        endpoint = endpoint ?? vals.Value<string>("TEXT_ANALYTICS_ENDPOINT");
                        key = key ?? vals.Value<string>("TEXT_ANALYTICS_KEY");
                    }
                    break;
                }

                dir = dir.Parent;
            }

            return (endpoint, key);
        }

        [Fact]
        public async Task Lightweight_Performance_SmokeTest()
        {
            // Performance smoke test: run unconditionally (will skip later if credentials are missing)

            var creds = LoadCredentialsLocal();
            if (string.IsNullOrWhiteSpace(creds.endpoint) || string.IsNullOrWhiteSpace(creds.key))
            {
                throw new Xunit.Sdk.SkipException("TEXT_ANALYTICS_ENDPOINT and TEXT_ANALYTICS_KEY must be configured to run performance tests.");
            }

            Environment.SetEnvironmentVariable("TEXT_ANALYTICS_ENDPOINT", creds.endpoint);
            Environment.SetEnvironmentVariable("TEXT_ANALYTICS_KEY", creds.key);

            var paragraph = "本日は会議を始めます。参加者は山田太郎、佐藤花子です。連絡先は090-1234-5678、メールはsample@example.comです。";
            var size = 5000; // moderate size for smoke

            var builder = new StringBuilder();
            while (builder.Length < size) builder.Append(paragraph);
            var input = builder.ToString();

            var json = JsonConvert.SerializeObject(new { transcript = input });
            var httpContext = new DefaultHttpContext();
            var request = httpContext.Request;
            request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
            request.ContentType = "application/json";

            var logger = NullLogger.Instance;
            var execContext = new Microsoft.Azure.WebJobs.ExecutionContext { FunctionName = "PiiMasking" };

            var sw = Stopwatch.StartNew();

            var client = PiiMaskingService.CreateTextAnalyticsClient();
            if (client == null)
            {
                var fallbackCreds = LoadCredentialsLocal();
                if (string.IsNullOrWhiteSpace(fallbackCreds.endpoint) || string.IsNullOrWhiteSpace(fallbackCreds.key))
                {
                    throw new Xunit.Sdk.SkipException("TEXT_ANALYTICS_ENDPOINT and TEXT_ANALYTICS_KEY must be configured to run performance tests.");
                }
                Environment.SetEnvironmentVariable("TEXT_ANALYTICS_ENDPOINT", fallbackCreds.endpoint);
                Environment.SetEnvironmentVariable("TEXT_ANALYTICS_KEY", fallbackCreds.key);
                client = PiiMaskingService.CreateTextAnalyticsClient();
            }

            Assert.NotNull(client);

            List<test.CommonFunctions.AzureFunction.PiiMasking.Internal.PiiEntity> entities = new();
            try
            {
                entities = await PiiMaskingService.DetectPiiEntitiesAsync(client!, input, NullLogger.Instance, CancellationToken.None);
            }
            catch (Exception ex)
            {
                sw.Stop();
                throw new Xunit.Sdk.SkipException("Live Text Analytics call failed: " + ex.Message);
            }

            var result = PiiMaskingService.ProcessMasking(input, entities);
            sw.Stop();

            Assert.NotNull(result);
            Assert.NotNull(result.MaskedTranscript);
            Console.WriteLine($"Performance smoke: elapsed ms = {sw.ElapsedMilliseconds}");
            Assert.InRange(sw.ElapsedMilliseconds, 0, 120000); // sanity bound
        }
    }
}
