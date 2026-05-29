using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace test.CommonFunctions.AzureFunction.PiiMasking.Internal
{
    #region データモデル

    /// <summary>
    /// アクティビティへの入力パラメータ（型安全）
    /// </summary>
    public class AnalyzeActivityInput
    {
        public string InstanceId { get; set; } = string.Empty;
        public string Transcript { get; set; } = string.Empty;
    }

    /// <summary>
    /// アクティビティからの出力結果（型安全）
    /// </summary>
    public class AnalyzeActivityOutput
    {
        public string OperationId { get; set; } = string.Empty;
        public string MaskedTranscript { get; set; } = string.Empty;

        /// <summary>
        /// Base64エンコードされたMaskedTranscript（文字化け回避用）
        /// </summary>
        public string? MaskedTranscriptBase64 { get; set; }

        /// <summary>
        /// Azure Storageにアップロードされたマスキング済みVTTのURL
        /// </summary>
        public string? BlobUrl { get; set; }

        public List<PiiEntityDto> Entities { get; set; } = new();
        public string? MaskCategoriesEnv { get; set; }
        public string[] MaskCategoriesUsed { get; set; } = Array.Empty<string>();
        public List<MaskOffsetDto> MaskOffsets { get; set; } = new();
        public long ProcessingTimeMs { get; set; }
        public string? Error { get; set; }
        public bool IsSuccess => string.IsNullOrEmpty(Error);
    }

    /// <summary>
    /// PIIエンティティのDTO（シリアライズ用）
    /// </summary>
    public class PiiEntityDto
    {
        public string Category { get; set; } = string.Empty;
        public string? SubCategory { get; set; }
        public string Text { get; set; } = string.Empty;
        public int Offset { get; set; }
        public int Length { get; set; }
    }

    /// <summary>
    /// マスクオフセットのDTO（シリアライズ用）
    /// </summary>
    public class MaskOffsetDto
    {
        public int Offset { get; set; }
        public int Length { get; set; }
    }

    #endregion

    /// <summary>
    /// Azure OpenAIを使用したPIIマスキングの共通サービスクラス
    /// テキストとWebVTTファイルのマスキング処理を提供します
    /// </summary>
    public static class PiiMaskingService
    {
        #region 定数

        /// <summary>
        /// Azure OpenAIの推奨最大入力文字数
        /// これを超える場合はチャンク分割処理を実行します
        /// </summary>
        public const int MaxTranscriptLength = 10000;

        /// <summary>
        /// VTTファイル処理時の1チャンクあたりのキュー数
        /// </summary>
        public const int CuesPerChunk = 50;

        #endregion

        #region 入力バリデーション

        /// <summary>
        /// トランスクリプトのバリデーション結果
        /// </summary>
        public class ValidationResult
        {
            public bool IsValid { get; set; }
            public string? ErrorMessage { get; set; }
            public string? WarningMessage { get; set; }

            public static ValidationResult Success() => new() { IsValid = true };
            public static ValidationResult Error(string message) => new() { IsValid = false, ErrorMessage = message };
            public static ValidationResult SuccessWithWarning(string warning) => new() { IsValid = true, WarningMessage = warning };
        }

        /// <summary>
        /// トランスクリプトの入力検証
        /// </summary>
        public static ValidationResult ValidateTranscript(string? transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript))
            {
                return ValidationResult.Error("Transcript is empty or whitespace only");
            }

            // Azure OpenAIの入力制限をチェック（大きすぎる場合は警告）
            const int warningThreshold = 50000;
            if (transcript.Length > warningThreshold)
            {
                return ValidationResult.SuccessWithWarning($"Transcript length ({transcript.Length}) is large. Processing may be slow.");
            }

            return ValidationResult.Success();
        }

        #endregion

        #region リクエスト解析

        /// <summary>
        /// リクエストボディからトランスクリプトを解析
        /// JSON形式の場合は "transcript" または "text" フィールドを抽出
        /// </summary>
        public static string? ParseTranscript(string? requestBody)
        {
            if (string.IsNullOrWhiteSpace(requestBody)) return null;

            try
            {
                var j = JObject.Parse(requestBody);
                if (j["transcript"] != null) return j["transcript"]!.ToString();
                if (j["text"] != null) return j["text"]!.ToString();
                return requestBody;
            }
            catch
            {
                return requestBody;
            }
        }

        #endregion

        // Azure Text Analytics client creation removed

        #region マスキングカテゴリ

        #endregion

        #region VTTマスキング

        /// <summary>
        /// VTTファイル全体をAzure OpenAIでマスキング
        /// VTT構文を保持したまま、PIIと病歴情報をマスキングします
        /// </summary>
        public static async Task<string?> MaskVttWithOpenAIAsync(string endpoint, string apiKey, string deployment, string vttContent, ILogger? log, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(vttContent)) return vttContent;
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment))
            {
                return vttContent;
            }

            var baseUrl = endpoint.TrimEnd('/');
            var url = $"{baseUrl}/openai/deployments/{deployment}/chat/completions?api-version=2023-10-01-preview";

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(5); // VTT処理用に5分に延長
            http.DefaultRequestHeaders.Add("api-key", apiKey);

            try
            {
                return await FunctionBaseRetry.FuncWithRetryAsync(async (ct) =>
                {
                    // デバッグ: 送信前のVTTをログ
                    log?.LogInformation("Sending VTT to OpenAI, first 100 chars: {Preview}", 
                        vttContent.Substring(0, Math.Min(100, vttContent.Length)));

                    var systemPrompt = @"あなたはWebVTT字幕ファイルの個人情報マスキング専門家です。

【保持する情報（絶対にマスキングしない）】
- 人名
- 年齢
- 住所
- 郵便番号
- 日付
- 電話番号
- メールアドレス
- URL

【必ずマスキングする情報】
- マイナンバー、社員番号、年金番号、法人番号、株主番号、保険証番号
- クレジットカード番号、銀行口座番号
- パスポート番号、運転免許証番号
- 病名（糖尿病、がん、高血圧、うつ病、適応障害など）
- 症状、検査数値（HbA1c、血糖値など）
- 医薬品名（メトホルミン、タモキシフェン、エスシタロプラムなど）
- 診断内容、入院歴、治療内容、後遺障害等級

【重要】
- WebVTTの構文（WEBVTT、NOTE、タイムスタンプ、番号）は絶対に変更しない
- テキスト部分のみマスキング
- マスキングは「*」で元の文字数分置き換え
- マスキング済みのWebVTT全体のみを返す（説明不要）";

                    var userPrompt = $"以下のWebVTTファイルをマスキングしてください：\n\n{vttContent}";

                    var messages = new List<object>
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = "例：\n\n14:21:15.000 --> 14:22:15.000\n<v 村上>中島、社員番号 E-11102、マイナンバー 765432109876。中島さんは 2型糖尿病 で、メトホルミン 500mg を服用中です。" },
                        new { role = "assistant", content = "14:21:15.000 --> 14:22:15.000\n<v 村上>中島、社員番号 *******、マイナンバー ************。中島さんは ****** で、******** 500mg を服用中です。" },
                        new { role = "user", content = userPrompt }
                    };

                    var payload = new
                    {
                        messages = messages,
                        max_completion_tokens = 16000,
                        temperature = 0.0
                    };

                    var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload, new JsonSerializerSettings
                    {
                        StringEscapeHandling = StringEscapeHandling.Default
                    });

                    using var sc = new StringContent(json, Encoding.UTF8, "application/json");
                    using var resp = await http.PostAsync(url, sc, ct);

                    if (!resp.IsSuccessStatusCode)
                    {
                        var errorBody = await resp.Content.ReadAsStringAsync(ct);
                        log?.LogError("OpenAI VTT API Error {StatusCode}: {ErrorBody}", resp.StatusCode, errorBody);
                    }

                    resp.EnsureSuccessStatusCode();

                    // UTF-8でレスポンスを読み取る
                    var responseBytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    var respText = Encoding.UTF8.GetString(responseBytes);

                    // デバッグ: レスポンスをログ
                    log?.LogInformation("Received response from OpenAI, length: {Length}, first 200 chars: {Preview}",
                        respText.Length,
                        respText.Substring(0, Math.Min(200, respText.Length)));

                    var jo = JObject.Parse(respText);

                    // デバッグ: レスポンス全体の構造をログ出力
                    log?.LogInformation("Response JSON structure: {Json}", jo.ToString(Formatting.None).Substring(0, Math.Min(500, jo.ToString(Formatting.None).Length)));

                    // Content Filterエラーをチェック
                    var contentFilterError = jo["choices"]?[0]?["content_filter_result"]?["error"];
                    if (contentFilterError != null)
                    {
                        var errorCode = contentFilterError["code"]?.ToString();
                        var errorMessage = contentFilterError["message"]?.ToString();
                        log?.LogWarning("Azure OpenAI Content Filter Error for this chunk: {Code} - {Message}. Returning original content.", errorCode, errorMessage);
                        // チャンク処理の場合は、このチャンクだけ元のまま返す
                        return vttContent;
                    }

                    // 通常のエラーチェック
                    if (jo["error"] != null)
                    {
                        var errorMsg = jo["error"]?["message"]?.ToString() ?? "Unknown error";
                        log?.LogError("Azure OpenAI API Error: {Error}", errorMsg);
                        return vttContent;
                    }

                    // コンテンツ抽出（複数のパスを試行）
                    var txt = jo["choices"]?[0]?["message"]?["content"]?.ToString();

                    if (string.IsNullOrWhiteSpace(txt))
                    {
                        // 代替パスを試行
                        txt = jo["choices"]?[0]?["text"]?.ToString();
                    }

                    if (string.IsNullOrWhiteSpace(txt))
                    {
                        log?.LogWarning("Could not extract content from OpenAI response. Available keys in choices[0]: {Keys}", 
                            string.Join(", ", (jo["choices"]?[0] as JObject)?.Properties().Select(p => p.Name) ?? Array.Empty<string>()));
                        return vttContent;
                    }

                    log?.LogInformation("Successfully extracted content, length: {Length}", txt.Length);

                    // デバッグ: 抽出したコンテンツをログ
                    log?.LogInformation("Extracted content from response, length: {Length}, first 100 chars: {Preview}",
                        txt?.Length ?? 0,
                        txt?.Substring(0, Math.Min(100, txt?.Length ?? 0)) ?? "(null)");

                    if (string.IsNullOrWhiteSpace(txt)) return vttContent;

                    // コードフェンスで囲まれている場合は除去
                    if (txt.StartsWith("```"))
                    {
                        var idx = txt.IndexOf('\n');
                        if (idx >= 0) txt = txt.Substring(idx + 1);
                        if (txt.EndsWith("```")) txt = txt.Substring(0, txt.Length - 3).TrimEnd();
                    }

                    return txt;

                }, log, cancellationToken);
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "OpenAI VTT masking failed");
                log?.LogError("VTT Exception type: {ExType}, Message: {Msg}", ex.GetType().Name, ex.Message);
                if (ex.InnerException != null)
                {
                    log?.LogError("VTT Inner exception: {InnerMsg}", ex.InnerException.Message);
                }
                return vttContent;
            }
        }


        #endregion

        #region VTTマスキング

        /// <summary>
        /// VTTファイルのマスキングエントリーポイント
        /// Azure OpenAIを使用してマスキングを実行します
        /// </summary>
        public static async Task<string> MaskVttAsync(string vttContent, ILogger? log, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(vttContent))
            {
                log?.LogWarning("VTT content is null or empty");
                return vttContent ?? string.Empty;
            }

            try
            {
                var oaEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
                var oaKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY");
                var deployment = Environment.GetEnvironmentVariable("OPENAI_DEPLOYMENT_NAME") ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");

                log?.LogInformation("Azure OpenAI Config - Endpoint: {HasEndpoint}, Key: {HasKey}, Deployment: {Deployment}",
                    !string.IsNullOrWhiteSpace(oaEndpoint),
                    !string.IsNullOrWhiteSpace(oaKey),
                    deployment ?? "(null)");

                if (!string.IsNullOrWhiteSpace(oaEndpoint) && !string.IsNullOrWhiteSpace(oaKey) && !string.IsNullOrWhiteSpace(deployment))
                {
                    // VTTファイルが大きい場合は分割処理
                    if (vttContent.Length > MaxTranscriptLength)
                    {
                        log?.LogInformation("VTT file is large ({Size} chars), splitting into chunks", vttContent.Length);
                        return await MaskVttInChunksAsync(oaEndpoint, oaKey, deployment, vttContent, log, cancellationToken);
                    }

                    log?.LogInformation("Calling MaskVttWithOpenAIAsync for {Size} chars", vttContent.Length);
                    var masked = await MaskVttWithOpenAIAsync(oaEndpoint, oaKey, deployment, vttContent, log, cancellationToken);

                    if (string.IsNullOrWhiteSpace(masked))
                    {
                        log?.LogWarning("MaskVttWithOpenAIAsync returned null or empty, returning original content");
                        return vttContent;
                    }

                    log?.LogInformation("Masking successful, result length: {Length}", masked.Length);

                    // デバッグ用：マスキング結果を保存
                    try
                    {
                        var outDir = Path.Combine(Directory.GetCurrentDirectory(), "TestResults");
                        Directory.CreateDirectory(outDir);
                        var outFile = Path.Combine(outDir, $"openai_masked_vtt_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.vtt");
                        File.WriteAllText(outFile, masked ?? string.Empty, Encoding.UTF8);
                        log?.LogInformation("Debug: Saved masked VTT to {Path}", outFile);
                    }
                    catch (Exception debugEx)
                    {
                        log?.LogWarning(debugEx, "Failed to save debug output");
                    }

                    return masked;
                }
                else
                {
                    log?.LogWarning("Azure OpenAI configuration is missing. Returning original VTT content.");
                    return vttContent;
                }
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "VTT masking failed");
                return vttContent;
            }
        }

        /// <summary>
        /// VTTファイルを分割してマスキング処理
        /// </summary>
        private static async Task<string> MaskVttInChunksAsync(string endpoint, string apiKey, string deployment, string vttContent, ILogger? log, CancellationToken cancellationToken)
        {
            try
            {
                // VTTをキューごとに分割
                var lines = vttContent.Split('\n');
                var header = new List<string>();
                var cues = new List<List<string>>();
                var currentCue = new List<string>();
                bool inHeader = true;

                foreach (var line in lines)
                {
                    if (inHeader)
                    {
                        header.Add(line);
                        if (line.Trim() == "" && header.Count > 2)
                        {
                            inHeader = false;
                        }
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        if (currentCue.Count > 0)
                        {
                            cues.Add(new List<string>(currentCue));
                            currentCue.Clear();
                        }
                    }
                    else
                    {
                        currentCue.Add(line);
                    }
                }

                if (currentCue.Count > 0)
                {
                    cues.Add(currentCue);
                }

                log?.LogInformation("Split VTT into {Count} cues", cues.Count);

                // キューを複数のチャンクに分割
                var chunks = new List<string>();

                for (int i = 0; i < cues.Count; i += CuesPerChunk)
                {
                    var chunkCues = cues.Skip(i).Take(CuesPerChunk).ToList();
                    var chunkText = string.Join("\n\n", chunkCues.Select(c => string.Join("\n", c)));
                    chunks.Add(chunkText);
                }

                log?.LogInformation("Created {Count} chunks for processing", chunks.Count);

                // 各チャンクをマスキング
                var maskedChunks = new List<string>();
                for (int i = 0; i < chunks.Count; i++)
                {
                    log?.LogInformation("Processing chunk {Current}/{Total}", i + 1, chunks.Count);
                    try
                    {
                        var masked = await MaskVttWithOpenAIAsync(endpoint, apiKey, deployment, chunks[i], log, cancellationToken);

                        // 空またはnullの場合は元のチャンクを使用（Content Filterエラーなどの場合）
                        if (string.IsNullOrWhiteSpace(masked))
                        {
                            log?.LogWarning("Chunk {Current} returned empty, using original chunk", i + 1);
                            maskedChunks.Add(chunks[i]);
                        }
                        else
                        {
                            maskedChunks.Add(masked);
                        }
                    }
                    catch (Exception chunkEx)
                    {
                        log?.LogError(chunkEx, "Error processing chunk {Current}, using original chunk", i + 1);
                        maskedChunks.Add(chunks[i]); // エラーの場合は元のチャンクを使用
                    }
                }

                // ヘッダーとマスキング済みチャンクを結合
                var result = string.Join("\n", header) + "\n\n" + string.Join("\n\n", maskedChunks);
                return result;
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "VTT chunk masking failed");
                return vttContent;
            }
        }

        #endregion

        #region Azure Storage アップロード

        /// <summary>
        /// マスキング済みVTTをAzure Blob Storageにアップロード
        /// </summary>
        /// <param name="connectionString">Azure Storage接続文字列</param>
        /// <param name="containerName">コンテナ名</param>
        /// <param name="blobName">Blob名（ファイル名）</param>
        /// <param name="vttContent">VTTファイル内容</param>
        /// <param name="log">ロガー</param>
        /// <returns>アップロードされたBlobのURL</returns>
        public static async Task<string> UploadToAzureStorageAsync(
            string connectionString,
            string containerName,
            string blobName,
            string vttContent,
            ILogger? log,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var blobServiceClient = new BlobServiceClient(connectionString);
                var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

                // コンテナが存在しない場合は作成
                await containerClient.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

                var blobClient = containerClient.GetBlobClient(blobName);

                // UTF-8でアップロード
                var bytes = Encoding.UTF8.GetBytes(vttContent);
                using var stream = new MemoryStream(bytes);

                var uploadOptions = new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders
                    {
                        ContentType = "text/vtt; charset=utf-8"
                    }
                };

                await blobClient.UploadAsync(stream, uploadOptions, cancellationToken);

                var blobUrl = blobClient.Uri.ToString();
                log?.LogInformation("Uploaded masked VTT to Azure Storage: {BlobUrl}", blobUrl);

                return blobUrl;
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "Failed to upload to Azure Storage");
                throw;
            }
        }

        #endregion
    }

    /// <summary>
    /// リトライ機能を提供するユーティリティクラス
    /// </summary>
    internal static class FunctionBaseRetry
    {
        // デフォルトのリトライ設定
        public const int DefaultMaxRetryCount = 3;
        public const int DefaultRetryIntervalMs = 1000; // ミリ秒
        public const int DefaultMaxDelayMs = 30000;

        private static readonly Random _jitterer = new Random();

        private static int GetEnvInt(string name, int defaultValue)
        {
            try
            {
                var v = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(v) && int.TryParse(v, out var r)) return r;
            }
            catch { }
            return defaultValue;
        }

        private static bool IsTransient(Exception ex)
        {
            if (ex is HttpRequestException) return true;
            if (ex is TimeoutException) return true;
            if (ex is TaskCanceledException) return true;
            return false;
        }

        // 汎用の戻り値あり非同期リトライ
        public static async Task<T> FuncWithRetryAsync<T>(Func<CancellationToken, Task<T>> func, ILogger? log, CancellationToken cancellationToken, int? maxRetryCount = null, int? baseDelayMs = null)
        {
            int maxAttempts = maxRetryCount ?? GetEnvInt("PII_RETRY_MAX_COUNT", DefaultMaxRetryCount);
            int baseDelay = baseDelayMs ?? GetEnvInt("PII_RETRY_BASE_DELAY_MS", DefaultRetryIntervalMs);
            int maxDelay = GetEnvInt("PII_RETRY_MAX_DELAY_MS", DefaultMaxDelayMs);

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await func(cancellationToken);
                }
                catch (Exception ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        log?.LogInformation("Operation cancelled.");
                        throw;
                    }

                    bool transient = IsTransient(ex);
                    if (!transient || attempt >= maxAttempts)
                    {
                        log?.LogError(ex, "Operation failed after {Attempt} attempts.", attempt + 1);
                        throw;
                    }

                    // Exponential backoff with jitter
                    int exponential = baseDelay * (1 << attempt);
                    int delay = Math.Min(exponential, maxDelay);
                    int jitter = _jitterer.Next(0, baseDelay);
                    int finalDelay = delay + jitter;

                    log?.LogWarning(ex, "Transient error detected. Retrying attempt {Attempt}/{Max} after {Delay}ms", attempt + 1, maxAttempts, finalDelay);

                    try
                    {
                        await Task.Delay(finalDelay, cancellationToken);
                    }
                    catch (TaskCanceledException)
                    {
                        log?.LogInformation("Delay cancelled.");
                        throw;
                    }
                }
            }
        }

        // 非同期アクション用 (CancellationToken を受け取る Func を想定)
        public static Task ActionWithRetryAsync(Func<CancellationToken, Task> func, ILogger? log, CancellationToken cancellationToken, int? maxRetryCount = null, int? baseDelayMs = null)
        {
            return FuncWithRetryAsync<object?>(async ct => { await func(ct); return null; }, log, cancellationToken, maxRetryCount, baseDelayMs);
        }

        // 互換性のためのオーバーロード (既存のシグネチャを維持)
        public static Task<T> FuncWithRetryAsync<T>(Func<Task<T>> func, ILogger? log, CancellationToken cancellationToken, int? maxRetryCount = null, int? baseDelayMs = null)
        {
            return FuncWithRetryAsync<T>(ct => func(), log, cancellationToken, maxRetryCount, baseDelayMs);
        }

        public static Task ActionWithRetryAsync(Func<Task> func, ILogger? log, CancellationToken cancellationToken, int? maxRetryCount = null, int? baseDelayMs = null)
        {
            return ActionWithRetryAsync(ct => func(), log, cancellationToken, maxRetryCount, baseDelayMs);
        }
    }
}
