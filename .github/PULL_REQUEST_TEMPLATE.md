## Summary

<!-- What does this change and why? -->

## Related issue

<!-- e.g. Closes #123 -->

## Checklist

- [ ] `dotnet build OsduDelivery.sln -c Release` is warning-clean
- [ ] `dotnet test OsduDelivery.sln` passes
- [ ] `osdu/gui`: `npm run build` and `npm run lint` pass
- [ ] `bash tools/check-vendored-sqlflow.sh` passes
- [ ] Any change to `sqlflow/` is a generic extension point, in a commit of its own that touches nothing else,
      with a subject starting `sqlflow:`
- [ ] Any change to the OSDU model ships with its EF Core migration, designer file and refreshed snapshot
- [ ] Added/updated tests for behavior changes
- [ ] No secrets or real connection strings committed
