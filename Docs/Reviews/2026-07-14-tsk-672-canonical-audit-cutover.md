# TSK-672 canonical audit cutover

Rental Command now has one audit store and one writer: `AtomicAuditLogs`, materialized by the
atomic command kernel in the same transaction as business state, receipt, and outbox intent.
The legacy `AuditLog` entity/table, `AuditSaveChangesInterceptor`, request `AuditScope`, runtime
registrations, permissions, schema, and reader queries were removed destructively. There is no
alias, fallback, dual write, or data bridge.

`AuditTrailService` is now only a semantic-event adapter over `IAtomicAuditEventSink`. The sink
fails closed when no atomic attempt is active. This is deliberate: letting these calls silently
write a standalone audit row would recreate the exact split-commit path TSK-672 removes.

## Unconverted mutations blocked by the fail-closed boundary

The following 16 semantic audit call sites (covering 21 named mutation operations because
private helpers serve multiple entry points) must be moved into receipt-backed
`IAtomicUnitOfWork` commands. Until then, the business mutation reaches the audit boundary and is
rejected instead of falling back to a second writer.

- `UnitService`: `CreateAsync`, `UpdateAsync`, `DeleteAsync`
- `ApplicationService`: `CreateFromScanAsync`, `UpdateFromQueryAsync`,
  `ApproveFromQueryAsync`, `DeclineFromQueryAsync`, `DeleteFromQueryAsync`
- `ListingWorkspaceService`: `GenerateAsync`, `SaveAsync`, `AttachPhotoAsync`;
  `UpdatePhotoAsync`, `RemovePhotoAsync`, and `ReorderPhotosAsync` through
  `MutatePhotoPackageAsync`; `PrepareConnectedAsync`, `PublishConnectedAsync`,
  `UpdateConnectedAsync`, and `UnpublishConnectedAsync` through the guarded Connected helpers;
  and `ConfirmSignalAsync`
- `AuthService`: password-change mutation through `LogPasswordChangeAuditAsync`
- `ScanService`: `RejectDraftAsync`

For each conversion, bind or stage its semantic audit inside the handler before the kernel's final
flush. Do not add a direct `AtomicAuditLogs` insert, a transaction-local alternate writer, or a
temporary compatibility interceptor. Once the last route is converted, set
`allowUnconvertedWrites: false` in both API and Engine as the final TSK-672 enforcement gate.
