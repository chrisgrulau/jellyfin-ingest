# Contributing

Issues and pull requests are welcome.

- Target the `main` branch; keep pull requests focused.
- Match the existing style (`.editorconfig`); the build treats warnings as errors.
- Add or update unit tests for any change to parsing, naming or planning — those are pure functions and easy to test.
- Never commit media files, real library paths or credentials.

## Development setup

- .NET 10 SDK
- A Jellyfin 12.1+ test server you don't mind files being moved on — **test against copies, not your real library**.

```bash
dotnet build -c Debug
dotnet test
```

Copy `src/Jellyfin.Plugin.Ingest/bin/Debug/net10.0/Jellyfin.Plugin.Ingest.dll` into
`<jellyfin data>/plugins/Ingest_0.0.0.0/` on the test server and restart Jellyfin.
