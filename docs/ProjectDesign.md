Project Design Document
=======================

1) Overview
- Purpose: Provide an HTTP API (Azure Functions) that detects and masks PII from transcripts (text/voice) using Azure Text Analytics.
- Main function: receive text Å® call Text Analytics PII detection Å® mask detected entities Å® return masked text and entity metadata.

2) Scope
- Core: `src/PiiMaskingFunction` (Azure Functions v4 / .NET 6)
- External: Azure Text Analytics (`Azure.AI.TextAnalytics`)
- Local development: Azure Functions Core Tools (`func`), `local.settings.json`

3) Tech stack
- .NET 6, Azure Functions v4
- Libraries: `Azure.AI.TextAnalytics`, `Azure.Core`, `Microsoft.NET.Sdk.Functions`
- Testing: xUnit

4) Project structure
- `src/PiiMaskingFunction/PiiMaskingFunction.cs` ? HTTP-triggered Function (main flow)
- `src/PiiMaskingFunction/FunctionBase.cs` ? utilities (retry helper)
- `src/PiiMaskingFunction/local.settings.json` ? local environment values (do not commit secrets)
- `tests/` ? unit/integration/e2e tests

5) High-level data flow
- Client Å® POST `/api/maskpii` with JSON `{ "transcript": "..." }` or raw text
- Validate and parse transcript
- Build `TextAnalyticsClient` from env keys
- Call PII recognition via `FunctionBaseRetry.FuncWithRetryAsync` (retries, cancellation aware)
- Mask transcript according to entity offsets
- Return JSON `{ maskedTranscript, entities }`

6) Key design decisions
- Retry strategy: exponential backoff + jitter; transient-only retries; `CancellationToken` aware
- Transient detection: `HttpRequestException`, `Azure.RequestFailedException` with 5xx/429, timeouts
- Asynchronous Analyze Actions: To support very large transcripts the function uses the Text Analytics long-running Analyze Actions API (`StartAnalyzeActionsAsync`) to perform PII recognition in async mode, which supports much larger documents (service async limits). This avoids client-side chunking for most scenarios.
- Merge policy: Detections from the async action are normalized and deduplicated; overlapping detections are merged into coherent, non-overlapping entities. When categories differ, categories may be concatenated (e.g. `Person/Location`).
-- Configurable via env vars: `PII_RETRY_MAX_COUNT`, `PII_RETRY_BASE_DELAY_MS`, `PII_RETRY_MAX_DELAY_MS`, and `PII_MASK_CATEGORIES` (comma-separated list of categories to mask).

Note: The function defaults to masking only highly sensitive identifiers that typically should not be stored in a corporate CRM for B2B insurance sales (credit card numbers, national IDs, bank account numbers, passports, driver licenses, IP addresses, URLs). Contact fields such as `Person`, `PhoneNumber`, `Email`, and `Address` are left unmasked by default to support CRM workflows. Change `PII_MASK_CATEGORIES` to alter behavior.
- Keep masking logic deterministic; mask from end to start to preserve offsets

7) Environment & Configuration
- Required env vars: `TEXT_ANALYTICS_ENDPOINT`, `TEXT_ANALYTICS_KEY`, `AzureWebJobsStorage`
- Optional retry env vars: `PII_RETRY_MAX_COUNT`, `PII_RETRY_BASE_DELAY_MS`, `PII_RETRY_MAX_DELAY_MS`
- Local overrides: `local.settings.json`

8) Logging & Observability
- Log retries as warnings (attempt, delay); final failure as error
- Recommend metrics: retry count, success/failure rate, latency
- Suggest Application Insights for telemetry and alerts

9) Security
- Store keys in Key Vault or environment settings; do not commit `local.settings.json` with secrets
- Do not log raw PII values; mask before any persistent logging

10) Testing
- Unit tests for `MaskTranscript`, retry helpers, and function error paths
- Integration tests call live Text Analytics service (configurable); results are saved as JSONL under `TestResults`
- E2E tests send Japanese inputs to verify masking across PII types
- Performance tests: long-input end-to-end tests (various sizes) are added and write timing/detection metrics to `TestResults/*.jsonl`.
- Concurrent/load tests: parallel workers simulate concurrent requests to exercise throughput and rate-limit behavior; results are saved to `TestResults` as JSONL.

11) CI/CD recommendations
- Use GitHub Actions: restore, build, test, and deploy (Azure/functions-action). Use GitHub Secrets for service keys.
- Store test artifacts (integration results) as build artifacts if desired

12) Future improvements
- Introduce Polly for advanced policies (circuit breaker + retry combined)
- Add rate limiting / batching to reduce API calls and cost
- Improve PII taxonomy mappings per locale and provide configurable mask strategies
- Add more comprehensive monitoring dashboards and alerts for retry spikes

13) Runbook Summary
- To run locally: set `TEXT_ANALYTICS_ENDPOINT` and `TEXT_ANALYTICS_KEY` in env or `local.settings.json` and run `func start` or `dotnet test` for tests
- To troubleshoot: check function logs in Application Insights; inspect retry warnings and failure errors

Change log
- Initial design created and saved.
