# Contributing to dotwire

Issues, bug reports, and pull requests are welcome. dotwire is early, so feedback from real integration attempts is the most useful contribution of all.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md). Report security issues privately as described in [SECURITY.md](SECURITY.md), not in public issues.

## Before you start

* Read **[AGENTS.md](AGENTS.md)**. Its hard conventions are binding: Native AOT only, source-generated JSON, shard-key discipline, the read/write data-source split, and an append-only audit table.
* Check **[PRODUCT.md](PRODUCT.md)**'s out-of-scope list before proposing a feature. It's a contract, not a backlog.
* Every endpoint or hub event needs a matching method in both host SDKs (`Dotwire.Host`, `@dotwire/host`), and in `@dotwire/client` when it's member-facing.
* Never log or persist plaintext message content, including in debug logs and error messages.

For anything larger than a bug fix, open an issue first so the approach can be agreed before you write the code.

## Building & testing locally

Requires the **.NET 10 SDK** `10.0.403` or a later `10.0` feature band (`global.json` pins `10.0.403` with `rollForward: latestFeature`; older SDKs ship a vulnerable Native AOT compiler):

```bash
# Build the solution
dotnet build dotwire.slnx

# Run the test suite (Compose integration tests use Postgres and NATS when available)
dotnet test dotwire.slnx

# Publish standalone Native AOT binary
dotnet publish dotwire/dotwire.csproj -c Release
```

The TypeScript SDKs live in the `packages/` npm workspace:

```bash
cd packages
npm ci
npm run build --workspaces
npm test --workspaces
```

## Pull requests

* Keep each pull request to one change, with tests for new behavior.
* Run the .NET and npm test suites above before opening it.
* Fill in the pull request template checklist.
