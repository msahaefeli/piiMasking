# Architecture
# PII Masking Function - Architecture

This project provides an HTTP-triggered Azure Function using Durable Functions that accepts meeting transcripts and returns a masked version where PII is obfuscated. The function uses Azure AI Language (Text Analytics) PII recognition API to identify entities and masks them in-place.

## Key Architectural Points

### Durable Functions Architecture

```
Client
    Ñ†
    Ñ•ÑüPOSTÅ® /api/maskpii ÑüÑüÑüÑüÑüÑüÑüÑüÑüÑüÑü? PiiMasking_HttpStart
    Ñ†                                        Ñ†
    Ñ†                                        Å•
    Ñ†                               PiiMasking_Orchestrator
    Ñ†                                        Ñ†
    Ñ†                                        Å•
    Ñ†                               PiiMasking_RunAnalyze
    Ñ†                                        Ñ† (Azure AI Language API call)
    Ñ†                                        Ñ† (with retry)
    Ñ†                                        Å•
    Ñ†                                 [Result stored]
    Ñ†
    Ñ§ÑüGETÅ® /api/status/{instanceId} ?ÑüÑüÑü PiiMasking_Status
                                              (Poll for status/result)
```

### Components

- **PiiMasking_HttpStart**: HTTP trigger that starts the orchestrator and returns polling URLs
- **PiiMasking_Orchestrator**: Coordinates the PII detection and masking workflow
- **PiiMasking_RunAnalyze**: Activity that calls Azure AI Language API with retry support
- **PiiMasking_Status**: HTTP trigger to check orchestration status and retrieve results

### Core Files

- `PiiMaskingDurable.cs`: Azure Functions entry points (HTTP triggers, orchestrator, activity)
- `FunctionBase.cs`: Shared services including:
  - `PiiMaskingService`: Common business logic (PII detection, masking, credit card detection)
  - `FunctionBaseRetry`: Retry utility with exponential backoff
  - `PiiEntity` / `MaskingResult`: Data models

### Key Features

- **Asynchronous Processing**: Uses Durable Functions async pattern for long-running operations
- **Retry and Resilience**: Calls to Azure AI Language are wrapped with retry utility using exponential backoff and transient detection (5xx/429)
- **Merge/Aggregation**: Detections are deduplicated and merged into coherent, non-overlapping entities
- **Masking**: Applied deterministically from end-to-start to preserve offset correctness
- **Credit Card Detection**: Additional heuristic using Luhn algorithm

## Default Masking Categories (Japan B2B Insurance Sales)

This function is configured for Japanese corporate CRM workflows. Contact fields needed for sales activities are preserved.

### Masked by Default

| Category | Description |
|----------|-------------|
| `CreditCardNumber` | Credit card numbers |
| `BankAccountNumber` | Bank account numbers |
| `JPBankAccountNumber` | Japanese bank account numbers |
| `SWIFTCode` | SWIFT codes |
| `JPMyNumberPersonal` | My Number (Individual) |
| `JPMyNumberCorporate` | Corporate Number |
| `JPResidenceCardNumber` | Residence card numbers |
| `JPDriversLicenseNumber` | Japanese driver's license |
| `JPPassportNumber` | Japanese passport numbers |
| `JPSocialInsuranceNumber` | Social insurance numbers |
| `JPHealthInsuranceNumber` | Health insurance card numbers |
| `PassportNumber` | Passport numbers (general) |
| `DriverLicenseNumber` | Driver's license (general) |
| `IPAddress` | IP addresses |
| `URL` / `Url` | URLs |

### Preserved for CRM

- `Person` (names)
- `PhoneNumber` (phone numbers)
- `Email` (email addresses)
- `Address` (addresses)
- `Organization` (organization names)

## Configuration

Environment variables control credentials and behavior:

| Variable | Description |
|----------|-------------|
| `TEXT_ANALYTICS_ENDPOINT` | Azure AI Language endpoint (required) |
| `TEXT_ANALYTICS_KEY` | Azure AI Language API key (required) |
| `AzureWebJobsStorage` | Storage connection for Durable Functions (required) |
| `PII_MASK_CATEGORIES` | Comma-separated list of categories to mask (optional, overrides defaults) |
| `PII_RETRY_MAX_COUNT` | Maximum retry attempts (default: 3) |
| `PII_RETRY_BASE_DELAY_MS` | Base delay for retries in ms (default: 1000) |
| `PII_RETRY_MAX_DELAY_MS` | Maximum delay for retries in ms (default: 30000) |

## Testing

- **Unit tests**: `MaskTranscriptFromOffsets`, retry helpers, error paths
- **Integration tests**: Call actual Azure AI Language service (requires credentials)
- **E2E tests**: Japanese sample inputs for PII detection and masking
- **Performance tests**: Long inputs (2k-20k characters), results saved to `TestResults/*.jsonl`

