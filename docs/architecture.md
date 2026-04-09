# アーキテクチャ
# PII マスキング機能 - アーキテクチャ

このプロジェクトは、会議の文字起こしを受け取り PII（個人識別情報）をマスクして返す HTTP トリガーの Azure Functions（Durable Functions）を提供します。PII の検出には Azure AI Language（Text Analytics）の PII 認識 API を利用し、検出されたエンティティを原文のオフセットを保ったままマスキングします。

## 主要なアーキテクチャポイント

### Durable Functions 構成

```
クライアント
  POST /api/maskpii -> PiiMasking_HttpStart (HTTP トリガー)
    -> PiiMasking_Orchestrator (オーケストレーター)
        -> PiiMasking_RunAnalyze (Activity: Text Analytics 呼び出し、リトライ含む)
    <- 結果を格納
  GET /api/status/{instanceId} -> PiiMasking_Status (処理状況のポーリング)
```

### コンポーネント

- `PiiMasking_HttpStart`: オーケストレーターを起動し、ポーリング用 URL を返す HTTP トリガー
- `PiiMasking_Orchestrator`: PII 検出とマスキングのワークフローを調整するオーケストレーター
- `PiiMasking_RunAnalyze`: Azure AI Language API を呼び出すアクティビティ（リトライ対応）
- `PiiMasking_Status`: オーケストレーションの状態を取得し結果を返す HTTP トリガー

### コア実装ファイル

- `PiiMaskingDurable.cs`: Azure Functions のエントリポイント（HTTP トリガー、オーケストレーター、アクティビティ）
- `FunctionBase.cs`: 共通ロジックとユーティリティ
  - `PiiMaskingService`: PII 検出、オフセット処理、マスキングのビジネスロジック
  - `FunctionBaseRetry`: 指数バックオフを使ったリトライユーティリティ
  - `PiiEntity` / `MaskingResult`: データモデル

### 主な特徴

- 非同期処理: Durable Functions パターンを採用し長時間処理を安全に扱う
- リトライと耐障害性: Text Analytics 呼び出しは過渡的エラー（5xx/429 など）を判定してリトライ
- 統合と重複排除: 検出結果を統合し重複しないエンティティに整形
- マスキング: 文字列の後ろから前に置換することでオフセット整合性を維持

## デフォルトのマスキングカテゴリ（日本向け B2B 保険営業シナリオ）

営業活動に必要な連絡先情報は保持し、機微な識別子のみをデフォルトでマスキングする設定になっています。

### デフォルトでマスクされるカテゴリ

| カテゴリ | 説明 |
|----------|------|
| `CreditCardNumber` | クレジットカード番号 |
| `BankAccountNumber` | 銀行口座番号 |
| `JPBankAccountNumber` | 日本の銀行口座番号 |
| `SWIFTCode` | SWIFT コード |
| `JPMyNumberPersonal` | マイナンバー（個人番号） |
| `JPMyNumberCorporate` | 法人番号 |
| `JPResidenceCardNumber` | 在留カード番号 |
| `JPDriversLicenseNumber` | 運転免許証番号（日本） |
| `JPPassportNumber` | パスポート番号（日本） |
| `JPSocialInsuranceNumber` | 社会保険番号 |
| `JPHealthInsuranceNumber` | 健康保険証番号 |
| `PassportNumber` | パスポート番号（汎用） |
| `DriverLicenseNumber` | 運転免許証番号（汎用） |
| `IPAddress` | IP アドレス |
| `URL` / `Url` | URL |

### CRM 用に保持されるカテゴリ

- `Person`（氏名）
- `PhoneNumber`（電話番号）
- `Email`（メールアドレス）
- `Address`（住所）
- `Organization`（組織名）

## 設定

次の環境変数で認証情報と挙動を制御します。

| 変数 | 説明 |
|------|------|
| `TEXT_ANALYTICS_ENDPOINT` | Azure AI Language のエンドポイント（必須） |
| `TEXT_ANALYTICS_KEY` | Azure AI Language の API キー（必須） |
| `AzureWebJobsStorage` | Durable Functions 用ストレージ接続文字列（必須） |
| `PII_MASK_CATEGORIES` | マスキング対象カテゴリ（カンマ区切り）。指定がなければデフォルト一覧を使用 |
| `PII_RETRY_MAX_COUNT` | 最大リトライ回数（デフォルト: 3） |
| `PII_RETRY_BASE_DELAY_MS` | リトライ基本遅延（ms、デフォルト: 1000） |
| `PII_RETRY_MAX_DELAY_MS` | リトライ最大遅延（ms、デフォルト: 30000） |

## テスト

- 単体テスト: `MaskTranscriptFromOffsets` やリトライユーティリティ等
- 統合テスト: 実際の Azure AI Language を呼ぶテスト（資格情報が必要）
- E2E テスト: 日本語サンプルを用いた PII 検出とマスキングの確認（個別カテゴリと全カテゴリを含む結合ケースを実施）
- パフォーマンステスト: 長文入力（数千?数万文字）を想定したスモークテスト。結果は `TestResults/*.jsonl` に出力されます。

