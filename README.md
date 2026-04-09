# piiMasking
# PII Masking Function

会議の文字起こしから個人情報（PII）を検出してマスキングする Azure Functions アプリケーションです。

## 概要

Azure AI Language (Text Analytics) を使用して、日本の法人向け保険営業の CRM 用途に最適化された PII マスキング機能を提供します。

### 特徴

- **非同期処理**: Durable Functions を使用した非同期処理パターン
 - **リトライ機能**: 指数バックオフによる自動リトライ
 - **日本向け最適化**: 日本固有の PII カテゴリ（マイナンバー、健康保険証番号など）に対応
 - **CRM 対応**: 営業活動に必要な情報（氏名、電話番号、メール）は保持

## リポジトリ構成

```
├── .github/          # PR/Issue テンプレート
├── docs/             # アーキテクチャ、設計ドキュメント
├── src/
│   └── PiiMaskingFunction/
│       ├── FunctionBase.cs       # 共通サービス、リトライ、データモデル
│       ├── PiiMaskingDurable.cs  # Durable Functions エントリポイント
│       ├── host.json             # Azure Functions 設定
│       └── local.settings.json   # ローカル環境変数
└── tests/            # ユニット・統合テスト
```

## クイックスタート

### 前提条件

- .NET 6.0 SDK
- Azure Functions Core Tools v4
- Azure AI Language リソース

### ローカル実行

1. `src/PiiMaskingFunction/local.settings.json` を設定:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet",
    "TEXT_ANALYTICS_ENDPOINT": "https://your-resource.cognitiveservices.azure.com/",
    "TEXT_ANALYTICS_KEY": "your-api-key"
  }
}
```

2. Azure Functions を起動:

```bash
cd src/PiiMaskingFunction
func start
```

## API リファレンス

### POST /api/maskpii

PII マスキング処理を開始します。

**リクエスト:**

```json
{
  "transcript": "田中太郎さんの電話番号は090-1234-5678です。クレジットカード番号は4111-1111-1111-1111です。"
}
```

**レスポンス (202 Accepted):**

```json
{
  "id": "instance-id",
  "statusQueryGetUri": "http://localhost:7071/api/status/instance-id",
  "terminatePostUri": "..."
}
```

### GET /api/status/{instanceId}

処理状態を確認します。

**レスポンス (処理中 - 202):**

```json
{
  "instanceId": "instance-id",
  "runtimeStatus": "Running",
  "createdTime": "2024-01-01T00:00:00Z",
  "lastUpdatedTime": "2024-01-01T00:00:01Z"
}
```

**レスポンス (完了 - 200):**

```json
{
  "operationId": "instance-id",
  "maskedTranscript": "田中太郎さんの電話番号は090-1234-5678です。クレジットカード番号は*******************です。",
  "entities": [
    {
      "category": "CreditCardNumber",
      "text": "4111-1111-1111-1111",
      "offset": 42,
      "length": 19
    }
  ],
  "processingTimeMs": 1234
}
```

## マスキング対象カテゴリ

### デフォルトでマスキングされるカテゴリ

| カテゴリ | 説明 |
|---------|------|
| `CreditCardNumber` | クレジットカード番号 |
| `BankAccountNumber` | 銀行口座番号 |
| `JPBankAccountNumber` | 日本の銀行口座番号 |
| `JPMyNumberPersonal` | マイナンバー（個人番号） |
| `JPMyNumberCorporate` | 法人番号 |
| `JPHealthInsuranceNumber` | 健康保険証番号 |
| `JPSocialInsuranceNumber` | 社会保険番号 |
| `JPDriversLicenseNumber` | 日本の運転免許証番号 |
| `JPPassportNumber` | 日本のパスポート番号 |
| `JPResidenceCardNumber` | 在留カード番号 |
| `PassportNumber` | パスポート番号（汎用） |
| `DriverLicenseNumber` | 運転免許証番号（汎用） |
| `IPAddress` | IPアドレス |
| `URL` / `Url` | URL |

### 保持されるカテゴリ（CRM 営業に必要）

- `Person`（氏名）
- `PhoneNumber`（電話番号）
- `Email`（メールアドレス）
- `Address`（住所）
- `Organization`（組織名）

### カスタマイズ

環境変数 `PII_MASK_CATEGORIES` でマスキング対象をカスタマイズできます：

```
PII_MASK_CATEGORIES=CreditCardNumber,JPMyNumberPersonal,Email
```

## 環境変数

| 変数名 | 必須 | 説明 | デフォルト |
|--------|------|------|----------|
| `TEXT_ANALYTICS_ENDPOINT` | 必須 | Azure AI Language のエンドポイント | - |
| `TEXT_ANALYTICS_KEY` | 必須 | Azure AI Language の API キー | - |
| `AzureWebJobsStorage` | 必須 | Durable Functions 用のストレージ接続文字列 | - |
| `PII_MASK_CATEGORIES` | 任意 | マスキング対象カテゴリ（カンマ区切り）。未指定時はデフォルトの一覧を使用 | デフォルト一覧 |
| `PII_RETRY_MAX_COUNT` | 任意 | 最大リトライ回数 | 3 |
| `PII_RETRY_BASE_DELAY_MS` | 任意 | リトライの基本遅延（ミリ秒） | 1000 |
| `PII_RETRY_MAX_DELAY_MS` | 任意 | リトライの最大遅延（ミリ秒） | 30000 |

## 制限事項

- 最大入力文字数: 125,000 文字（Azure AI Language の制限）
- 推奨入力文字数: 50,000 文字以下（パフォーマンス考慮）
- 処理タイムアウト: 10 分

## テスト

```bash
# ユニットテスト
dotnet test tests/PiiMaskingFunction.Tests

# 統合テスト（Azure AI Language への接続が必要）
dotnet test tests/PiiMaskingFunction.Tests

---

注意: 最近の変更に伴い、クレジットカード番号のヒューリスティック検出（`DetectCreditCardCandidates`）はソースから削除されました。現在は Azure AI Language による PII 検出結果と、設定されたマスクポリシー（環境変数 `PII_MASK_CATEGORIES`）に基づいてマスキング処理を行います。

統合／E2E テストについては、複数カテゴリを個別に確認するケースと、すべての PII 種類を1つの入力に含める結合ケースの両方を含むように更新しました。ライブサービスを使ったテストを実行する場合は、`src/PiiMaskingFunction/local.settings.json` または環境変数に `TEXT_ANALYTICS_ENDPOINT` と `TEXT_ANALYTICS_KEY` を設定してください。
```

## 開発・貢献

1. ブランチを切る: `feature/xxx` または `bugfix/xxx`
2. 変更は小さく、コミットメッセージに内容を明記
3. PR を作成し `.github/PULL_REQUEST_TEMPLATE.md` に従う
4. PR 前に差分を確認し、不要ファイルが含まれていないことを確認する

詳細は `docs/ProjectDesign.ja.md` および `docs/architecture.md` を参照してください。

## ライセンス

MIT License