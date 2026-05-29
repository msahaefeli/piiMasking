using System;
using System;
using System.Collections.Generic;
using System.Text;
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
    /// Durable Functionsを使用した非同期VTTマスキング処理
    /// Azure OpenAI APIを使用してVTTファイルからPIIと医療情報をマスキングします
    /// </summary>
    public static class PiiMaskingDurable
    {
        /// <summary>
        /// VTTマスキング処理を開始するHTTPトリガー
        /// POST /api/maskpii
        /// </summary>
        [FunctionName("PiiMasking_HttpStart")]
        public static async Task<IActionResult> HttpStart(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "maskpii")] HttpRequest req,
            [DurableClient] IDurableClient starter,
            ILogger log)
        {
            // リクエストボディをバッファリング（複数回読み取り可能にする）
            req.EnableBuffering();

            // リクエストボディを読み取り
            string requestBody;
            using (var reader = new System.IO.StreamReader(req.Body, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                requestBody = await reader.ReadToEndAsync();
            }

            // ストリームをリセット（念のため）
            req.Body.Position = 0;

            if (string.IsNullOrWhiteSpace(requestBody))
            {
                return new BadRequestObjectResult(new { error = "Request body is empty" });
            }

            // VTTコンテンツを解析
            string? vttContent = PiiMaskingService.ParseTranscript(requestBody);
            if (string.IsNullOrWhiteSpace(vttContent))
            {
                return new BadRequestObjectResult(new { error = "No VTT content provided" });
            }

            // 入力バリデーション
            var validation = PiiMaskingService.ValidateTranscript(vttContent);
            if (!validation.IsValid)
            {
                return new BadRequestObjectResult(new { error = validation.ErrorMessage });
            }

            // 警告がある場合はログに記録
            if (!string.IsNullOrEmpty(validation.WarningMessage))
            {
                log.LogWarning(validation.WarningMessage);
            }

            // オーケストレーションを開始（インスタンスIDは自動生成、inputとしてvttContentを渡す）
            var instanceId = await starter.StartNewAsync("PiiMasking_Orchestrator", input: vttContent);
            log.LogInformation("Started VTT masking orchestrator with ID = '{InstanceId}', VTT length = {Length}.",
                instanceId, vttContent.Length);

            return starter.CreateCheckStatusResponse(req, instanceId);
        }

        /// <summary>
        /// オーケストレーター: VTTマスキング処理を調整
        /// </summary>
        [FunctionName("PiiMasking_Orchestrator")]
        public static async Task<AnalyzeActivityOutput> Orchestrator([OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            var vttContent = context.GetInput<string>() ?? string.Empty;
            var instanceId = context.InstanceId;

            // 型安全な入力オブジェクトを作成
            var input = new AnalyzeActivityInput
            {
                InstanceId = instanceId,
                Transcript = vttContent
            };

            // アクティビティを呼び出してVTTマスキングを実行
            var result = await context.CallActivityAsync<AnalyzeActivityOutput>("PiiMasking_RunAnalyze", input);

            // オーケストレーター側でログ（注: リプレイ時は複数回実行される可能性あり）
            if (!context.IsReplaying)
            {
                // 注: オーケストレーター内では通常のログは使えないため、カスタムステータスに設定
                context.SetCustomStatus(new
                {
                    MaskedLength = result?.MaskedTranscript?.Length ?? 0,
                    ProcessingTime = result?.ProcessingTimeMs ?? 0,
                    HasError = !string.IsNullOrEmpty(result?.Error)
                });
            }

            return result;
        }

        /// <summary>
        /// アクティビティ: Azure OpenAI APIを呼び出してVTTファイルをマスキング
        /// </summary>
        [FunctionName("PiiMasking_RunAnalyze")]
        public static async Task<AnalyzeActivityOutput> RunAnalyze(
            [ActivityTrigger] AnalyzeActivityInput input,
            ILogger log)
        {
            var instanceId = input.InstanceId;
            var vttContent = input.Transcript;
            var output = new AnalyzeActivityOutput { OperationId = instanceId };

            try
            {
                var start = System.Diagnostics.Stopwatch.StartNew();

                log.LogInformation("Starting VTT masking for instance {InstanceId}, VTT length: {Length}", 
                    instanceId, vttContent?.Length ?? 0);

                // Azure OpenAIを使用してVTTマスキング実行
                var maskedVtt = await PiiMaskingService.MaskVttAsync(vttContent, log);

                log.LogInformation("VTT masking completed for instance {InstanceId}. Result length: {ResultLength}", 
                    instanceId, maskedVtt?.Length ?? 0);

                // デバッグ: 結果の先頭を確認
                if (!string.IsNullOrEmpty(maskedVtt))
                {
                    var preview = maskedVtt.Substring(0, Math.Min(100, maskedVtt.Length));
                    log.LogInformation("Masked VTT preview: {Preview}", preview);
                }
                else
                {
                    log.LogWarning("Masked VTT is null or empty!");
                }

                // 結果を設定
                output.MaskedTranscript = maskedVtt ?? string.Empty;

                // Base64エンコード（文字化け回避）
                if (!string.IsNullOrEmpty(maskedVtt))
                {
                    var bytes = Encoding.UTF8.GetBytes(maskedVtt);
                    output.MaskedTranscriptBase64 = Convert.ToBase64String(bytes);
                    log.LogInformation("Base64 encoded length: {Length}", output.MaskedTranscriptBase64.Length);
                }

                // Azure Storageへアップロード（オプション）
                var storageConnectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");
                var storageContainer = Environment.GetEnvironmentVariable("STORAGE_CONTAINER_NAME") ?? "masked-vtt";

                if (!string.IsNullOrEmpty(storageConnectionString) && !string.IsNullOrEmpty(maskedVtt))
                {
                    try
                    {
                        var blobName = $"masked_{instanceId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.vtt";
                        var blobUrl = await PiiMaskingService.UploadToAzureStorageAsync(
                            storageConnectionString,
                            storageContainer,
                            blobName,
                            maskedVtt,
                            log,
                            default);

                        output.BlobUrl = blobUrl;
                        log.LogInformation("Uploaded to Azure Storage: {BlobUrl}", blobUrl);
                    }
                    catch (Exception uploadEx)
                    {
                        log.LogWarning(uploadEx, "Failed to upload to Azure Storage, but continuing");
                    }
                }

                output.Entities = new List<PiiEntityDto>();
                output.MaskCategoriesEnv = null;
                output.MaskCategoriesUsed = Array.Empty<string>();
                output.MaskOffsets = new List<MaskOffsetDto>();
                output.ProcessingTimeMs = start.ElapsedMilliseconds;

                // デバッグ: 設定後のoutputを確認
                log.LogInformation("Output.MaskedTranscript length after assignment: {Length}", output.MaskedTranscript?.Length ?? 0);

                return output;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "VTT masking failed for instance {InstanceId}", instanceId);
                output.Error = ex.Message;
                output.MaskedTranscript = string.Empty; // 明示的に空文字列を設定
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
