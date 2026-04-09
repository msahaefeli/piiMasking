プロジェクト設計書
=================

1) 概要
- 目的: Azure AI Language (Text Analytics) を用いてトランスクリプト（音声またはテキスト）から PII を検出・マスクし、HTTP API として提供する。
- 主なフロー: テキスト受信 → Text Analytics による PII 検出 → 検出結果に基づくマスキング → マスク済みテキストとエンティティ情報を返却。

2) 範囲
- コア: `src/PiiMaskingFunction`（Azure Functions v4 / .NET 6）
- 外部連携: Azure AI Language (`Azure.AI.TextAnalytics`)
- ローカル開発: Azure Functions Core Tools、`local.settings.json` による環境変数上書き

3) 技術スタック
- プラットフォーム: .NET 6, Azure Functions v4, Durable Functions
- ライブラリ: `Azure.AI.TextAnalytics`, `Azure.Core`, `Microsoft.NET.Sdk.Functions`, `Microsoft.Azure.WebJobs.Extensions.DurableTask`
- テスト: xUnit

4) プロジェクト構成（主要）
- `src/PiiMaskingFunction/PiiMaskingDurable.cs` ? Durable Functions HTTPトリガー、オーケストレーター、アクティビティ、ステータス確認
- `src/PiiMaskingFunction/FunctionBase.cs` ? 共通サービス（PiiMaskingService）、リトライユーティリティ（FunctionBaseRetry）、データモデル（PiiEntity, MaskingResult）
- `src/PiiMaskingFunction/local.settings.json` ? ローカル用環境変数（機密はコミットしない）
- `tests/` ? 単体・統合・E2E テスト

5) 高レベルデータフロー（Durable Functions）
```
クライアント
    │
    ├─POST→ /api/maskpii ───────────? PiiMasking_HttpStart
    │                                        │
    │                                        ▼
    │                               PiiMasking_Orchestrator
    │                                        │
    │                                        ▼
    │                               PiiMasking_RunAnalyze
    │                                        │ (Azure AI Language API呼び出し)
    │                                        │ (リトライ機能付き)
    │                                        ▼
    │                                 [結果保存]
    │
    └─GET→ /api/status/{instanceId} ?─── PiiMasking_Status
                                              (ポーリングで状態・結果取得)
```

6) 主要設計方針
- Durable Functions: 非同期処理パターンを採用。リクエスト送信後、ステータスと結果の監視は別のFunctionで実施
- リトライ戦略: 指数バックオフ + ジッター、トランジェント例外のみ再試行、`CancellationToken` 対応
- トランジェント判定例: `HttpRequestException`, `RequestFailedException`（5xx/429）, タイムアウト
- 非同期 Analyze Actions: 大きなトランスクリプトに対応するため、Text Analytics の長時間実行（Analyze Actions）API を使用して PII 検出を非同期で実行
- マージポリシー: 非同期 API から得られた検出結果を正規化・重複除去して、重複や重なりを解消したエンティティに統合
- 設定可能項目（環境変数）: `PII_RETRY_MAX_COUNT`, `PII_RETRY_BASE_DELAY_MS`, `PII_RETRY_MAX_DELAY_MS`, およびマスク対象カテゴリを指定する `PII_MASK_CATEGORIES`（カンマ区切り）

7) CRM 向け既定のマスク動作（日本の法人向け保険営業）

本関数は日本の法人向け CRM ワークフロー（B2B 保険営業）を想定した既定設定になっています。

**マスキング対象（デフォルト）:**

| カテゴリ | 説明 |
|---------|------|
| `CreditCardNumber` | クレジットカード番号 |
| `BankAccountNumber` | 銀行口座番号 |
| `JPBankAccountNumber` | 日本の銀行口座番号 |
| `SWIFTCode` | SWIFTコード |
| `JPMyNumberPersonal` | マイナンバー（個人番号） |
| `JPMyNumberCorporate` | 法人番号 |
| `JPResidenceCardNumber` | 在留カード番号 |
| `JPDriversLicenseNumber` | 日本の運転免許証番号 |
| `JPPassportNumber` | 日本のパスポート番号 |
| `JPSocialInsuranceNumber` | 社会保険番号 |
| `JPHealthInsuranceNumber` | 健康保険証番号 |
| `PassportNumber` | パスポート番号（汎用） |
| `DriverLicenseNumber` | 運転免許証番号（汎用） |
| `IPAddress` | IPアドレス |
| `URL` / `Url` | URL |

