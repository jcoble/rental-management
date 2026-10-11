import csv
import importlib.util
import io
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("tsk754_checkpoint.py")
SPEC = importlib.util.spec_from_file_location("tsk754_checkpoint", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


def write_csv(path: Path, fieldnames: list[str], rows: list[dict[str, str]]) -> None:
    with path.open("w", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


class Tsk754CheckpointTests(unittest.TestCase):
    def test_latest_ledger_row_upserts_by_run_id(self) -> None:
        rows = [
            MODULE.LedgerRow(1, "2027-01-02", "RUN-1", "Blocked", "Web", "Scan", "old"),
            MODULE.LedgerRow(2, "2027-01-02", "RUN-1", "Completed", "Web", "Scan", "new"),
        ]

        latest = MODULE.latest_ledger_by_run_id(rows)

        self.assertEqual("Completed", latest["RUN-1"].result)
        self.assertTrue(latest["RUN-1"].is_complete)

    def test_terminal_result_classifier_covers_durable_completion_variants(self) -> None:
        terminal_results = [
            "Completed with existing scan destination-binding defect",
            "Corrected and live verified",
            "Safely blocked after planner correction",
            "Safely blocked after YS-209 correction",
            "Blocked by existing YS-168 planner correction",
            "Blocked by YS-168 planner correction; YS-166 fix committed",
            "Rejected as expected",
        ]
        nonterminal_results = [
            "Pending",
            "Partial",
            "Variance",
            "Failed with data-isolation and lifecycle defects",
            "Blocked at business date",
            "Blocked by YS-113 and YS-114",
            "Web and database completed on next genuine event; physical mobile proof pending",
        ]

        for result in terminal_results:
            self.assertTrue(MODULE.is_terminal_result(result), result)
        for result in nonterminal_results:
            self.assertFalse(MODULE.is_terminal_result(result), result)

    def test_bug_status_classifier_matches_durable_resolution_semantics(self) -> None:
        terminal_statuses = [
            "Fixed",
            "Fixed and live-verified",
            "Fixed; live API and PostgreSQL verified",
            "Fixed locally and live verified; awaiting commit",
            "Fixed locally; awaiting commit",
            "Fixed locally; planner correction recorded",
            "Fixed locally; source contract verified",
            "Planner correction recorded",
            "Retracted",
        ]
        blocking_statuses = [
            "Open",
            "Open intermittent",
            "Reopened",
            "Confirmed; fix in progress",
            "Fix in progress",
            "Fixed pending scan rerun",
            "Fixed locally; live verification pending",
            "Fixed locally and web live verified; mobile pending",
            "Fix committed; live verification pending valid planner data",
            "Planner correction required",
            "Planner prerequisite repaired; run queued",
        ]

        for status in terminal_statuses:
            self.assertTrue(MODULE.is_terminal_bug_status(status), status)
            self.assertFalse(MODULE.is_blocking_bug_status(status), status)
        for status in blocking_statuses:
            self.assertTrue(MODULE.is_blocking_bug_status(status), status)

    def test_unknown_status_and_result_values_are_auditable(self) -> None:
        self.assertTrue(MODULE.is_unknown_result("Needs human review"))
        self.assertTrue(MODULE.is_unknown_bug_status("Deferred to next checkpoint"))
        self.assertFalse(MODULE.is_unknown_result("Completed after live fix"))
        self.assertFalse(MODULE.is_unknown_bug_status("Open"))

    def test_sequential_duplicate_evidence_is_history_when_latest_is_authoritative(self) -> None:
        partial = MODULE.LedgerRow(1, "2027-01-02", "RUN-1", "Partial", "Web", "Scan", "early")
        completed = MODULE.LedgerRow(2, "2027-01-02", "RUN-1", "Completed", "Web", "Scan", "final")

        report = MODULE.analyze_duplicate_groups([partial, completed])

        self.assertIn("RUN-1", report.historical)
        self.assertNotIn("RUN-1", report.blocking)

    def test_exact_duplicate_identity_is_idempotency_blocker(self) -> None:
        first = MODULE.LedgerRow(1, "2027-01-02", "RUN-1", "Completed", "Web", "Scan", "same")
        repeated = MODULE.LedgerRow(2, "2027-01-02", "RUN-1", "Completed", "Web", "Scan", "same")

        report = MODULE.analyze_duplicate_groups([first, repeated])

        self.assertIn("RUN-1", report.exact_duplicate)
        self.assertIn("RUN-1", report.blocking)

    def test_tied_latest_rows_are_unordered_authoritative_blocker(self) -> None:
        first = MODULE.LedgerRow(1, "2027-01-02", "RUN-1", "Partial", "Web", "Scan", "early")
        latest_a = MODULE.LedgerRow(2, "2027-01-02", "RUN-1", "Completed", "Web", "Scan", "final")
        latest_b = MODULE.LedgerRow(2, "2027-01-02", "RUN-1", "Completed", "Mobile", "Scan", "final mobile")

        report = MODULE.analyze_duplicate_groups([first, latest_a, latest_b])

        self.assertIn("RUN-1", report.unordered_authoritative)
        self.assertIn("RUN-1", report.blocking)

    def test_gate_fails_for_incomplete_earlier_run(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            schedule = root / "schedule.csv"
            ledger = root / "ledger.csv"
            bugs = root / "bugs.csv"
            write_csv(
                schedule,
                [
                    "run_id",
                    "simulated_clock",
                    "sequence",
                    "phase",
                    "role",
                    "client",
                    "entry_mode",
                    "workflow",
                    "asset_refs",
                    "financial_refs",
                ],
                [
                    {
                        "run_id": "RUN-1",
                        "simulated_clock": "2027-01-02",
                        "sequence": "1",
                        "phase": "Opening",
                        "role": "Admin",
                        "client": "Web",
                        "entry_mode": "Scan",
                        "workflow": "Lease",
                        "asset_refs": "SCN-1",
                        "financial_refs": "",
                    },
                    {
                        "run_id": "RUN-2",
                        "simulated_clock": "2027-01-03",
                        "sequence": "1",
                        "phase": "Opening",
                        "role": "Admin",
                        "client": "Web",
                        "entry_mode": "Scan",
                        "workflow": "Lease",
                        "asset_refs": "SCN-2",
                        "financial_refs": "",
                    },
                ],
            )
            write_csv(
                ledger,
                ["sim_date", "run_id", "client", "entry_mode", "asset_or_reference", "result", "system_record", "amount", "allocation_or_balance", "evidence"],
                [
                    {
                        "sim_date": "2027-01-03",
                        "run_id": "RUN-2",
                        "client": "Web",
                        "entry_mode": "Scan",
                        "asset_or_reference": "SCN-2",
                        "result": "Completed",
                        "system_record": "",
                        "amount": "",
                        "allocation_or_balance": "",
                        "evidence": "ok",
                    }
                ],
            )
            write_csv(
                bugs,
                ["bug_id", "first_seen_sim_date", "surface", "severity", "status", "summary", "evidence", "financial_impact", "fix_commit"],
                [],
            )

            with redirect_stdout(io.StringIO()):
                code = MODULE.main(
                    [
                        "gate",
                        "--schedule",
                        str(schedule),
                        "--ledger",
                        str(ledger),
                        "--bug-ledger",
                        str(bugs),
                        "--advance-to",
                        "2027-01-04",
                    ]
                )

            self.assertEqual(1, code)

    def test_gate_scopes_duplicate_conflicts_before_advance_date(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            schedule = root / "schedule.csv"
            ledger = root / "ledger.csv"
            bugs = root / "bugs.csv"
            write_csv(
                schedule,
                [
                    "run_id",
                    "simulated_clock",
                    "sequence",
                    "phase",
                    "role",
                    "client",
                    "entry_mode",
                    "workflow",
                    "asset_refs",
                    "financial_refs",
                ],
                [
                    {
                        "run_id": "RUN-1",
                        "simulated_clock": "2027-01-02",
                        "sequence": "1",
                        "phase": "Opening",
                        "role": "Admin",
                        "client": "Web",
                        "entry_mode": "Scan",
                        "workflow": "Lease",
                        "asset_refs": "",
                        "financial_refs": "",
                    },
                    {
                        "run_id": "RUN-2",
                        "simulated_clock": "2027-01-05",
                        "sequence": "1",
                        "phase": "Opening",
                        "role": "Admin",
                        "client": "Web",
                        "entry_mode": "Scan",
                        "workflow": "Lease",
                        "asset_refs": "",
                        "financial_refs": "",
                    },
                ],
            )
            write_csv(
                ledger,
                ["sim_date", "run_id", "client", "entry_mode", "asset_or_reference", "result", "system_record", "amount", "allocation_or_balance", "evidence"],
                [
                    {
                        "sim_date": "2027-01-02",
                        "run_id": "RUN-1",
                        "client": "Web",
                        "entry_mode": "Scan",
                        "asset_or_reference": "",
                        "result": "Completed",
                        "system_record": "",
                        "amount": "",
                        "allocation_or_balance": "",
                        "evidence": "ok",
                    },
                    {
                        "sim_date": "2027-01-05",
                        "run_id": "RUN-2",
                        "client": "Web",
                        "entry_mode": "Scan",
                        "asset_or_reference": "",
                        "result": "Completed",
                        "system_record": "",
                        "amount": "",
                        "allocation_or_balance": "",
                        "evidence": "old",
                    },
                    {
                        "sim_date": "2027-01-05",
                        "run_id": "RUN-2",
                        "client": "Mobile",
                        "entry_mode": "Scan",
                        "asset_or_reference": "",
                        "result": "Completed",
                        "system_record": "",
                        "amount": "",
                        "allocation_or_balance": "",
                        "evidence": "new",
                    },
                ],
            )
            write_csv(
                bugs,
                ["bug_id", "first_seen_sim_date", "surface", "severity", "status", "summary", "evidence", "financial_impact", "fix_commit"],
                [],
            )

            with redirect_stdout(io.StringIO()):
                code = MODULE.main(
                    [
                        "gate",
                        "--schedule",
                        str(schedule),
                        "--ledger",
                        str(ledger),
                        "--bug-ledger",
                        str(bugs),
                        "--advance-to",
                        "2027-01-04",
                    ]
                )

            self.assertEqual(0, code)

    def test_corpus_status_reconciles_manifest_and_index(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = root / "scan-assets.csv"
            index = root / "corpus-index.csv"
            write_csv(manifest, ["asset_id"], [{"asset_id": "SCN-1"}, {"asset_id": "SCN-2"}])
            write_csv(index, ["asset_id", "status"], [{"asset_id": "SCN-1", "status": "OK"}, {"asset_id": "SCN-2", "status": "OK"}])

            ok, detail = MODULE.corpus_status(manifest, index)

            self.assertTrue(ok)
            self.assertIn("missing=0", detail)

    def test_opening_control_sql_is_plan_anchored_and_reports_seed_pollution(self) -> None:
        sql = MODULE.build_opening_control_sql(
            portfolio_id=2,
            as_of="2027-01-15",
            portfolio_rows=[
                {
                    "property_id": "P001",
                    "property_name": "O'Hara House",
                    "address": "117 Arbor Street",
                    "city": "Columbus",
                    "state": "OH",
                    "postal_code": "43201",
                }
            ],
            unit_rows=[
                {
                    "unit_id": "U001",
                    "property_id": "P001",
                    "label": "Main",
                }
            ],
            lease_rows=[
                {
                    "lease_id": "L001",
                    "unit_id": "U001",
                    "tenant_email": "tenant.001@example.local",
                    "start_date": "2026-02-01",
                    "end_date": "2027-12-31",
                    "monthly_rent_cents": "157500",
                    "deposit_cents": "157500",
                    "rent_due_day": "1",
                    "origin": "Opening lease scan",
                }
            ],
            scan_rows=[
                {
                    "asset_id": "SCN-0001",
                    "filename": "scn-0001-opening-lease-l001.pdf",
                    "intended_target": "Lease",
                    "lease_id": "L001",
                }
            ],
        )

        self.assertEqual(1, sql.count("SELECT jsonb_build_object("))
        self.assertIn("WITH\nparams AS", sql)
        self.assertIn("'O''Hara House'", sql)
        self.assertIn("matchedPlanProperties", sql)
        self.assertIn("extraActiveProperties", sql)
        self.assertIn("extraActivePropertySample", sql)
        self.assertIn("missingPlanLeases", sql)
        self.assertIn("missingOpeningAssets", sql)
        self.assertIn("futureCreatedExtraProperties", sql)
        self.assertIn("genericOpeningFallbackAssets", sql)
        self.assertIn("ambiguousGenericOpeningAssets", sql)
        self.assertIn("LEFT JOIN matched_properties", sql)
        self.assertIn("named_opening_attachments AS", sql)
        self.assertIn('"ScanDrafts" draft', sql)
        self.assertIn('sf."Id" = draft."SourceStoredFileId"', sql)
        self.assertIn('sf."EntityType" = \'LeaseAgreement\'', sql)
        self.assertIn('sf."EntityId" = mol.agreement_id', sql)
        self.assertIn('"DeletedAt" IS NULL', sql)
        self.assertIn('lower(coalesce(sf."FileName", \'\')) LIKE \'%\' || lower(poa.asset_id) || \'%\'', sql)
        self.assertIn('lower(coalesce(sf."FilePath", \'\')) LIKE \'%\' || lower(poa.asset_id) || \'%\'', sql)
        self.assertIn('WHERE lower(coalesce(sf."FileName", \'\')) ~', sql)
        self.assertIn("^[0-9]+\\.(jpg|jpeg)$", sql)
        self.assertIn("WHERE candidate_count = 1", sql)
        self.assertNotIn('SourceContentSha256" = poa.source_content_sha256', sql)
        self.assertIn('p."CreatedAt" < (params.as_of + INTERVAL \'1 day\')', sql)
        self.assertNotIn('la."TermStartOn" <= params.as_of', sql)
        self.assertNotIn('lmp."EffectiveFrom" <= params.as_of', sql)

    def test_opening_control_sql_checks_duplicate_opening_attachments_db_side(self) -> None:
        sql = MODULE.build_opening_control_sql(
            portfolio_id=2,
            as_of="2027-01-15",
            portfolio_rows=[],
            unit_rows=[],
            lease_rows=[],
            scan_rows=[
                {
                    "asset_id": "SCN-0013",
                    "filename": "scn-0013-opening-lease-l013.pdf",
                    "intended_target": "Lease",
                    "lease_id": "L013",
                },
                {
                    "asset_id": "SCN-0935",
                    "filename": "scn-0935-move-out-notice-l005.jpg",
                    "intended_target": "Notice / Lease document",
                    "lease_id": "L005",
                },
            ],
        )

        self.assertIn("duplicate_opening_assets AS", sql)
        self.assertIn("GROUP BY asset_id, filename", sql)
        self.assertIn("HAVING COUNT(*) > 1", sql)
        self.assertIn('COUNT(DISTINCT scan_draft_id) FILTER (WHERE scan_draft_id IS NOT NULL)::int AS confirmed_draft_count', sql)
        self.assertIn("COUNT(DISTINCT stored_file_id)::int AS active_attachment_count", sql)
        self.assertIn("COUNT(*)::int AS candidate_count", sql)
        self.assertIn("duplicateOpeningAssets", sql)
        self.assertIn("missing_opening_assets AS", sql)
        self.assertIn("'matchedOpeningAttachments', (SELECT COUNT(*) FROM opening_attachments)", sql)
        self.assertIn("'SCN-0013'", sql)
        self.assertNotIn("'SCN-0935'", sql)

    def test_opening_control_sql_exposes_generic_fallback_and_ambiguity_db_side(self) -> None:
        sql = MODULE.build_opening_control_sql(
            portfolio_id=2,
            as_of="2027-01-15",
            portfolio_rows=[],
            unit_rows=[],
            lease_rows=[],
            scan_rows=[
                {
                    "asset_id": "SCN-0016",
                    "filename": "scn-0016-opening-lease-l016.jpg",
                    "intended_target": "Lease",
                    "lease_id": "L016",
                }
            ],
        )

        self.assertIn("generic_scan_candidates AS", sql)
        self.assertIn("COUNT(*) OVER (PARTITION BY poa.asset_id) AS candidate_count", sql)
        self.assertIn("generic_opening_attachments AS", sql)
        self.assertIn("'generic-confirmed-draft'::text AS match_method", sql)
        self.assertIn("ambiguous_generic_opening_assets AS", sql)
        self.assertIn("WHERE candidate_count > 1", sql)
        self.assertIn("genericOpeningFallbackAssets", sql)
        self.assertIn("ambiguousGenericOpeningAssets", sql)
        self.assertIn("'SCN-0016'", sql)


if __name__ == "__main__":
    unittest.main()
