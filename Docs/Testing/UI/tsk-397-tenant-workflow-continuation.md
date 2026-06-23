# TSK-397 Tenant Workflow Continuation

## Environment

- Web: `https://localhost:6042`
- API: `https://localhost:6041`
- Account: Harper Stone, `tsk397.pass39.202606231342@example.local`
- Portfolio: Harper Stone's Portfolio

## Guide

1. Open `/tenants`, search for a no-match string, and verify the filtered empty state offers `Add tenant`.
2. Create a disposable tenant from the filtered empty state. Verify required first/last name validation, save success, row navigation, detail contact fields, empty leases/documents states, and audit history.
3. Edit the disposable tenant from detail. Verify required last-name validation, save success, and an expandable history diff.
4. Upload a camera-style JPEG to the tenant Documents panel. Download it back through the UI and verify it remains a JPEG image. Cancel document delete, then confirm delete and verify the empty state returns.
5. Delete the disposable tenant. Cancel once, then confirm, and verify the app returns to `/tenants` with real linked tenants intact.
6. On a tenant with an active lease, open list-row Delete and detail-page Delete. Verify both explain the active-lease dependency and disable the destructive action.
7. On the active tenant detail, verify the linked lease row opens the lease detail.
8. Open `Create / Send notice`; verify no-due copy, forced notice creation, delivery-channel controls, local draft review, and dismiss. Do not press Send unless explicitly testing provider delivery.
