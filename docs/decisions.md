# Decisions

- Use Azure Text Analytics PII recognition to detect PII entities.
- Mask detected entities by replacing characters with `*` to preserve offsets/lengths.
- Keep shared utilities in `FunctionBase.cs` and instantiate it inside the function to reuse common logic.
- Project targets .NET 6 and Azure Functions v4.
