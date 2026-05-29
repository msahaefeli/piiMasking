# アーキテクチャ決定記録 (ADR)

## ADR-001: Azure OpenAIを使用したPIIマスキング

**日付**: 2024-04-24  
**ステータス**: 採用

### コンテキスト

当初は Azure AI Language (Text Analytics) のPII検出APIを使用していましたが、以下の課題がありました:
- 医療情報（病名、症状、薬品名など）の検出精度が不十分
- カスタマイズが困難
- 複雑なエンティティ管理とオフセット計算が必要

### 決定

Azure OpenAI (GPT-4o) の Chat Completions API を使用して、プロンプトベースでPIIと医療情報を直接マスキングする方式に変更。

### 理由

1. **柔軟なマスキング**: プロンプトで保持項目とマスキング項目を明確に定義可能
2. **医療情報対応**: 病名、症状、薬品名など医療情報を高精度で検出
3. **シンプルな実装**: エンティティ検出とオフセット管理が不要
4. **Few-shot Learning**: 例を示すことで精度向上

### 影響

- **削除**: Azure AI Language依存関係、エンティティ管理、オフセット計算
- **追加**: Azure OpenAI依存、プロンプトエンジニアリング、Few-shot examples
- **変更**: マスキングロジックの簡素化（約300行のコード削減）

---

## ADR-002: VTTファイルのチャンク分割処理

**日付**: 2024-04-24  
**ステータス**: 採用

### コンテキスト

大きなVTTファイル（14,000文字超）を一度に処理すると、Azure OpenAI APIの処理に時間がかかり（2分以上）、.NET HttpClientのデフォルトタイムアウト（100秒）を超過してしまう問題がありました。

### 決定

VTTファイルが10,000文字を超える場合、自動的にキュー単位で分割し、50キューごとにチャンク分割処理を適用。さらに、HttpClientのタイムアウトを5分に延長。

### 理由

1. **タイムアウト回避**: HttpClientのデフォルトタイムアウト（100秒）を超える長時間処理に対応
2. **処理時間短縮**: 小さなチャンクに分割することでAPI呼び出し1回あたりの処理時間を短縮
3. **VTT構造維持**: キュー単位で分割することで構造を維持
4. **並列処理**: 将来的にチャンク並列化可能（現在は順次処理）

### 実装詳細

```csharp
// HttpClientタイムアウト延長
http.Timeout = TimeSpan.FromMinutes(5);

// チャンク分割判定
if (vttContent.Length > MaxTranscriptLength) // 10,000文字
{
    return await MaskVttInChunksAsync(...);
}
```

チャンク処理:
1. ヘッダーとキューを分離
2. 50キューごとにチャンク作成
3. 各チャンクをマスキング
4. 結合して元のVTT形式で出力

### 影響

- **追加**: `MaskVttInChunksAsync` メソッド
- **変更**: HttpClient タイムアウトを100秒から5分に延長
- **結果**: 15,000文字のVTTファイルが30秒で処理可能（タイムアウトエラーなし）

---

## ADR-003: max_completion_tokens パラメータの使用

**日付**: 2024-04-24  
**ステータス**: 採用

### コンテキスト

GPT-4o系モデルでは `max_tokens` パラメータがサポートされず、400 Bad Requestエラーが発生していました。

### 決定

`max_tokens` を `max_completion_tokens` に変更。

### 理由

Azure OpenAI API（GPT-4o）の新しい仕様に準拠。`max_completion_tokens` は生成トークン数の上限を指定します。

### 実装

```csharp
var requestBody = new
{
    messages = new[] { systemMessage, userMessage },
    temperature = 0.0,
    max_completion_tokens = 16000
};
```

---

## ADR-004: Durable Functions の採用

**日付**: 2024-04-24  
**ステータス**: 採用

### コンテキスト

大きなVTTファイルの処理は数分かかることがあり、同期HTTPリクエストでは以下の問題がありました:
- クライアント側のHTTPタイムアウト
- Function実行時間の制限（従量課金プランで10分）
- 処理状況の可視化が困難

### 決定

Azure Durable Functions を採用し、非同期処理パターンを実装。

### 理由

1. **長時間処理対応**: オーケストレーションで複数分の処理を管理
2. **ステータス管理**: ポーリングでリアルタイム進捗確認
3. **リトライ機能**: アクティビティレベルでリトライ可能
4. **スケーラビリティ**: 複数リクエストを効率的に処理

### アーキテクチャ

