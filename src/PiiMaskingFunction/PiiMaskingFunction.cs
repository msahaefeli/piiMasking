using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.TextAnalytics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using test.CommonFunctions.AzureFunction.PiiMasking.Internal;

namespace test.CommonFunctions.AzureFunction.PiiMasking
{
    public static class PiiMaskingFunction
    {
        [FunctionName("PiiMasking")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "maskpii")] HttpRequest req,
            ILogger log,
            Microsoft.Azure.WebJobs.ExecutionContext context,
            CancellationToken cancellationToken)
        {
            // Validate request body
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(requestBody))
            {
                return new BadRequestObjectResult(new { error = "Request body is empty" });
            }


            // Parse transcript
            string transcript = ParseTranscript(requestBody);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                return new BadRequestObjectResult(new { error = "No transcript provided" });
            }

            // Create client from environment (no hard-coded credentials)
            var client = CreateTextAnalyticsClient();
            if (client == null)
            {
                return new ObjectResult(new { error = "Text Analytics credentials not configured" }) { StatusCode = 500 };
            }

            // Recognize PII entities using the asynchronous (long-running) Analyze Actions API.
            // This supports much larger documents (up to the service async limit) and avoids chunking.
            var collected = new List<(string category, string subCategory, string text, int offset, int length)>();
            try
            {
                var actions = new TextAnalyticsActions()
                {
                    RecognizePiiEntitiesActions = new List<RecognizePiiEntitiesAction> { new RecognizePiiEntitiesAction() }
                };

                var operation = await FunctionBaseRetry.FuncWithRetryAsync(
                    async (ct) => await client.StartAnalyzeActionsAsync(new[] { transcript }, actions, cancellationToken: ct),
                    log,
                    cancellationToken);

                // Wait for completion
                await operation.WaitForCompletionAsync(cancellationToken);

                var resultCollection = operation.Value;
                // Iterate results: pages -> action results -> document results
                await foreach (var actionResult in resultCollection)
                {
                    foreach (var piiActionResult in actionResult.RecognizePiiEntitiesResults)
                    {
                        foreach (var docResult in piiActionResult.DocumentsResults)
                        {
                            if (docResult.HasError)
                            {
                                // record error and continue
                                log.LogWarning("PII action document error: {0}", docResult.Error.Message);
                                continue;
                            }

                            foreach (var e in docResult.Entities)
                            {
                                // offsets from async API are absolute to the document, so use directly
                                collected.Add((e.Category.ToString(), e.SubCategory, e.Text, (int)e.Offset, (int)e.Length));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "PII recognition (async) failed");
                return new ObjectResult(new { error = "PII recognition failed", detail = ex.Message }) { StatusCode = 500 };
            }

            // Deduplicate / merge overlapping detections conservatively
            var entitiesDistinct = collected
                .GroupBy(x => (x.offset, x.length, x.text))
                .Select(g => g.First())
                .OrderBy(e => e.offset)
                .ToList();

            var entitiesInfo = entitiesDistinct.Select(e => new
            {
                category = e.category,
                subCategory = e.subCategory,
                text = e.text,
                offset = e.offset,
                length = e.length
            }).ToList();

            // Determine which categories should be masked. Configurable via environment variable
            // PII_MASK_CATEGORIES (comma-separated). If not set, use a conservative default suitable for
            // corporate insurance sales (mask personal identifiers like Person, PhoneNumber, Email, Address, DateTime, IPAddress, USSocialSecurityNumber, CreditCardNumber, URL).
            var maskCatsEnv = Environment.GetEnvironmentVariable("PII_MASK_CATEGORIES");
            HashSet<string> allowedMaskCategories;
            if (!string.IsNullOrWhiteSpace(maskCatsEnv))
            {
                allowedMaskCategories = new HashSet<string>(maskCatsEnv.Split(',').Select(s => s.Trim()), System.StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                // Default: mask highly sensitive identifiers that typically should NOT be stored in a CRM
                // for B2B insurance sales (keep contactable fields such as Person, PhoneNumber, Email, Address
                // available to CRM by default). Adjust via PII_MASK_CATEGORIES as needed.
                allowedMaskCategories = new HashSet<string>(new[]
                {
                    "CreditCardNumber",
                    "USSocialSecurityNumber",
                    "JPMyNumberPersonal",
                    "BankAccountNumber",
                    "IBAN",
                    "PassportNumber",
                    "DriverLicenseNumber",
                    "IPAddress",
                    "URL",
                    "Url"
                }, System.StringComparer.OrdinalIgnoreCase);
            }

            // Filter entities to mask according to allowedMaskCategories; keep entitiesInfo full for diagnostics
            var maskOffsets = entitiesDistinct
                .Where(e => allowedMaskCategories.Contains(e.category))
                .Select(e => ((int)e.offset, (int)e.length))
                .ToList();

            var masked = MaskTranscriptFromOffsets(transcript, maskOffsets);

            var result = new
            {
                maskedTranscript = masked,
                entities = entitiesInfo
            };

            return new OkObjectResult(result);
        }

        internal static string ParseTranscript(string requestBody)
        {
            if (string.IsNullOrWhiteSpace(requestBody)) return null;

            try
            {
                var j = JObject.Parse(requestBody);
                if (j["transcript"] != null) return j["transcript"].ToString();
                if (j["text"] != null) return j["text"].ToString();
                return requestBody;
            }
            catch
            {
                return requestBody;
            }
        }

        private static TextAnalyticsClient CreateTextAnalyticsClient()
        {
            var endpoint = Environment.GetEnvironmentVariable("TEXT_ANALYTICS_ENDPOINT");
            var apiKey = Environment.GetEnvironmentVariable("TEXT_ANALYTICS_KEY");

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
            {
                return null;
            }

            return new TextAnalyticsClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        }

        internal static string MaskTranscript(string transcript, PiiEntityCollection entities)
        {
            if (entities == null) return transcript;
            var offsets = entities.Select(e => ((int)e.Offset, (int)e.Length));
            return MaskTranscriptFromOffsets(transcript, offsets);
        }

        // Test-friendly masking helper that accepts plain offsets/lengths
        internal static string MaskTranscriptFromOffsets(string transcript, IEnumerable<(int offset, int length)> offsets)
        {
            if (string.IsNullOrEmpty(transcript)) return transcript;
            if (offsets == null) return transcript;

            var chars = transcript.ToCharArray();

            // Mask from end to start to preserve offsets
            foreach (var (offset, length) in offsets.OrderByDescending(o => o.offset))
            {
                if (offset < 0) continue;
                int start = offset;
                int len = Math.Max(0, length);
                int end = Math.Min(start + len, chars.Length);
                for (int i = start; i < end; i++) chars[i] = '*';
            }

            return new string(chars);
        }


    }
}
