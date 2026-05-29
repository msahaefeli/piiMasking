# VTT PII Masking Function with Azure OpenAI

Azure OpenAIを使用してTeams会議のWebVTT字幕ファイルからPII(個人を特定できる情報)と病歴情報をマスキングするAzure Functionsアプリケーションです。

## 概要

Azure OpenAI (GPT-4o) を使用して、WebVTT字幕ファイルのマスキングを提供します：

- **VTTファイルマスキング**: WebVTT形式の字幕ファイルを構造を保持したままマスキング
- **非同期処理**: Durable Functionsを使用した非同期処理パターン
- **自動リトライ**: エクスポネンシャルバックオフによる自動リトライ機能
- **チャンク分割**: 大きなVTTファイルを自動的に分割処理

### 主な特徴

✅ **Azure OpenAI完結**: Azure AI Languageは不要、OpenAIのみで動作  
✅ **VTT構文保持**: タイムスタンプ、キュー構造、話者タグを完全に維持  
✅ **大規模ファイル対応**: 10,000文字超のファイルを自動分割  
✅ **カスタマイズ可能**: マスキング対象/保持項目をプロンプトで調整可能

## マスキング仕様

### 保持される情報 (マスキングされない)

- 👤 **人名**
- 📅 **年齢**
- 🏠 **住所**
- 📮 **郵便番号**
- 📆 **日付**
- 📞 **電話番号**
- 📧 **メールアドレス**
- 🌐 **URL**

### マスキングされる情報

| カテゴリ | 例 |
|---------|-----|
| **識別番号** | マイナンバー、社員番号、年金番号、法人番号、株主番号、保険証番号 |
| **金融情報** | クレジットカード番号、銀行口座番号 |
| **身分証明** | パスポート番号、運転免許証番号 |
| **医療情報** | 病名、症状、検査数値、医薬品名、診断内容、入院歴、治療内容 |

### マスキング例

**入力**:
```
中島 洋一、社員番号 E-11102、生年月日 1978年3月30日、マイナンバー 765432109876。
中島さんは 2型糖尿病 の診断を受けており、メトホルミン 500mg を服用中です。
```

**出力**:
```
中島 洋一、社員番号 ******、生年月日 1978年3月30日、マイナンバー ************。
中島さんは ****** の診断を受けており、******** 500mg を服用中です。
```

## セットアップ

### 前提条件

- .NET 6.0 SDK
- Azure Functions Core Tools v4
- Azure OpenAI リソース (GPT-4o デプロイ済み)

### 環境変数設定

`src/PiiMaskingFunction/local.settings.json` を作成：

```json
{
  "IsEncrypted": false,
  "Values": {
    "FUNCTIONS_WORKER_RUNTIME": "dotnet",
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "AZURE_OPENAI_ENDPOINT": "https://your-openai.openai.azure.com/",
    "AZURE_OPENAI_KEY": "your-api-key",
    "OPENAI_DEPLOYMENT_NAME": "gpt-4o",
    // オプション: Azure Storageへの自動アップロード
    "AZURE_STORAGE_CONNECTION_STRING": "DefaultEndpointsProtocol=https;AccountName=...",
    "STORAGE_CONTAINER_NAME": "masked-vtt"
  }
}
```

# PII Masking Azure Functions

Azure OpenAI (GPT-4o) を使用して、Microsoft Teams会議のトランスクリプト（WebVTT形式）から個人情報（PII）と医療情報をマスキングするAzure Durable Functions。

---

## 📚 プロジェクト再現ガイド

このプロジェクトを一から再現する方法については、以下のドキュメントを参照してください：

### 🚀 技術者向け - 最速で再現
- **[SUPER_PROMPT.txt](SUPER_PROMPT.txt)** - **1回のプロンプトで完全再現**（10-15分）
  - このファイル全体をコピー＆ペーストするだけ
  - AIが全ファイルを自動生成
  - コード、設定、テストツール、サンプルデータすべて含む

### 👔 非技術者向け - やりたいことを伝えるだけ
- **[NON_TECHNICAL_PROMPTS.md](NON_TECHNICAL_PROMPTS.md)** - **プログラミング知識不要**
  - 「やりたいこと」を自然言語で書くだけ
  - 具体例とビジネス要件を伝えればOK
  - 技術用語を使わずにシステムを発注できる

### 💼 経営者・マネージャー向け - ビジネス要件で発注
- **[BUSINESS_REQUIREMENTS.md](BUSINESS_REQUIREMENTS.md)** - **要件定義書テンプレート**
  - コピペして使える完全な要件定義
  - コスト試算、ROI計算のプロンプト付き
  - セキュリティ要件、運用要件も完備

