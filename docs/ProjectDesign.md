# プロジェクト設計書

## 1. プロジェクト概要

### 目的
Azure OpenAI (GPT-4o) を使用してWebVTT字幕ファイルから個人情報（PII）と医療情報を検出・マスキングし、HTTP API として提供する。

### 主要フロー
```
VTT送信 → Azure OpenAI Chat Completions API呼び出し 
       → プロンプトベースでマスキング 
       → マスキング済みVTT返却
```

### ユースケース
- Microsoft Teams会議のトランスクリプトから機密情報を自動削除
- 医療情報を含むVTTファイルの匿名化
- 法人番号、社員番号、マイナンバー等のマスキング

## 2. スコープ

### 範囲内
- WebVTT形式のPII/医療情報マスキング
- Azure OpenAI GPT-4oの活用
- Durable Functions による非同期処理
- チャンク分割処理（大きなVTT対応）
- Content Filterエラーハンドリング
- Azure Blob Storage 自動アップロード（オプション）

### 範囲外
- VTT以外の形式（SRT、TXT等）のマスキング
- リアルタイムストリーミング処理
- 音声ファイルからのトランスクリプト生成
- SharePoint統合（削除済み）

## 3. 技術スタック

### プラットフォーム
- **.NET 6**
- **Azure Functions v4**
- **Durable Functions 2.7.0**

### 主要ライブラリ

| パッケージ | バージョン | 用途 |
|-----------|----------|------|
| Microsoft.NET.Sdk.Functions | 4.5.0 | Azure Functions SDK |
| Microsoft.Azure.WebJobs.Extensions.DurableTask | 2.7.0 | Durable Functions |
| Azure.AI.OpenAI | 2.0.0 | Azure OpenAI API |
| Azure.Storage.Blobs | 12.17.0 | Blob Storageアップロード |
| Newtonsoft.Json | - | JSONシリアライゼーション |

### 外部サービス
- **Azure OpenAI Service** (GPT-4o)
  - API: Chat Completions
  - パラメータ: `max_completion_tokens`（GPT-4o系必須）
  - Temperature: 0.0（一貫性重視）

## 4. プロジェクト構成

### ディレクトリ構造

```
piiMasking/
├── src/
│   └── PiiMaskingFunction/
│       ├── PiiMaskingDurable.cs           # Durable Functions エントリーポイント
│       │   ├── PiiMasking_HttpStart       # HTTP Trigger（開始）
│       │   ├── PiiMasking_Orchestrator    # Orchestrator（ワークフロー）
│       │   └── PiiMasking_RunAnalyze      # Activity（実処理）
│       ├── FunctionBase.cs                # 共通サービス
│       │   ├── PiiMaskingService          # マスキングロジック
│       │   ├── FunctionBaseRetry          # リトライロジック
│       │   └── Data Models                # DTO、モデル
│       ├── host.json                      # Functions設定
│       ├── local.settings.json            # ローカル環境変数
│       └── PiiMaskingFunction.csproj      # プロジェクトファイル
├── tools/
│   ├── AzureFunctionTester/               # デプロイ後テストツール
│   └── OpenAiTester/                      # ローカルテストツール
├── docs/
│   ├── architecture.md                    # アーキテクチャ設計書
│   ├── decisions.md                       # ADR（アーキテクチャ決定記録）
│   └── ProjectDesign.md                   # このファイル
└── README.md                              # プロジェクト概要
```

## 5. データフロー（Durable Functions）

```
┌────────────────┐
│   クライアント   │
└───────┬────────┘
        │ POST /api/maskpii
        │ { "transcript": "WEBVTT..." }
        ▼
┌───────────────────────────┐
│ PiiMasking_HttpStart      │ ← HTTP Trigger
│ - リクエスト検証          │
│ - オーケストレーション開始 │
│ - ステータスURL返却       │
└───────┬───────────────────┘
        │
        ▼
┌───────────────────────────┐
│ PiiMasking_Orchestrator   │ ← Orchestrator
│ - ワークフロー制御        │
│ - Activity呼び出し        │
│ - カスタムステータス設定  │
└───────┬───────────────────┘
        │
        ▼
┌───────────────────────────┐
│ PiiMasking_RunAnalyze     │ ← Activity
│ ┌───────────────────────┐ │
│ │ 1. VTTサイズチェック  │ │
│ │ 2. チャンク分割判定   │ │
│ │ 3. Azure OpenAI呼出   │ │
│ │ 4. レスポンス処理     │ │
│ │ 5. Base64エンコード   │ │
│ │ 6. Storageアップロード│ │
│ └───────────────────────┘ │
│         │                 │
│         ├──? Azure OpenAI │
│         │    (GPT-4o)     │
│         │                 │
│         └──? Azure Storage│
│              (オプション)  │
└───────┬───────────────────┘
        │
        ▼
┌───────────────────────────┐
│ Durable Functions Runtime │
│ - 状態管理                │
│ - ステータスクエリ        │
└───────┬───────────────────┘
        │ GET {statusQueryGetUri}
        ▼
┌───────────────────────────┐
│ レスポンス                │
│ { "runtimeStatus": "...", │
│   "output": {...} }       │
└───────────────────────────┘
```