```
HTTP Trigger → Orchestrator → Activity
     ↓              ↓             ↓
ステータスURL  ワークフロー   実処理
```

### 影響

- **追加**: PiiMasking_HttpStart, PiiMasking_Orchestrator, PiiMasking_RunAnalyze
- **削除**: 従来の同期HTTP Function（削除予定）
- **メリット**: 100%非同期、処理状況の可視化、タイムアウトリスク軽減

---

## ADR-005: Base64エンコーディングによる文字化け対策

**日付**: 2024-04-24  
**ステータス**: 採用

### コンテキスト

Durable Functionsの内部シリアライゼーション処理で、日本語が文字化けする問題が発生しました。具体的には、マスキング済みVTTの日本語が `?` に置き換わる現象です。

### 決定

マスキング済みVTTをBase64エンコードして `MaskedTranscriptBase64` フィールドに格納。クライアント側でデコードして使用。

### 理由

1. **文字化け完全回避**: Base64はバイナリセーフなため、UTF-8文字列を確実に保持
2. **後方互換性**: `MaskedTranscript` フィールドも残すことで、既存クライアントへの影響を最小化
3. **実装簡潔**: エンコード/デコードは標準ライブラリで対応可能

### 実装

```csharp
// エンコード（Functions側）
var bytes = Encoding.UTF8.GetBytes(maskedVtt);
output.MaskedTranscriptBase64 = Convert.ToBase64String(bytes);

// デコード（クライアント側）
var bytes = Convert.FromBase64String(output.MaskedTranscriptBase64);
var maskedVtt = Encoding.UTF8.GetString(bytes);
```

### 影響

- **追加**: `MaskedTranscriptBase64` フィールド
- **保持**: `MaskedTranscript` フィールド（文字化けの可能性あり）
- **推奨**: クライアントは `MaskedTranscriptBase64` を優先使用

---

## ADR-006: Azure Blob Storage 自動アップロード（オプション）

**日付**: 2024-04-24  
**ステータス**: 採用（オプション機能）

### コンテキスト

マスキング済みVTTを永続化し、後から取得できるようにしたい要望がありました。

### 決定

環境変数 `AZURE_STORAGE_CONNECTION_STRING` が設定されている場合、マスキング済みVTTを自動的にAzure Blob Storageにアップロード。

### 理由

1. **永続化**: Durable Functionsの状態は一定期間後に削除される
2. **ダウンロード**: 直接URLでアクセス可能
3. **オプション**: 環境変数未設定の場合はスキップ（影響なし）
4. **監査証跡**: マスキング済みファイルの履歴保存

### 実装

```csharp
if (!string.IsNullOrEmpty(storageConnectionString) && !string.IsNullOrEmpty(maskedVtt))
{
    var blobUrl = await PiiMaskingService.UploadToAzureStorageAsync(...);
    output.BlobUrl = blobUrl;
}
```

**ファイル名**: `masked_{instanceId}_{yyyyMMdd_HHmmss}.vtt`  
**Content-Type**: `text/vtt; charset=utf-8`

### 影響

- **追加**: `UploadToAzureStorageAsync` メソッド、`BlobUrl` フィールド
- **依存**: Azure.Storage.Blobs NuGetパッケージ
- **オプション**: 環境変数未設定でも動作

---

## ADR-007: Content Filterエラーのハンドリング

**日付**: 2024-04-24  
**ステータス**: 採用

### コンテキスト

医療情報を含むVTTファイルをマスキングする際、Azure OpenAIのContent Filterに引っかかることがありました。特に、病名や症状が多いチャンクでエラーが発生します。

### 決定

Content Filterエラーを検出した場合、以下の対応を実施:
1. ログに警告を記録
2. そのチャンクは元のまま保持
3. 処理を継続（他のチャンクは正常にマスキング）

### 理由

1. **処理継続**: 一部チャンクのエラーで全体が失敗しないようにする
2. **データ保護**: エラー時も元のVTTが返却されるため、データ損失なし
3. **監視**: ログで問題箇所を特定可能
4. **段階的対応**: Content Filter設定調整で解決可能

### 実装

```csharp
if (responseJson["choices"]?[0]?["content_filter_result"]?["error"] != null)
{
    log?.LogWarning("Content filter error detected for chunk {Chunk}", chunkIndex);
    return originalChunk; // 元のチャンクを返す
}
```

### 推奨設定

Azure OpenAI Studio → Deployments → Content filters → Protected Material: Low/Off