### 📖 詳細な手順を理解したい場合
- **[RECREATION_GUIDE.md](RECREATION_GUIDE.md)** - 完全な再現手順（段階的なプロンプトとファイルテンプレート）
- **[MINIMAL_RECREATION.md](MINIMAL_RECREATION.md)** - 最小限のファイルセットで30分以内に再現
- **[PROMPT_HISTORY.md](PROMPT_HISTORY.md)** - 実際に使用したプロンプトの履歴
- **[ONE_PROMPT_COMPLETE.md](ONE_PROMPT_COMPLETE.md)** - ワンプロンプト再現の詳細ガイド

---

## 主な機能

### ✅ 高精度なPIIマスキング
- **マスキング対象**:
  - 識別番号: マイナンバー、社員番号、年金番号、法人番号、株主番号、保険証番号
  - 金融情報: クレジットカード番号、銀行口座番号、IBAN、SWIFT
  - 身分証明書: パスポート番号、運転免許証番号、在留カード番号
  - 医療情報: 病名、症状、検査数値（HbA1c、血糖値等）、医薬品名、診断内容、入院歴、後遺障害等級
  - セキュリティ情報: パスワード、PIN、セキュリティコード（CVV）、IPアドレス、VIN

- **保持する情報**:
  - 人名、年齢、住所、郵便番号、日付、電話番号、メールアドレス、URL

### ✅ WebVTT完全対応
- タイムスタンプ、キュー番号、話者タグを完全保持
- VTT構文を維持したまま処理
- 大きなファイル（10,000文字超）は自動的にチャンク分割

### ✅ エンタープライズ対応
- Durable Functions による非同期・長時間処理対応
- リトライ機能（指数バックオフ）
- Content Filterエラーのハンドリング
- Base64エンコーディングによる文字化け対策
- Azure Blob Storage への自動アップロード（オプション）

## アーキテクチャ

```
HTTP Request (VTT) 
    → PiiMasking_HttpStart (Durable Function)
    → PiiMasking_Orchestrator
    → PiiMasking_RunAnalyze (Activity)
        → Azure OpenAI API (GPT-4o)
        → チャンク分割処理（10,000文字超の場合）
        → Azure Blob Storage アップロード（オプション）
    → HTTP Response (Masked VTT + BlobUrl)
```

## セットアップ

### 必要な環境

- .NET 6.0 SDK
- Azure Functions Core Tools v4
- Azure OpenAI リソース (GPT-4o デプロイ済み)
- Azure Storage Account（オプション: ファイルアップロード用）

### 環境変数設定

#### ローカル開発環境

`src/PiiMaskingFunction/local.settings.json`:

```json
{
  "Values": {
    "FUNCTIONS_WORKER_RUNTIME": "dotnet",
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "AZURE_OPENAI_ENDPOINT": "https://your-openai.openai.azure.com/",
    "AZURE_OPENAI_KEY": "your-api-key",
    "OPENAI_DEPLOYMENT_NAME": "gpt-4o",
    // オプション: Azure Storage アップロード
    "AZURE_STORAGE_CONNECTION_STRING": "DefaultEndpointsProtocol=https;AccountName=...",
    "STORAGE_CONTAINER_NAME": "masked-vtt"
  }
}
```

#### Azure Functions App（本番環境）

Azure Portal → Functions App → 構成 → アプリケーション設定:

| 名前 | 値 | 必須 |
|------|-----|------|
| `AZURE_OPENAI_ENDPOINT` | Azure OpenAI エンドポイントURL | ✅ |
| `AZURE_OPENAI_KEY` | Azure OpenAI APIキー | ✅ |
| `OPENAI_DEPLOYMENT_NAME` | デプロイメント名（例: gpt-4o） | ✅ |
| `AZURE_STORAGE_CONNECTION_STRING` | Storage Account接続文字列 | オプション |
| `STORAGE_CONTAINER_NAME` | コンテナ名（デフォルト: masked-vtt） | オプション |

### Azure OpenAI Content Filter 設定

医療情報を含むVTTファイルを処理するため、Content Filterを調整してください：

1. Azure OpenAI Studio を開く
2. **Deployments** → GPT-4oデプロイメントを選択
3. **Content filters** を編集
4. **Protected Material** を **Low** または **Off** に設定
5. 保存

## ビルドと実行

### ローカル実行

```bash
# ビルド
dotnet build src/PiiMaskingFunction/PiiMaskingFunction.csproj

# ローカル実行
cd src/PiiMaskingFunction
func start
```

