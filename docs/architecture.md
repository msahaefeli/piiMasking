# Architecture

This project provides an HTTP-triggered Azure Function that accepts meeting transcripts and returns a masked version where PII is obfuscated. The function uses Azure Text Analytics PII recognition API to identify entities and masks them in-place.

Key architectural points:

- Azure Function (HTTP Trigger): accepts POST requests with `{ "transcript": "..." }` and returns `{ maskedTranscript, entities }`.
- Azure Text Analytics - PII recognition: used to detect PII entities per text chunk.
- Asynchronous Analyze Actions: large transcripts are sent to the Text Analytics long-running Analyze Actions API (`StartAnalyzeActionsAsync`) which supports processing much larger single documents in async mode. This removes the need for client-side chunking in common cases.
- Retry and resilience: calls to Text Analytics are wrapped with a retry utility (`FunctionBaseRetry`) using exponential backoff and transient detection (5xx/429). Retry parameters are configurable via environment variables.
- Merge/aggregation: detections collected per-chunk are converted to absolute offsets, deduplicated and merged into coherent, non-overlapping entities before masking.
- Masking: masking is applied deterministically from end-to-start using the merged offsets to preserve correctness of offsets.
- FunctionBase.cs: contains shared utilities for retry logic and other helpers.

Configuration and observability:

- Environment variables control credentials and behavior (e.g., `TEXT_ANALYTICS_ENDPOINT`, `TEXT_ANALYTICS_KEY`, `PII_CHUNK_MAX_SIZE`, `PII_CHUNK_OVERLAP`, `PII_RETRY_MAX_COUNT`).
- Tests include unit, integration, E2E, performance (long inputs), and concurrent load tests. Integration and performance results are written to `TestResults/*.jsonl` for analysis.

