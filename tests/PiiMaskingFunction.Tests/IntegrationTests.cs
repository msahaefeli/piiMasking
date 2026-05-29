using System;
using System;
using System.Collections.Generic;
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
// Azure SDK types are not directly required in this integration test file;
// use the internal PiiMaskingService and its types to interact with the service.
using test.CommonFunctions.AzureFunction.PiiMasking.Internal;

namespace PiiMaskingFunction.Tests
{
    public class IntegrationTests
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
        public async Task E2E_Run_All_Pii_Types_Check_Masking_Policy()
        {
            // Ensure credentials are available; tests will be skipped if not
            var creds = LoadCredentialsLocal();
            if (string.IsNullOrWhiteSpace(creds.endpoint) || string.IsNullOrWhiteSpace(creds.key))
            {
                throw new Xunit.Sdk.SkipException("TEXT_ANALYTICS_ENDPOINT and TEXT_ANALYTICS_KEY must be configured to run end-to-end tests");
            }

            Environment.SetEnvironmentVariable("TEXT_ANALYTICS_ENDPOINT", creds.endpoint);
            Environment.SetEnvironmentVariable("TEXT_ANALYTICS_KEY", creds.key);

            var samples = new Dictionary<string, string>
            {
                { "Person", "山田太郎です。" },
                { "PhoneNumber", "電話番号は090-1234-5678です。" },
                { "Email", "メールは test@example.com です。" },
                { "CreditCardNumber", "クレジットカード番号は4111111111111111です。" },
                { "DateTime", "生年月日は1990年1月1日です。" },
                { "Location", "住所は東京都千代田区1-1です。" },
                { "IPAddress", "IPは192.168.0.1です。" },
                { "Url", "ウェブサイトは https://example.com です。" },
                { "USSocialSecurityNumber", "社会保障番号は 123-45-6789 です。" }
                ,
                // Combined sample containing all PII kinds in one input
                { "AllCombined", "山田太郎です。 電話番号は090-1234-5678です。 メールは test@example.com です。 クレジットカード番号は4111111111111111です。 生年月日は1990年1月1日です。 住所は東京都千代田区1-1です。 IPは192.168.0.1です。 ウェブサイトは https://example.com です。 社会保障番号は 123-45-6789 です。" }
            };

            var client = PiiMaskingService.CreateTextAnalyticsClient();
            if (client == null)
            {
                var fallbackCreds = LoadCredentialsLocal();
                if (string.IsNullOrWhiteSpace(fallbackCreds.endpoint) || string.IsNullOrWhiteSpace(fallbackCreds.key))
                {
                    throw new Xunit.Sdk.SkipException("TEXT_ANALYTICS_ENDPOINT and TEXT_ANALYTICS_KEY must be configured to run end-to-end tests");
                }
                Environment.SetEnvironmentVariable("TEXT_ANALYTICS_ENDPOINT", fallbackCreds.endpoint);
                Environment.SetEnvironmentVariable("TEXT_ANALYTICS_KEY", fallbackCreds.key);
                client = PiiMaskingService.CreateTextAnalyticsClient();
            }

            Assert.NotNull(client);

            var results = new List<object>();
            var failed = new List<object>();

            foreach (var kv in samples)
            {
                var category = kv.Key;
                var text = kv.Value;

            List<test.CommonFunctions.AzureFunction.PiiMasking.Internal.PiiEntity> entities;
                try
                {
                    entities = await PiiMaskingService.DetectPiiEntitiesAsync(client!, text, NullLogger.Instance, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    results.Add(new { category, input = text, error = ex.Message, success = false });
                    failed.Add(new { category, input = text, reason = "detect_failed", error = ex.Message });
                    continue;
                }

                var maskingResult = PiiMaskingService.ProcessMasking(text, entities);

                var maskCats = maskingResult.MaskCategoriesUsed ?? Array.Empty<string>();

                bool success = true;
                object rec;

                if (string.Equals(category, "AllCombined", StringComparison.OrdinalIgnoreCase))
                {
                    // For combined sample, validate per-entity masking according to runtime mask policy
                    var perEntityResults = new List<object>();
                    foreach (var e in maskingResult.Entities)
                    {
                        bool inPolicy = maskCats.Any(c => string.Equals(c, e.Category, StringComparison.OrdinalIgnoreCase));
                        bool containsRawEntity = maskingResult.MaskedTranscript.Contains(e.Text);
                        bool entityOk = inPolicy ? !containsRawEntity : containsRawEntity;
                        perEntityResults.Add(new { category = e.Category, text = e.Text, inPolicy, containsRawEntity, entityOk });
                        if (!entityOk) success = false;
                    }

                    rec = new { category, input = text, masked = maskingResult.MaskedTranscript, maskPolicy = maskCats, perEntity = perEntityResults, success };
                    results.Add(rec);
                    if (!success) failed.Add(rec);
                }
                else
                {
                    bool shouldMask = maskCats.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));

                    bool maskedPresent = maskingResult.MaskedTranscript.Contains("*");
                    bool containsRaw = maskingResult.MaskedTranscript.Contains(text);

                    if (shouldMask)
                    {
                        success = maskedPresent && !containsRaw;
                    }
                    else
                    {
                        success = !maskedPresent || containsRaw;
                    }

                    rec = new { category, input = text, masked = maskingResult.MaskedTranscript, entities = maskingResult.Entities.Select(e => e.Category), maskPolicy = maskCats, success };
                    results.Add(rec);
                    if (!success) failed.Add(rec);
                }
            }

            // write results for inspection
            try
            {
                var outDir = Environment.GetEnvironmentVariable("PII_INTEG_TEST_RESULTS_PATH");
                if (string.IsNullOrWhiteSpace(outDir)) outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos", "piiMasking", "TestResults");
                Directory.CreateDirectory(outDir);
                var outFile = Path.Combine(outDir, $"e2e_integration_results_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jsonl");
                using (var sw = File.CreateText(outFile))
                {
                    foreach (var r in results)
                    {
                        sw.WriteLine(JsonConvert.SerializeObject(r));
                    }
                }
                Console.WriteLine($"E2E integration results written to: {outFile}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to write E2E results: {ex}");
            }

            if (failed.Any())
            {
                var msg = "Some E2E masking checks failed:\n" + string.Join("\n", failed.Select(f => JsonConvert.SerializeObject(f)));
                Assert.True(false, msg);
            }
        }
    }
}
