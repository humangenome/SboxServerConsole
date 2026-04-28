# Contributing

Hey — glad you're here. Whether it's a bug report, a new feature, or a tweak to the dashboard, contributions are welcome.

## Found a bug?

Open an [issue](https://github.com/HumanGenome/SboxServerConsole/issues). Include what happened, what you expected, your SboxServerConsole version, and (if possible) a snippet from the audit log.

## Want to add something?

1. Fork the repo
2. Make your changes on a branch
3. Test against a real s&box dedicated server (the readme covers local setup)
4. Open a PR — describe what it does and why

Keep code style consistent with what's already there. C# uses 4-space indent, file-scoped namespaces, and `Pascal.Case` for public APIs / `_camelCase` for private fields.

## Build / publish

```
dotnet build src/SboxServerConsole.csproj -c Release
dotnet publish src/SboxServerConsole.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -o publish
```

The release pipeline runs the same `dotnet publish` on Windows runners and ships the resulting single-file binary as a GitHub release asset.