## 6. マスキング処理の詳細

### VTTファイルマスキング

**処理フロー**:
1. VTTファイルサイズをチェック
2. 10,000文字以下: そのままマスキング
3. 10,000文字超: チャンク分割処理

**チャンク分割処理**:
```
VTTファイル（例: 14,000文字）
    ↓
ヘッダー分離
    ↓
キュー単位で分割（例: 140キュー）
    ↓
50キューごとにチャンク作成（例: 3チャンク）
    ├─ Chunk 1: キュー1-50
    ├─ Chunk 2: キュー51-100
    └─ Chunk 3: キュー101-140
    ↓
各チャンクを個別にマスキング（順次処理）
    ↓
ヘッダー + マスキング済みチャンクを結合
    ↓
マスキング済みVTT出力
```

### Azure OpenAI プロンプト設計

**システムプロンプト**（抜粋）:
```
あなたはWebVTT字幕ファイルの個人情報マスキング専門家です。

【必ずマスキングする情報】
1. 識別番号
   - マイナンバー（個人番号）: 12桁の数字 → ************
   - 社員番号: 英数字 → *******
   - 年金番号、法人番号、株主番号、保険証番号 → すべてマスキング

2. 金融情報
   - クレジットカード番号、銀行口座番号、IBAN、SWIFT → すべてマスキング

3. 身分証明書
   - パスポート番号、運転免許証番号、在留カード番号 → すべてマスキング

4. 医療情報（重要）
   - 病名: 糖尿病、高血圧、乳がん、適応障害、うつ病 等 → すべてマスキング
   - 症状、検査数値（HbA1c、血糖値等）、医薬品名 → すべてマスキング
   - 診断内容、入院歴、後遺障害等級 → すべてマスキング

【保持する情報】
- 人名、住所、郵便番号、日付、電話番号、メールアドレス
- WebVTT構造（タイムスタンプ、キュー番号、話者タグ）
```

**ユーザープロンプト**:
```
以下のWebVTT字幕ファイルから個人情報をマスキングしてください。
VTT構造は完全に保持してください。

{VTT内容}
```

## 7. エラーハンドリング

### リトライ戦略

| 項目 | 設定値 |
|------|--------|
| 最大リトライ回数 | 3回 |
| 初回待機時間 | 1秒 |
| バックオフ方式 | 指数バックオフ（1s → 2s → 4s） |
| Jitter | ランダム遅延追加（0-500ms） |

**リトライ対象エラー**:
- `HttpRequestException`
- `TimeoutException`
- HTTP 429（Rate Limit）
- HTTP 5xx（サーバーエラー）

**リトライしないエラー**:
- HTTP 400（Bad Request）
- HTTP 401（Unauthorized）
- HTTP 403（Forbidden）

### Content Filterエラー

**検出方法**:
```csharp
if (responseJson["choices"]?[0]?["content_filter_result"]?["error"] != null)
{
    log.LogWarning("Content filter error detected");
    return originalChunk; // 元のチャンクを返す
}
```

**対応**:
1. ログに警告記録
2. そのチャンクは元のまま保持
3. 処理継続（他のチャンクは正常にマスキング）

**推奨設定**:
- Azure OpenAI Studio → Content filters → Protected Material: Low/Off

## 8. データモデル

### リクエスト

```csharp
public class MaskPiiRequest
{
    public string Transcript { get; set; } // WebVTT内容
}
```

### レスポンス（Durable Functions）

**初回レスポンス** (202 Accepted):
```json
{
  "id": "instance-id",
  "statusQueryGetUri": "https://.../instances/{id}?code=...",
  "sendEventPostUri": "...",
  "terminatePostUri": "...",
  "purgeHistoryDeleteUri": "..."
}
```

**ステータスクエリレスポンス** (Completed):
```json
{
  "runtimeStatus": "Completed",
  "output": {
    "OperationId": "instance-id",
    "MaskedTranscript": "WEBVTT\n\n1\n...",
    "MaskedTranscriptBase64": "V0VCVlRU...",
    "BlobUrl": "https://storage.blob.core.windows.net/...",
    "ProcessingTimeMs": 30000,
    "Error": null,
    "IsSuccess": true
  }
}
```

### 内部モデル

```csharp
public class AnalyzeActivityInput
{
    public string InstanceId { get; set; }
    public string VttContent { get; set; }
}

public class AnalyzeActivityOutput
{
    public string OperationId { get; set; }
    public string MaskedTranscript { get; set; }
    public string? MaskedTranscriptBase64 { get; set; }
    public string? BlobUrl { get; set; }
    public long ProcessingTimeMs { get; set; }
    public string? Error { get; set; }
    public bool IsSuccess => string.IsNullOrEmpty(Error);
}
```

