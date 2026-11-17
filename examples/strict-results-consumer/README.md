# Strict candidate package consumer

This standalone consumer references a locally packed candidate, with no source project reference
and no default published version. The existing `package-quickstart` remains pinned to 0.4.0.
Run from the repository root with the installed .NET 10 SDK. These commands pack, restore and run
an offline HTTP fixture; they never publish a package or contact Databricks.

```powershell
dotnet pack src/LakeSpeak.Genie/LakeSpeak.Genie.csproj -c Release -o artifacts/strict-results-pack -p:Version=0.4.1-strict.local
$candidateFeed = (Resolve-Path artifacts/strict-results-pack).Path
dotnet restore examples/strict-results-consumer/StrictResultsConsumer.csproj -p:LakeSpeakCandidateVersion=0.4.1-strict.local "-p:RestoreAdditionalProjectSources=$candidateFeed"
dotnet run --project examples/strict-results-consumer/StrictResultsConsumer.csproj -c Release -f net8.0 -p:LakeSpeakCandidateVersion=0.4.1-strict.local --no-restore
dotnet run --project examples/strict-results-consumer/StrictResultsConsumer.csproj -c Release -f net10.0 -p:LakeSpeakCandidateVersion=0.4.1-strict.local --no-restore
```

Each run must print `Strict candidate fixture PASS`: two query attachments, two complete rows,
four HTTP requests. An unknown request fails inside the fixture; there is no network fallback.
Use a fresh candidate version for a changed package to avoid NuGet's immutable-version cache.
This proves package API consumption and offline integration, not live upstream compatibility.
