# Inbound messaging safety review follow-up

## Verified SMS mutation boundary

Inbound SMS has one production mutation path: a verified provider event may complete exactly one
open vendor dispatch. The obsolete tenant rent-confirmation service, interface, registration, and
router fallback were deleted. A non-DONE body is acknowledged without entering a mutation command.
An unmatched or ambiguous DONE enters the atomic command, commits its provider-event command receipt
with a `NoOpenDispatch` result, and changes no dispatch, work order, vendor, payment, notification, or
outbox row. Non-DONE and sender-without-phone events use the same atomic receipt/no-op path without
running the dispatch match query.

Vendor matching is one SQL query over `VendorDispatches -> Vendor -> WorkOrder`, ordered for stable
diagnostics and limited to two rows. Zero rows means no match; two rows proves ambiguity and fails
closed even when both candidates belong to the same portfolio or vendor. Phone identity is not
portfolio-qualified, so uniqueness is evaluated system-wide.

`Vendor.JobsCompleted` advances only inside the first real `WorkOrder.Status != Completed` transition.
A replay is handled by the atomic receipt. A work-order aggregate lock serializes sibling dispatches
even when they arrive from different phones; a later sibling may close without receiving duplicate
scorecard credit, appending a second status event, or sending a second completion notification.

## Recipient joins

Legacy Identity roles do not choose recipients. Both notification paths use one DB-side query through:

`ApplicationUser -> WorkspaceAccessContext -> WorkspaceMembership -> MembershipRoleAssignment -> RoleProfileCapability -> CapabilityDefinition`

The access context, membership, and assignment must all be effective at the event time. Capability and
scope must be satisfied by the same assignment.

- Vendor completion requires `work.read` and either `AllProperties` or a `SelectedProperties` row for
  the exact `WorkOrder.PropertyId`.
- Tenant messages require `rentals.read` plus an effective issued `Active`/`NoticeGiven` lease linking
  the conversation tenant (primary `Lease.TenantId` or `LeaseTenant`) to a property allowed by that
  same assignment. Deleted, future, moved-out, draft, pending-signature, void, expired, and terminated
  leases do not establish visibility.
- Owner relationship accounts are not team-role recipients. Maintenance technician assigned-work
  notification is deliberately fail-closed because the current schema has no work-order responsibility
  row yet. If no authorized recipient is proved by these joins, no recipient notification is staged;
  the source message/completion, required audit, outbox intent, and atomic receipt still commit.

PostgreSQL and API tests include unrelated same-portfolio scoped users and legacy-role decoys to prove
they receive nothing.
