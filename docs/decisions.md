# 決定事項

- PII 検出には Azure Text Analytics の PII 認識機能を利用する。
- 検出されたエンティティは `*` で置換してマスクし、元のオフセットや長さを保つ。
- 共有ユーティリティは `FunctionBase.cs` にまとめ、関数から再利用することで重複を避ける。
- プロジェクトは .NET 6 と Azure Functions v4 を対象とする。
