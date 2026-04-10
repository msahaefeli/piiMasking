using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.TextAnalytics;
using Newtonsoft.Json.Linq;

namespace test.CommonFunctions.AzureFunction.PiiMasking.Internal
{
    #region データモデル

    /// <summary>
    /// PII検出結果を格納するレコード
    /// </summary>
    public record PiiEntity(string Category, string? SubCategory, string Text, int Offset, int Length);

    /// <summary>
    /// マスキング処理結果を格納するクラス
    /// </summary>
    public class MaskingResult
    {
        public string MaskedTranscript { get; set; } = string.Empty;
        public List<PiiEntity> Entities { get; set; } = new();
        public string[] MaskCategoriesUsed { get; set; } = Array.Empty<string>();
        public List<(int offset, int length)> MaskOffsets { get; set; } = new();
        public string? MaskCategoriesEnv { get; set; }
    }

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
    /// PIIマスキングの共通サービスクラス
    /// Durable Functionsで使用される全ての共通ロジックを集約
    /// </summary>
    public static class PiiMaskingService
    {
        #region 定数

        /// <summary>
        /// Azure AI Language の最大入力文字数（125,000文字）
        /// https://learn.microsoft.com/ja-jp/azure/ai-services/language-service/personally-identifiable-information/overview
        /// </summary>
        public const int MaxTranscriptLength = 125000;

        /// <summary>
        /// 推奨される最大入力文字数（パフォーマンス考慮）
        /// </summary>
        public const int RecommendedMaxTranscriptLength = 50000;

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

            if (transcript.Length > MaxTranscriptLength)
            {
                return ValidationResult.Error($"Transcript exceeds maximum length of {MaxTranscriptLength} characters (current: {transcript.Length})");
            }