**保持されるカテゴリ（CRM営業に必要）:**
- `Person`（氏名）
- `PhoneNumber`（電話番号）
- `Email`（メールアドレス）
- `Address`（住所）
- `Organization`（組織名）

既定の上書き:
マスク対象を変更するには `PII_MASK_CATEGORIES` にカンマ区切りでカテゴリ名を指定してください。環境変数がある場合はコード内の既定より優先されます。

8) 環境・設定
- 必須: `TEXT_ANALYTICS_ENDPOINT`, `TEXT_ANALYTICS_KEY`, `AzureWebJobsStorage`（Functions 実行に必要）
- 任意（リトライ設定）: `PII_RETRY_MAX_COUNT`, `PII_RETRY_BASE_DELAY_MS`, `PII_RETRY_MAX_DELAY_MS`
- ローカル: `local.settings.json` で上書き可能（ただし機密は管理に注意）

9) ロギング & 可観測性
- 再試行時は `LogWarning`、最終失敗は `LogError` を出力
- 監視推奨: 再試行回数、成功率、レイテンシ
- 推奨ツール: Application Insights（アラート・ダッシュボード）

10) セキュリティ
- シークレットは Key Vault やアプリ設定で管理。`local.settings.json` は共有・コミット禁止
- ログに PII を出力しない（必要ならマスクしてから記録）

11) テスト
- 単体: `MaskTranscriptFromOffsets`、リトライヘルパ、エラーパス等
- 統合: 実サービス（Text Analytics）を呼ぶテスト（環境変数 or local.settings.json 必須）。結果を JSONL で保存
- E2E: 日本語サンプルで PII 検出・マスキングを検証
- パフォーマンステスト: 長い入力（2k?20k 文字など）でエンドツーエンド性能を測定し、検出数・処理時間を `TestResults/*.jsonl` に保存

E2E 結合テスト（実サービス）実行方法:
- E2E テストは実際に Text Analytics サービスを呼び出すため、CI では無効化されています
- ローカルで実行するには `RUN_E2E=true` を設定し、`TEXT_ANALYTICS_ENDPOINT` と `TEXT_ANALYTICS_KEY` を用意してください
- 実行例（Windows cmd）:
  - `set RUN_E2E=true && dotnet test tests\\PiiMaskingFunction.Tests --filter FullyQualifiedName~PiiMaskingFunctionEndToEndAllPiiTests.Run_Masks_All_Pii_Types_In_Japanese_Inputs`
- 結果ファイルはリポジトリルートの `TestResults/` に JSONL で出力されます

12) CI/CD（推奨）
- GitHub Actions のワークフロー: restore → build → test → deploy（Azure Functions へ）。シークレットは GitHub Secrets で管理
- テスト結果や統合ログはアーティファクトとして保存可能

13) 将来の改善案
- `Polly` 導入による複合ポリシー（リトライ＋サーキットブレーカー）
- API 呼び出しのバッチ化やレート制御でコスト削減
- ローカライズ別の PII マッピング最適化
- テスト結果の履歴比較／ダッシュボード化

14) Runbook（要約）
- ローカル実行: `TEXT_ANALYTICS_ENDPOINT` と `TEXT_ANALYTICS_KEY` を設定し `func start` または `dotnet test` でテストを実行
- トラブル時: Application Insights のログを確認し、再試行ログやエラーのスタックを確認

変更履歴
- 初期設計書作成
- Durable Functions アーキテクチャに変更、コードをFunctionBase.csに統合
- 日本の法人向け保険営業に合わせてマスキング対象カテゴリを更新