### Azure へデプロイ

```bash
cd src/PiiMaskingFunction
func azure functionapp publish <your-function-app-name>
```

## 使用方法

### API呼び出し

**エンドポイント**: `POST /api/maskpii`

**リクエストボディ**:
```json
{
  "transcript": "WEBVTT\n\n1\n00:00:00.000 --> 00:00:05.000\n<v 田中>マイナンバーは 123456789012 です。"
}
```

**レスポンス** (Durable Functionsの標準レスポンス):
```json
{
  "id": "abc123",
  "statusQueryGetUri": "https://.../runtime/webhooks/durabletask/instances/abc123",
  "sendEventPostUri": "https://...",
  "terminatePostUri": "https://...",
  "purgeHistoryDeleteUri": "https://..."
}
```

### ステータス確認とマスキング結果取得

`statusQueryGetUri` にGETリクエスト:

```json
{
  "runtimeStatus": "Completed",
  "output": {
    "MaskedTranscript": "WEBVTT\n\n1\n00:00:00.000 --> 00:00:05.000\n<v 田中>マイナンバーは ************ です。",
    "MaskedTranscriptBase64": "V0VCVlRU...",
    "BlobUrl": "https://yourstorage.blob.core.windows.net/masked-vtt/masked_abc123_20260424_120000.vtt",
    "ProcessingTimeMs": 30000,
    "Error": null,
    "IsSuccess": true
  }
}
```

### テストツール

デプロイ後の動作確認:

```powershell
dotnet run --project tools/AzureFunctionTester/AzureFunctionTester.csproj
```

このツールは以下を自動実行します:
1. VTTファイルを読み込み
2. Azure Functionsにリクエスト送信
3. ステータスをポーリング
4. 結果を `TestResults/` に保存
5. マスキング結果を検証

## Azure Storage アップロード（オプション）

環境変数 `AZURE_STORAGE_CONNECTION_STRING` を設定すると、マスキング済みVTTを自動的にAzure Blob Storageにアップロードします。

**設定手順**:

1. Storage Account 作成（または既存のものを使用）
2. 接続文字列を取得: Storage Account → アクセスキー → 接続文字列
3. 環境変数に設定:
   - `AZURE_STORAGE_CONNECTION_STRING`: 接続文字列
   - `STORAGE_CONTAINER_NAME`: `masked-vtt`（または任意）

**アップロード結果**:

レスポンスの `output.BlobUrl` にアップロード先URLが含まれます:
```
https://yourstorage.blob.core.windows.net/masked-vtt/masked_abc123_20260424_120000.vtt
```

## パフォーマンス

### 処理時間

| VTTサイズ | チャンク数 | 処理時間 |
|----------|----------|---------|
| 5,000文字 | 1 | ~10秒 |
| 10,000文字 | 1 | ~15秒 |
| 14,000文字 | 3 | ~30秒 |
| 30,000文字 | 6 | ~60秒 |

### チャンク分割

- **閾値**: 10,000文字
- **チャンクサイズ**: 50キュー/チャンク
- **メリット**: 処理速度向上、タイムアウトリスク軽減、Content Filterエラー対策

### タイムアウト設定

- **HttpClient**: 5分
- **Function全体**: 10分

## トラブルシューティング

### Content Filter Error

**症状**: Application Insightsに `content_filter_error` が記録される

**原因**: 医療情報が多いチャンクがフィルターに引っかかる

**解決策**:
1. Azure OpenAI Studio → Deployments → Content filters
2. Protected Material を **Low** または **Off** に設定
3. 変更を保存して再デプロイ

### 文字化け（日本語が `?` になる）

**症状**: マスキング結果の日本語が文字化けする

**原因**: Durable Functionsのシリアライゼーション問題

**解決策**: Base64フィールドを使用
```csharp
var bytes = Convert.FromBase64String(output.MaskedTranscriptBase64);
var maskedVtt = Encoding.UTF8.GetString(bytes);
```

### タイムアウト

**症状**: 大きなVTTファイルでタイムアウト

**解決策**:
1. チャンク分割閾値を下げる（`MaxTranscriptLength` を 5000 に変更）
2. Azure Functions の `functionTimeout` を延長（`host.json`）

## セキュリティ

### マスキング対象の拡張

`FunctionBase.cs` のシステムプロンプトを編集:

```csharp
var systemPrompt = @"あなたはWebVTT字幕ファイルの個人情報マスキング専門家です。
【必ずマスキングする情報】
- 新しいカテゴリを追加
...
```

