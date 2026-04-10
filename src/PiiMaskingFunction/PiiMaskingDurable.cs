using System;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using test.CommonFunctions.AzureFunction.PiiMasking.Internal;

namespace test.CommonFunctions.AzureFunction.PiiMasking
{
    /// <summary>
    /// Durable Functionsを使用した非同期PIIマスキング処理
    /// Azure AI Languageへのリクエスト送信後、ステータスと結果の監視を別のFunctionで実施
    /// </summary>
    public static class PiiMaskingDurable
    {
        /// <summary>
        /// 非同期PIIマスキング処理を開始するHTTPトリガー
        /// POST /api/maskpii
        /// </summary>
        [FunctionName("PiiMasking_HttpStart")]
        public static async Task<IActionResult> HttpStart(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "maskpii")] HttpRequest req,
            [DurableClient] IDurableClient starter,
            ILogger log)
        {
            string requestBody;
            using (var sr = new System.IO.StreamReader(req.Body)) requestBody = await sr.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(requestBody))
            {
                return new BadRequestObjectResult(new { error = "Request body is empty" });
            }

            // トランスクリプトを解析
            string? transcript = PiiMaskingService.ParseTranscript(requestBody);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                return new BadRequestObjectResult(new { error = "No transcript provided" });
            }

            // 入力バリデーション
            var validation = PiiMaskingService.ValidateTranscript(transcript);
            if (!validation.IsValid)
            {
                return new BadRequestObjectResult(new { error = validation.ErrorMessage });
            }

            // 警告がある場合はログに記録
            if (!string.IsNullOrEmpty(validation.WarningMessage))
            {
                log.LogWarning(validation.WarningMessage);
            }

            var instanceId = await starter.StartNewAsync("PiiMasking_Orchestrator", transcript);
            log.LogInformation("Started PiiMasking durable orchestrator with ID = '{InstanceId}', transcript length = {Length}.",
                instanceId, transcript.Length);

            return starter.CreateCheckStatusResponse(req, instanceId);
        }

        /// <summary>
        /// オーケストレーター: PIIマスキング処理を調整
        /// </summary>
        [FunctionName("PiiMasking_Orchestrator")]
        public static async Task<AnalyzeActivityOutput> Orchestrator([OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            var transcript = context.GetInput<string>() ?? string.Empty;
            var instanceId = context.InstanceId;

            // 型安全な入力オブジェクトを作成
            var input = new AnalyzeActivityInput
            {
                InstanceId = instanceId,
                Transcript = transcript
            };

            // アクティビティを呼び出してPII検出とマスキングを実行
            var result = await context.CallActivityAsync<AnalyzeActivityOutput>("PiiMasking_RunAnalyze", input);
            return result;
        }

        /// <summary>
        /// アクティビティ: Azure AI Language APIを呼び出してPII検出とマスキングを実行
        /// </summary>
        [FunctionName("PiiMasking_RunAnalyze")]
        public static async Task<AnalyzeActivityOutput> RunAnalyze(
            [ActivityTrigger] AnalyzeActivityInput input,
            ILogger log)
        {
            var instanceId = input.InstanceId;
            var transcript = input.Transcript;
            var output = new AnalyzeActivityOutput { OperationId = instanceId };

            try
            {
                // TextAnalyticsクライアントを作成
                var client = PiiMaskingService.CreateTextAnalyticsClient();
                if (client == null)
                {
                    log.LogError("Text Analytics credentials not configured for durable activity");
                    output.Error = "Text Analytics credentials not configured";
                    return output;
                }

                // 共通サービスを使用してPII検出（リトライ機能付き）
                var detectedEntities = await PiiMaskingService.DetectPiiEntitiesAsync(
                    client, transcript, log, CancellationToken.None);

                // 共通サービスを使用してマスキング処理
                var result = PiiMaskingService.ProcessMasking(transcript, detectedEntities);

                log.LogInformation("PiiMasking completed for instance {InstanceId}",
                    instanceId);

                // 結果を設定
                output.MaskedTranscript = result.MaskedTranscript;
                output.Entities = result.Entities.Select(e => new PiiEntityDto
                {
                    Category = e.Category,
                    SubCategory = e.SubCategory,
                    Text = e.Text,
                    Offset = e.Offset,
                    Length = e.Length
                }).ToList();
                output.MaskCategoriesEnv = result.MaskCategoriesEnv;
                output.MaskCategoriesUsed = result.MaskCategoriesUsed;
                output.MaskOffsets = result.MaskOffsets.Select(o => new MaskOffsetDto
                {
                    Offset = o.offset,
                    Length = o.length
                }).ToList();
                output.ProcessingTimeMs = start.ElapsedMilliseconds;

                return output;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Durable analyze actions failed for instance {InstanceId}", instanceId);
                output.Error = ex.Message;;
                return output;
            }
        }

        /// <summary>
        /// ステータス確認用HTTPトリガー: オーケストレーションの状態と結果を取得
        /// GET /api/status/{instanceId}
        /// </summary>
        [FunctionName("PiiMasking_Status")]
        public static async Task<IActionResult> GetStatus(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "status/{instanceId}")] HttpRequest req,
            string instanceId,
            [DurableClient] IDurableClient client,
            ILogger log)
        {
            if (string.IsNullOrEmpty(instanceId))
            {
                return new BadRequestObjectResult(new { error = "instanceId is required" });
            }

            var status = await client.GetStatusAsync(instanceId);
            if (status == null)
            {
                return new NotFoundObjectResult(new { error = "Instance not found" });
            }

            var runtime = status.RuntimeStatus;

            // 完了した場合は結果を返す
            if (runtime == OrchestrationRuntimeStatus.Completed)
            {
                return new OkObjectResult(status.Output);
            }

            // 失敗した場合はエラー情報を返す
            if (runtime == OrchestrationRuntimeStatus.Failed)
            {
                return new ObjectResult(new
                {
                    instanceId = status.InstanceId,
                    runtimeStatus = runtime.ToString(),
                    error = status.Output?.ToString() ?? "Unknown error",
                    createdTime = status.CreatedTime,
                    lastUpdatedTime = status.LastUpdatedTime
                })
                { StatusCode = 500 };
            }

            // キャンセルされた場合
            if (runtime == OrchestrationRuntimeStatus.Canceled || runtime == OrchestrationRuntimeStatus.Terminated)
            {
                return new ObjectResult(new
                {
                    instanceId = status.InstanceId,
                    runtimeStatus = runtime.ToString(),
                    createdTime = status.CreatedTime,
                    lastUpdatedTime = status.LastUpdatedTime
                })
                { StatusCode = 410 }; // Gone
            }

            // 処理中の場合は202を返す
            var response = new
            {
                instanceId = status.InstanceId,
                runtimeStatus = runtime.ToString(),
                createdTime = status.CreatedTime,
                lastUpdatedTime = status.LastUpdatedTime
            };

            return new ObjectResult(response) { StatusCode = 202 };
        }
    }
}
