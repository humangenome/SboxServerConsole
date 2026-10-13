# Contributing

Bug reports, fixes and new features are welcome.

## Found a bug?

Open an [issue](https://github.com/HumanGenome/SboxServerConsole/issues). Include what happened, what you expected, your SboxServerConsole version, and (if possible) a snippet from the audit log.

## Want to add something?

1. Fork the repo
2. Make your changes on a branch
3. Run the tests (`dotnet test tests/SboxServerConsole.Tests/SboxServerConsole.Tests.csproj --settings tests/SboxServerConsole.Tests/test.runsettings`) and, for anything that touches the server process, try it against a real s&box dedicated server (the README covers setup)
4. Open a PR that says what it changes and why

Match the existing style: 4-space indent, file-scoped namespaces, `PascalCase` for public members and `_camelCase` for private fields. If you change an HTTP route or a setting, update `docs/api.md` too; the tests compare the two.

## Build / publish

```
dotnet build src/SboxServerConsole.csproj -c Release

# Windows binary
dotnet publish src/SboxServerConsole.csproj -c Release -r win-x64 \
  --self-contained -p:PublishSingleFile=true -o publish/win-x64

# Linux binary
dotnet publish src/SboxServerConsole.csproj -c Release -r linux-x64 \
  --self-contained -p:PublishSingleFile=true -o publish/linux-x64
```

Both publishes work cross-platform: a Linux dev box can produce the Windows binary, and vice versa. The release pipeline runs both publishes on a Linux runner and ships the resulting single-file binaries plus zip/tarball bundles as GitHub release assets.