## 9. セキュリティ

### 認証
- **Function Key**: HTTPリクエストのクエリパラメータ `code`
- **Azure OpenAI**: APIキー認証（環境変数管理）

### データ保護
- **転送中**: HTTPS通信のみ（TLS 1.2以上）
- **保存時**: Azure Storage暗号化（既定で有効）
- **機密情報**: 環境変数で管理、推奨: Azure Key Vault

### Content Filter
- **設定**: Protected Material: Low/Off（医療情報対応）
- **その他**: 既定値（Hate, Violence, Sexual, Self-harm）

## 10. パフォーマンス

### 処理時間

| VTTサイズ | チャンク数 | API呼び出し | 処理時間 |
|----------|----------|-----------|---------|
| 5,000文字 | 1 | 1回 | ~10秒 |
| 10,000文字 | 1 | 1回 | ~15秒 |
| 14,000文字 | 3 | 3回 | ~30秒 |
| 50,000文字 | 10 | 10回 | ~90秒 |

### スケーラビリティ
- **最大インスタンス数**: 200（従量課金プラン）
- **制限要因**: Azure OpenAI TPM/RPM
- **コールドスタート**: 1-5秒

### 最適化施策
- チャンク分割（タイムアウト回避）
- リトライ機能（一時的エラー対応）
- Base64エンコーディング（シリアライゼーション最適化）

## 11. 監視・ログ

### Application Insights

**自動収集**:
- HTTPリクエスト
- Function実行時間
- 例外・エラー
- 依存関係（Azure OpenAI API呼び出し）

**カスタムログ**:
```csharp
log.LogInformation("Starting VTT masking for instance {InstanceId}", instanceId);
log.LogWarning("Content filter error for chunk {Chunk}", chunkIndex);
log.LogError(ex, "VTT masking failed");
```

**推奨クエリ**（Kusto）:
```kusto
// 成功率
traces
| where message contains "VTT masking completed"
| summarize SuccessCount=count() by bin(timestamp, 1h)

// Content Filterエラー
traces
| where message contains "Content filter error"
| summarize ErrorCount=count() by bin(timestamp, 1h)

// 平均処理時間
customMetrics
| where name == "ProcessingTimeMs"
| summarize avg(value) by bin(timestamp, 1h)
```

## 12. テスト戦略

### ユニットテスト（今後実装推奨）
- PiiMaskingService のマスキングロジック
- チャンク分割アルゴリズム
- リトライ機能

### 統合テスト
- **AzureFunctionTester**: デプロイ後の動作確認
  - VTTファイル読み込み
  - Azure Functions API呼び出し
  - ステータスポーリング
  - 結果検証

### テストケース
- ? 小さいVTT（5,000文字以下）
- ? 大きいVTT（14,000文字、チャンク分割）
- ? 医療情報を含むVTT
- ? 法人番号、社員番号、マイナンバーのマスキング
- ? WebVTT構造の保持

## 13. デプロイ

### 環境変数

#### 必須
```
AZURE_OPENAI_ENDPOINT=https://your-openai.openai.azure.com/
AZURE_OPENAI_KEY=your-api-key
OPENAI_DEPLOYMENT_NAME=gpt-4o
AzureWebJobsStorage=DefaultEndpointsProtocol=https;...
```

#### オプション
```
AZURE_STORAGE_CONNECTION_STRING=DefaultEndpointsProtocol=https;...
STORAGE_CONTAINER_NAME=masked-vtt
```

### デプロイ手順
```bash
# ビルド
dotnet build src/PiiMaskingFunction/PiiMaskingFunction.csproj

# デプロイ
cd src/PiiMaskingFunction
func azure functionapp publish <your-function-app-name>
```

## 14. 制約事項

### 既知の制限
- WebVTT形式のみサポート
- 最大処理時間: 10分（Function実行時間制限）
- Azure OpenAI TPM/RPM制限に依存
- Base64エンコーディング必須（文字化け対策）

### 今後の改善項目
- チャンク並列処理
- カスタムマスキングルールの動的設定
- 複数VTTファイルのバッチ処理
- Webhook通知（処理完了時）
- マスキング統計レポート

## 15. 参考資料

### 内部ドキュメント
- [アーキテクチャ設計書](architecture.md)
- [アーキテクチャ決定記録（ADR）](decisions.md)

### 外部リソース
- [Azure OpenAI Service](https://learn.microsoft.com/azure/ai-services/openai/)
- [Azure Durable Functions](https://learn.microsoft.com/azure/azure-functions/durable/)
- [WebVTT仕様](https://www.w3.org/TR/webvtt1/)
