## What and why

## Checklist

- [ ] Not on PRODUCT.md's out-of-scope list
- [ ] Follows the hard conventions in AGENTS.md (Native AOT, source-generated JSON, `room_id` on hot-path queries, read/write data-source split)
- [ ] New endpoints or hub events have matching methods in `Dotwire.Host`, `@dotwire/host`, and `@dotwire/client` when member-facing
- [ ] No plaintext message content in logs, errors, or audit events
- [ ] `dotnet test dotwire.slnx` and `npm test --workspaces` pass
