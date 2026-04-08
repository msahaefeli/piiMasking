プロジェクト設計書
=================

1) 概要
- 目的: Azure Text Analytics を用いてトランスクリプト（音声またはテキスト）から PII を検出・マスクし、HTTP API として提供する。
- 主なフロー: テキスト受信 → Text Analytics による PII 検出 → 検出結果に基づくマスキング → マスク済みテキストとエンティティ情報を返却。

2) 範囲
- コア: `src/PiiMaskingFunction`（Azure Functions v4 / .NET 6）
- 外部連携: Azure Text Analytics (`Azure.AI.TextAnalytics`)
- ローカル開発: Azure Functions Core Tools、`local.settings.json` による環境変数上書き

3) 技術スタック
- プラットフォーム: .NET 6, Azure Functions v4
- ライブラリ: `Azure.AI.TextAnalytics`, `Azure.Core`, `Microsoft.NET.Sdk.Functions`
- テスト: xUnit

4) プロジェクト構成（主要）
- `src/PiiMaskingFunction/PiiMaskingFunction.cs` ? HTTP トリガー、主要処理
- `src/PiiMaskingFunction/FunctionBase.cs` ? リトライなどの共通ユーティリティ
- `src/PiiMaskingFunction/local.settings.json` ? ローカル用環境変数（機密はコミットしない）
- `tests/` ? 単体・統合・E2E テスト

5) 高レベルデータフロー
- クライアント → POST `/api/maskpii`（JSON `{ "transcript": "..." }` または生テキスト）
- リクエスト検証・パース
- 環境変数から TextAnalyticsClient を構築
- `FunctionBaseRetry.FuncWithRetryAsync` 経由で PII 検出（リトライ、キャンセル対応）
- 検出エンティティのオフセットに基づきマスキング（末尾から処理）
- `{ maskedTranscript, entities }` を返却

6) 主要設計方針
- リトライ戦略: 指数バックオフ + ジッター、トランジェント例外のみ再試行、`CancellationToken` 対応
- トランジェント判定例: `HttpRequestException`, `RequestFailedException`（5xx/429）, タイムアウト
- 非同期 Analyze Actions: 大きなトランスクリプトに対応するため、Text Analytics の長時間実行（Analyze Actions）API を使用して PII 検出を非同期で実行します。これによりクライアント側でのチャンク分割を原則不要にしています（サービス側の非同期上限に依存）。
- マージポリシー: 非同期 API から得られた検出結果を正規化・重複除去して、重複や重なりを解消したエンティティに統合します。カテゴリが異なる場合は `カテゴリA/カテゴリB` のように連結することがあります。
- 設定可能項目（環境変数）: `PII_RETRY_MAX_COUNT`, `PII_RETRY_BASE_DELAY_MS`, `PII_RETRY_MAX_DELAY_MS`
- マスキングはオフセットを保持するため末尾から実行

7) 環境・設定
- 必須: `TEXT_ANALYTICS_ENDPOINT`, `TEXT_ANALYTICS_KEY`, `AzureWebJobsStorage`（Functions 実行に必要）
- 任意（リトライ設定）: `PII_RETRY_MAX_COUNT`, `PII_RETRY_BASE_DELAY_MS`, `PII_RETRY_MAX_DELAY_MS`
- ローカル: `local.settings.json` で上書き可能（ただし機密は管理に注意）

8) ロギング & 可観測性
- 再試行時は `LogWarning`、最終失敗は `LogError` を出力
- 監視推奨: 再試行回数、成功率、レイテンシ
- 推奨ツール: Application Insights（アラート・ダッシュボード）

9) セキュリティ
- シークレットは Key Vault やアプリ設定で管理。`local.settings.json` は共有・コミット禁止
- ログに PII を出力しない（必要ならマスクしてから記録）

10) テスト
- 単体: `MaskTranscript`、リトライヘルパ、エラーパス等
- 統合: 実サービス（Text Analytics）を呼ぶテスト（環境変数 or local.settings.json 必須）。結果を JSONL で保存
- E2E: 日本語サンプルで PII 検出・マスキングを検証
- パフォーマンステスト: 長い入力（2k?20k 文字など）でエンドツーエンド性能を測定し、検出数・処理時間を `TestResults/*.jsonl` に保存します。
- 並列/負荷テスト: 複数のワーカーで同時リクエストを生成してスループットと安定性を検証し、結果を `TestResults` に保存します。

11) CI/CD（推奨）
- GitHub Actions のワークフロー: restore → build → test → deploy（Azure Functions へ）。シークレットは GitHub Secrets で管理
- テスト結果や統合ログはアーティファクトとして保存可能

12) 将来の改善案
- `Polly` 導入による複合ポリシー（リトライ＋サーキットブレーカー）
- API 呼び出しのバッチ化やレート制御でコスト削減
- ローカライズ別の PII マッピング最適化
- テスト結果の履歴比較／ダッシュボード化

13) Runbook（要約）
- ローカル実行: `TEXT_ANALYTICS_ENDPOINT` と `TEXT_ANALYTICS_KEY` を設定し `func start` または `dotnet test` でテストを実行
- トラブル時: Application Insights のログを確認し、再試行ログやエラーのスタックを確認

変更履歴
- 初期設計書作成