            if (transcript.Length > RecommendedMaxTranscriptLength)
            {
                return ValidationResult.SuccessWithWarning($"Transcript length ({transcript.Length}) exceeds recommended maximum of {RecommendedMaxTranscriptLength} characters. Processing may be slow.");
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

        #region Azure AI Language クライアント

        /// <summary>
        /// 環境変数からTextAnalyticsClientを作成
        /// </summary>
        public static TextAnalyticsClient? CreateTextAnalyticsClient()
        {
            var endpoint = Environment.GetEnvironmentVariable("TEXT_ANALYTICS_ENDPOINT");
            var apiKey = Environment.GetEnvironmentVariable("TEXT_ANALYTICS_KEY");

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
            {
                return null;
            }

            return new TextAnalyticsClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        }

        #endregion

        #region マスキングカテゴリ

        /// <summary>
        /// デフォルトのマスキング対象カテゴリ
        /// 日本の法人向け保険営業のCRM用途では保持すべきでない高機密な識別子のみをマスキング
        /// （Person/Phone/Email/Addressは営業活動に必要なため保持）
        /// </summary>
        public static HashSet<string> GetDefaultMaskCategories()
        {
            return new HashSet<string>(new[]
            {
                // 金融情報
                "CreditCardNumber",           // クレジットカード番号
                "BankAccountNumber",          // 銀行口座番号
                "JPBankAccountNumber",        // 日本の銀行口座番号
                "SWIFTCode",                  // SWIFTコード

                // 日本固有の個人識別番号
                "JPMyNumberPersonal",         // マイナンバー（個人番号）
                "JPMyNumberCorporate",        // 法人番号
                "JPResidenceCardNumber",      // 在留カード番号
                "JPDriversLicenseNumber",     // 日本の運転免許証番号
                "JPPassportNumber",           // 日本のパスポート番号
                "JPSocialInsuranceNumber",    // 社会保険番号
                "JPHealthInsuranceNumber",    // 健康保険証番号

                // 一般的な識別番号（国際）
                "PassportNumber",             // パスポート番号（汎用）
                "DriverLicenseNumber",        // 運転免許証番号（汎用）

                // 技術的な識別子
                "IPAddress",                  // IPアドレス
                "URL",                        // URL
                "Url"                         // URL (小文字バリエーション)
            }, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 環境変数またはデフォルト値からマスキング対象カテゴリを取得
        /// </summary>
        public static HashSet<string> GetMaskCategories()
        {
            var maskCatsEnv = Environment.GetEnvironmentVariable("PII_MASK_CATEGORIES");
            if (!string.IsNullOrWhiteSpace(maskCatsEnv))
            {
                return new HashSet<string>(
                    maskCatsEnv.Split(',').Select(s => s.Trim()),
                    StringComparer.OrdinalIgnoreCase);
            }
            return GetDefaultMaskCategories();
        }

        #endregion

        #region PII検出

        /// <summary>
        /// Azure AI Language APIを呼び出してPIIエンティティを検出（リトライ機能付き）
        /// </summary>
        public static async Task<List<PiiEntity>> DetectPiiEntitiesAsync(
            TextAnalyticsClient client,
            string transcript,
            ILogger? log,
            CancellationToken cancellationToken = default)
        {
            var collected = new List<PiiEntity>();

            // リトライ機能を使用してAPI呼び出し
            await FunctionBaseRetry.FuncWithRetryAsync(async (ct) =>
            {
                var actions = new TextAnalyticsActions()
                {
                    RecognizePiiEntitiesActions = new List<RecognizePiiEntitiesAction>
                    {
                        new RecognizePiiEntitiesAction()
                    }
                };

                var operation = await client.StartAnalyzeActionsAsync(new[] { transcript }, actions, cancellationToken: ct);
                await operation.WaitForCompletionAsync(ct);

                await foreach (var actionResult in operation.Value)
                {
                    foreach (var piiActionResult in actionResult.RecognizePiiEntitiesResults)
                    {
                        foreach (var docResult in piiActionResult.DocumentsResults)
                        {
                            if (docResult.HasError)
                            {
                                log?.LogWarning("Document error: {0}", docResult.Error.Message);
                                continue;
                            }

                            foreach (var e in docResult.Entities)
                            {
                                collected.Add(new PiiEntity(
                                    e.Category.ToString(),
                                    e.SubCategory,
                                    e.Text,
                                    (int)e.Offset,
                                    (int)e.Length));
                            }
                        }
                    }
                }

                return collected;
            }, log, cancellationToken);

            return collected;
        }

        /// <summary>
        /// 検出されたエンティティを重複排除してソート
        /// </summary>
        public static List<PiiEntity> DeduplicateEntities(List<PiiEntity> entities)
        {
            return entities
                .GroupBy(x => (x.Offset, x.Length, x.Text))
                .Select(g => g.First())
                .OrderBy(e => e.Offset)
                .ToList();
        }

        #endregion

        #region マスキング処理

        /// <summary>
        /// 指定されたオフセットに基づいてテキストをマスキング
        /// </summary>
        public static string MaskTranscriptFromOffsets(string transcript, IEnumerable<(int offset, int length)>? offsets)
        {
            if (string.IsNullOrEmpty(transcript)) return transcript;
            if (offsets == null) return transcript;

            var chars = transcript.ToCharArray();

            // オフセットを保持するために末尾からマスキング
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

        /// <summary>
        /// PIIマスキングの完全な処理を実行
        /// </summary>
        public static MaskingResult ProcessMasking(
            string transcript,
            List<PiiEntity> detectedEntities)
        {
            var allowedMaskCategories = GetMaskCategories();
            var maskCatsEnv = Environment.GetEnvironmentVariable("PII_MASK_CATEGORIES");

            // エンティティの重複排除
            var entitiesDistinct = DeduplicateEntities(detectedEntities);

            // マスキング対象のオフセットを収集
            var maskOffsetsList = new List<(int offset, int length)>();

            // 1) 許可リストにあるカテゴリのエンティティをマスキング
            foreach (var e in entitiesDistinct)
            {
                if (allowedMaskCategories.Contains(e.Category))
                {
                    maskOffsetsList.Add((e.Offset, e.Length));
                }
            }


            // マスキング実行
            var maskedTranscript = MaskTranscriptFromOffsets(transcript, maskOffsetsList);

            return new MaskingResult
            {
                MaskedTranscript = maskedTranscript,
                Entities = entitiesDistinct,
                MaskCategoriesUsed = allowedMaskCategories.ToArray(),
                MaskOffsets = maskOffsetsList,
                MaskCategoriesEnv = maskCatsEnv
            };
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
            if (ex is RequestFailedException rfe)
            {
                // Retry on server errors and throttling
                if (rfe.Status >= 500 || rfe.Status == 429) return true;
                return false;
            }
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