### API キーの管理

**推奨**: Azure Key Vault を使用

```json
{
  "AZURE_OPENAI_KEY": "@Microsoft.KeyVault(SecretUri=https://your-vault.vault.azure.net/secrets/openai-key/)"
}
```

## プロジェクト構成

```
piiMasking/
├── src/
│   └── PiiMaskingFunction/
│       ├── PiiMaskingDurable.cs       # Durable Functions エントリーポイント
│       ├── FunctionBase.cs            # マスキングロジック
│       ├── PiiMaskingFunction.cs      # 従来のHTTP Function（削除予定）
│       ├── host.json                  # Functions設定
│       └── local.settings.json        # ローカル環境変数
├── tools/
│   ├── AzureFunctionTester/           # デプロイ先動作確認ツール
│   └── OpenAiTester/                  # ローカルOpenAIテストツール
├── TestResults/                        # テスト出力ディレクトリ
└── README.md                          # このファイル
```

## ライセンス

MIT License

## 参考リンク

- [Azure OpenAI Service](https://learn.microsoft.com/azure/ai-services/openai/)
- [Azure Durable Functions](https://learn.microsoft.com/azure/azure-functions/durable/)
- [WebVTT仕様](https://www.w3.org/TR/webvtt1/)

### ビルドと実行

```bash
# ビルド
dotnet build src/PiiMaskingFunction/PiiMaskingFunction.csproj

# ローカル実行
cd src/PiiMaskingFunction
func start
```

## API リファレンス

### POST /api/maskpii

テキストのPIIマスキング処理を開始します (非同期)。

**リクエスト:**
```json
{
  "transcript": "マスキング対象のテキスト"
}
```

**レスポンス (202 Accepted):**
```json
{
  "id": "instance-id",
  "statusQueryGetUri": "http://localhost:7071/api/status/instance-id"
}
```

### GET /api/status/{instanceId}

処理状態と結果を取得します。

**レスポンス (完了時):**
```json
{
  "operationId": "instance-id",
  "maskedTranscript": "マスキング済みテキスト",
  "entities": [],
  "processingTimeMs": 1234
}
```

## テスト

### VTTファイルマスキングのテスト

```bash
dotnet run --project tools/OpenAiTester/OpenAiTester.csproj
```

結果は `TestResults/` ディレクトリに保存されます。

## VTTファイル処理の詳細

### チャンク分割処理

VTTファイルが10,000文字を超える場合、自動的にチャンク分割処理を実行：

1. **VTT解析**: キューごとに分割
2. **チャンク作成**: 50キューごとにグループ化
3. **並列マスキング**: 各チャンクを個別にマスキング
4. **結合**: ヘッダーとマスキング済みチャンクを結合

### 処理統計例

- 元ファイル: 14,219文字、105キュー
- 分割: 3チャンク
- 処理時間: 約30秒
- 結果: 14,250文字 (構造保持、タイムスタンプ105個すべて保持)

## パフォーマンス

| VTTファイルサイズ | キュー数 | 処理時間 | チャンク数 |
|---------------|--------|---------|----------|
| ~5,000文字 | ~40 | ~5秒 | 1 |
| ~10,000文字 | ~80 | ~10秒 | 1 |
| ~15,000文字 | ~105 | ~30秒 | 3 |

## トラブルシューティング

### タイムアウトエラー

**症状**: `TaskCanceledException: The request was canceled`

**解決策**: HttpClientのタイムアウトは5分に設定済み。VTTファイルが大きい場合は自動的にチャンク分割されます。

### マスキングされない

**症状**: 出力が入力と同じ

**解決策**:
1. `AZURE_OPENAI_ENDPOINT` が正しいか確認
2. `AZURE_OPENAI_KEY` が有効か確認
3. `OPENAI_DEPLOYMENT_NAME` がデプロイ済みか確認
4. GPT-4o系モデルを使用 (`max_completion_tokens` サポート)

### 400 Bad Request

**症状**: `max_tokens is not supported with this model`

**解決策**: コードは `max_completion_tokens` を使用しています。古いモデル (GPT-3.5など) は非サポート。

## プロジェクト構造

詳細な設計情報は [docs/ProjectDesign.md](docs/ProjectDesign.md) を参照してください。

## ライセンス

このプロジェクトはMITライセンスの下で公開されています。

## 貢献

プルリクエストを歓迎します。大きな変更の場合は、まずissueを開いて変更内容を議論してください。

## サポート

問題が発生した場合は、GitHubのissueを作成してください。
