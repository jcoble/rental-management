---
title: Creating and Managing Leases
category: Tenants & Leases
slug: leases
order: 2
summary: Create a lease, read the account history (ledger), and establish a carried-over opening balance during move-in.
keywords: lease, create lease, rent terms, ledger, account history, balance, opening balance, security deposit, late fee, rent due day, lease status
---

A tenancy in Rental Command has three connected parts:

- **Lease management** is the continuing household relationship with the unit.
- **Lease agreements** are the immutable legal documents and signed versions for that relationship.
- **Tenant account** is the continuing financial ledger for charges, receipts, credits, and the security deposit.

A renewal or signed correction creates another agreement version without replacing the household's account history. Payments and deposits belong to the tenant account, while notices can reference the relationship, the governing agreement, or a specific ledger event depending on why they were created.

## Create a lease

1. Open **Leases** and click **New Lease**.
2. Pick the **property**, then the **unit**, then the **tenant**.
3. Set the **start** and **end** dates.
4. Enter **monthly rent**, **security deposit**, **late fee**, and the **rent due day** (the day of the month rent is due).
5. Choose a **status** (usually Draft to start, then Active).
6. Click **Save Lease**.

A relationship number and draft agreement are generated for you. You can also prepare them by **scanning a lease agreement** — see *Lease agreement and signing*.

## Agreement and tenancy status

Draft agreements can be prepared while another signed agreement still governs the tenancy. The governing agreement is derived from its signed dates and supersession history, so it cannot stay falsely active because a scheduled status update failed. Operational tenancy state—such as notice given, move-out underway, or ended—belongs to lease management and remains editable with an audit history.

## Account history (the ledger)

Open the tenancy's **Account History** — a plain-English tenant-account ledger of every charge, receipt, credit, and correction with a running balance. Each line explains *why* it's there. At the top you'll see a one-sentence summary, for example "Jane still owes $1,200" or "Paid ahead by $300 (credit on the account)."

## Opening balance

If a tenant already owed you money (or had a credit) **before** you started using Rental Command, establish an **opening balance** as an explicit tenant-account fact while preparing move-in:

1. Enter the signed amount, the "as of" date, and an optional note in Prepare move-in.
2. Complete move-in. Rental Command posts one immutable **Opening balance** entry to the tenant account ledger, so the running balance is correct from day one.

The opening balance is part of account history, not a separate editable lease record. If it was entered incorrectly, post an explicit account adjustment so both the original entry and the correction remain visible.
