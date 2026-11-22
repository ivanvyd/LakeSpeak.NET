# Strict results from a published package

This standalone consumer restores published `LakeSpeak.Genie` 0.5.0 from NuGet. It enables strict
result limits and checks that both query attachments have complete results. Run from the repository
root with the .NET 10 SDK and the .NET 8 runtime installed. Restore needs access to NuGet. The
fixture runs in memory and needs no Databricks workspace or credentials.

```powershell
dotnet restore examples/strict-results-consumer/StrictResultsConsumer.csproj --locked-mode --source https://api.nuget.org/v3/index.json
dotnet run --project examples/strict-results-consumer/StrictResultsConsumer.csproj -c Release -f net8.0 --no-restore
dotnet run --project examples/strict-results-consumer/StrictResultsConsumer.csproj -c Release -f net10.0 --no-restore
```

Each run must print `Strict results fixture PASS`: two query attachments, two complete rows,
four HTTP requests. An unknown request fails inside the fixture; there is no network fallback.
CI overrides `LakeSpeakCandidateVersion` and adds its local feed to exercise a freshly packed
candidate through this same fixture. The default commands above exercise the published package.