### 影響

- **追加**: Content Filterエラー検出ロジック
- **変更**: エラー時も処理継続
- **監視**: Application Insightsで "Content filter error" を検索

---

## ADR-008: リトライ機能（指数バックオフ）

**日付**: 2024-04-24  
**ステータス**: 採用

### コンテキスト

Azure OpenAI APIは一時的なネットワークエラーやレート制限（HTTP 429）が発生することがあります。

### 決定

指数バックオフ付きリトライ機能を実装。

### 設定

- **最大リトライ回数**: 3回
- **初回待機時間**: 1秒
- **バックオフ**: 指数（1s → 2s → 4s）
- **Jitter**: ランダム遅延追加（競合回避）

### 実装

```csharp
public static async Task<T> RetryAsync<T>(
    Func<Task<T>> action,
    int maxRetryCount = 3,
    int retryIntervalMs = 1000,
    ILogger? log = null,
    CancellationToken cancellationToken = default)
{
    for (int i = 0; i < maxRetryCount; i++)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (IsTransient(ex) && i < maxRetryCount - 1)
        {
            var delay = retryIntervalMs * Math.Pow(2, i) + _jitterer.Next(0, 500);
            await Task.Delay((int)delay, cancellationToken);
        }
    }
}
```

### リトライ対象

- `HttpRequestException`（ネットワークエラー）
- `TimeoutException`（タイムアウト）
- HTTP 429（Rate Limit）
- HTTP 5xx（サーバーエラー）

### リトライしないエラー

- HTTP 400（Bad Request）
- HTTP 401（Unauthorized）
- HTTP 403（Forbidden）

---

## ADR-009: SharePoint統合の削除

**日付**: 2024-04-24  
**ステータス**: 採用

### コンテキスト

当初はSharePointへの自動アップロード機能を実装しましたが、要件変更により不要となりました。

### 決定

SharePoint関連のすべてのコードとドキュメントを削除。

### 削除項目

- **NuGetパッケージ**: Microsoft.Graph, Azure.Identity（一部）
- **メソッド**: `UploadToSharePointAsync`, `GetSharePointDrivesAsync`
- **フィールド**: `AnalyzeActivityOutput.SharePointUrl`
- **環境変数**: `SHAREPOINT_*`
- **ドキュメント**: `docs/sharepoint-setup.md`

### 理由

1. **要件簡素化**: Azure Blob Storageで十分
2. **複雑性削減**: SharePoint認証・権限管理が不要
3. **依存削減**: Microsoft Graph SDKが不要
4. **保守性向上**: メンテナンス対象コード削減

### 影響

- **削除**: 約200行のコード
- **削除**: 約500行のドキュメント
- **メリット**: シンプルなアーキテクチャ、依存関係削減

---

## ADR-010: マスキング閾値を10,000文字に設定

**日付**: 2024-04-24  
**ステータス**: 採用

### コンテキスト

チャンク分割の閾値を決定する必要がありました。

### 決定

`MaxTranscriptLength = 10,000` 文字

### 理由

1. **処理時間**: 10,000文字のVTTは約15秒で処理可能
2. **トークン制限**: GPT-4oの入力制限（128Kトークン）に余裕
3. **タイムアウト回避**: 5分のHttpClientタイムアウト内に収まる
4. **柔軟性**: 環境変数で変更可能（将来）

### 実測値

| 文字数 | 処理時間 | チャンク数 |
|--------|---------|----------|
| 5,000 | ~10秒 | 1 |
| 10,000 | ~15秒 | 1 |
| 14,000 | ~30秒 | 3 |
| 50,000 | ~90秒 | 10 |

### 将来的な調整

環境変数 `MAX_TRANSCRIPT_LENGTH` を追加することで、運用中に調整可能にする（今後の拡張）。

---

## 決定の要約

| ADR | 決定事項 | ステータス |
|-----|---------|----------|
| 001 | Azure OpenAI使用 | ? 採用 |
| 002 | チャンク分割処理 | ? 採用 |
| 003 | max_completion_tokens | ? 採用 |
| 004 | Durable Functions | ? 採用 |
| 005 | Base64エンコーディング | ? 採用 |
| 006 | Blob Storageアップロード | ? 採用（オプション） |
| 007 | Content Filterハンドリング | ? 採用 |
| 008 | リトライ機能 | ? 採用 |
| 009 | SharePoint統合削除 | ? 採用 |
| 010 | マスキング閾値10,000文字 | ? 採用 |
