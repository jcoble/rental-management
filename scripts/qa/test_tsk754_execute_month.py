import csv
import importlib.util
import io
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import patch


MODULE_PATH = Path(__file__).with_name("tsk754_execute_month.py")
SPEC = importlib.util.spec_from_file_location("tsk754_execute_month", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


def write_csv(path: Path, fieldnames: list[str], rows: list[dict[str, str]]) -> None:
    with path.open("w", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


SCHEDULE_FIELDS = [
    "run_id",
    "simulated_clock",
    "sequence",
    "phase",
    "role",
    "client",
    "entry_mode",
    "workflow",
    "entity_refs",
    "asset_refs",
    "financial_refs",
    "exact_actions",
    "expected_result",
    "evidence_required",
    "negative_or_retry_check",
    "completion_gate",
]


ORACLE_FIELDS = [
    "event_id",
    "entry_date",
    "effective_date",
    "event_type",
    "category",
    "property_id",
    "unit_id",
    "lease_id",
    "counterparty",
    "source_mode",
    "source_asset_id",
    "reference",
    "amount_cents",
    "ar_delta_cents",
    "operating_cash_delta_cents",
    "deposit_cash_delta_cents",
    "reserve_cash_delta_cents",
    "deposit_liability_delta_cents",
    "loan_liability_delta_cents",
    "income_cents",
    "expense_cents",
    "owner_equity_delta_cents",
    "owner_distribution_cents",
    "capital_asset_delta_cents",
    "accumulated_depreciation_delta_cents",
    "memo",
]


SCAN_FIELDS = [
    "asset_id",
    "planned_date",
    "filename",
    "document_family",
    "intended_target",
    "intake_expectation",
    "format",
    "pages_or_images",
    "capture_profile",
    "property_id",
    "unit_id",
    "lease_id",
    "financial_event_id",
    "expected_fields",
    "edge_case",
    "paired_manual_action",
    "confidentiality",
]


class Tsk754ExecuteMonthTests(unittest.TestCase):
    def test_schedule_slicing_filters_month_lane_and_sorts_by_day_sequence(self) -> None:
        rows = [
            self.row("RUN-3", "2027-02-01", "2", "Monthly operations", "Web"),
            self.row("RUN-1", "2027-01-02", "2", "Monthly operations", "Web"),
            self.row("RUN-2", "2027-01-02", "1", "Opening setup", "Web"),
            self.row("RUN-4", "2027-01-03", "1", "Monthly operations", "Mobile"),
        ]

        sliced = MODULE.slice_schedule(rows, "2027-01", "Monthly operations")

        self.assertEqual(["RUN-1", "RUN-4"], [row.run_id for row in sliced])

    def test_schedule_slicing_can_select_exact_date_and_run_ids(self) -> None:
        rows = [
            self.row("RUN-1", "2027-02-03", "1", "Monthly operations", "Web"),
            self.row("RUN-2", "2027-02-03", "2", "Monthly operations", "Web"),
            self.row("RUN-3", "2027-02-04", "1", "Monthly operations", "Web"),
        ]

        sliced = MODULE.slice_schedule(
            rows,
            "2027-02",
            simulated_date="2027-02-03",
            run_ids={"RUN-2"},
        )

        self.assertEqual(["RUN-2"], [row.run_id for row in sliced])

    def test_stable_idempotency_keys_are_deterministic_and_distinct(self) -> None:
        first = MODULE.stable_key("RUN-20270103-02", "tenant-receipt", "FIN-00007")
        second = MODULE.stable_key("RUN-20270103-02", "tenant-receipt", "FIN-00007")
        different = MODULE.stable_key("RUN-20270103-02", "tenant-receipt", "FIN-00008")

        self.assertEqual(first, second)
        self.assertNotEqual(first, different)
        self.assertLessEqual(len(first), 128)
        self.assertTrue(first.startswith("tsk754:RUN-20270103-02:tenant-receipt:FIN-00007:"))

    def test_dry_run_does_not_require_token_or_execute_http(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            schedule, oracle, journal, control, scans = self.write_fixture_files(root)

            with redirect_stdout(io.StringIO()):
                code = MODULE.main(
                    [
                        "--schedule",
                        str(schedule),
                        "--financial-oracle",
                        str(oracle),
                        "--journal",
                        str(journal),
                        "--control-totals",
                        str(control),
                        "--scan-assets",
                        str(scans),
                        "--month",
                        "2027-01",
                        "--lane",
                        "System automation + reconciliation",
                    ]
                )

            self.assertEqual(0, code)

    def test_unsupported_mapping_fails_closed_without_required_id_map(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            schedule, oracle, journal, control, scans = self.write_fixture_files(root)
            rows = MODULE.slice_schedule(MODULE.load_schedule(schedule), "2027-01", "Manual ACH")

            plan = MODULE.build_plan(
                rows,
                MODULE.load_financial_events(oracle),
                MODULE.load_scan_assets(scans),
                id_map={},
                corpus_dir=root,
            )

            self.assertEqual(1, len(plan.unsupported))
            self.assertIn("tenant_account_id", plan.unsupported[0].reason)
            self.assertEqual([], [action for action in plan.actions if action.run_id == "RUN-20270103-03"])

    def test_asset_bearing_receipt_fails_closed_instead_of_scan_upload(self) -> None:
        row = self.row("RUN-SCANNED-RECEIPT", "2027-01-04", "1", "Monthly operations", "Scanned check")
        row = MODULE.ScheduleRow(**{
            **row.__dict__,
            "workflow": "Post and allocate tenant receipt",
            "asset_refs": "SCN-0001",
        })

        plan = MODULE.build_plan(
            [row],
            events={"FIN-00001": self.financial_event("TenantReceipt")},
            assets={"SCN-0001": self.scan_asset("SCN-0001", "receipt.pdf", "Payment")},
            id_map={row.run_id: {"tenant_account_id": "101", "target_charge_entry_id": "501"}},
            corpus_dir=Path("/does/not/matter"),
            corpus_index={"SCN-0001": Path("receipt.pdf")},
        )

        self.assertEqual(1, len(plan.unsupported))
        self.assertIn("asset-bearing tenant receipt", plan.unsupported[0].reason)
        self.assertEqual([], [action for action in plan.actions if action.run_id == row.run_id])

    def test_corpus_index_loads_relative_path_but_scan_rows_still_fail_closed(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            index = root / "corpus-index.csv"
            write_csv(
                index,
                ["asset_id", "relative_path"],
                [{"asset_id": "SCN-0001", "relative_path": "documents/2027-01-02/scn-0001-opening-lease.pdf"}],
            )
            row = MODULE.ScheduleRow(
                order=1,
                run_id="RUN-SCAN",
                simulated_clock="2027-01-02",
                sequence="1",
                phase="Opening setup",
                role="Workspace Administrator",
                client="Web",
                entry_mode="Scan-first",
                workflow="Opening lease intake batch 1",
                entity_refs="L001",
                asset_refs="SCN-0001",
                financial_refs="",
                exact_actions="upload each lease",
                expected_result="",
                evidence_required="",
                negative_or_retry_check="",
                completion_gate="",
            )

            plan = MODULE.build_plan(
                [row],
                events={},
                assets={"SCN-0001": self.scan_asset("SCN-0001", "legacy-flat-name.pdf", "Lease")},
                corpus_dir=root,
                corpus_index=MODULE.load_corpus_index(index),
            )

            self.assertEqual(Path("documents/2027-01-02/scn-0001-opening-lease.pdf"), MODULE.load_corpus_index(index)["SCN-0001"])
            self.assertEqual(1, len(plan.unsupported))
            self.assertIn("upload-only execution is disabled", plan.unsupported[0].reason)
            self.assertEqual([], [action for action in plan.actions if action.run_id == row.run_id])

    def test_unsupported_row_emits_no_clock_or_other_action_for_that_row(self) -> None:
        row = MODULE.ScheduleRow(
            order=1,
            run_id="RUN-UNSUPPORTED",
            simulated_clock="2027-01-05",
            sequence="1",
            phase="Weekly operations",
            role="Property Manager",
            client="Web",
            entry_mode="Manual",
            workflow="Manual web pass: unsupported workflow",
            entity_refs="",
            asset_refs="",
            financial_refs="",
            exact_actions="do a thing",
            expected_result="",
            evidence_required="",
            negative_or_retry_check="",
            completion_gate="",
        )

        plan = MODULE.build_plan([row], events={}, assets={})

        self.assertEqual(1, len(plan.unsupported))
        self.assertEqual([], [action for action in plan.actions if action.run_id == row.run_id])

    def test_receipt_payload_uses_exact_two_decimal_money_string_without_float(self) -> None:
        row = self.row("RUN-MONEY", "2027-01-03", "1", "Monthly operations", "Manual ACH")

        plan = MODULE.build_plan(
            [row],
            events={"FIN-00001": self.financial_event("TenantReceipt", amount_cents=1_234_567_890_123)},
            assets={},
            id_map={row.run_id: {"tenant_account_id": "101", "target_charge_entry_id": "501"}},
        )

        receipt = [action for action in plan.actions if action.kind == "tenant.receipt"][0]
        self.assertEqual("12345678901.23", receipt.json_body["amount"])
        self.assertEqual(501, receipt.json_body["targetChargeEntryId"])
        self.assertNotIn("allocateOldestCharges", receipt.json_body)
        self.assertEqual("-12345678901.23", MODULE.dollars(-1_234_567_890_123))

    def test_receipt_fails_closed_without_exact_target_charge_entry_id(self) -> None:
        row = self.row("RUN-NO-TARGET", "2027-01-03", "1", "Monthly operations", "Manual ACH")

        plan = MODULE.build_plan(
            [row],
            events={"FIN-00001": self.financial_event("TenantReceipt")},
            assets={},
            id_map={row.run_id: {"tenant_account_id": "101"}},
        )

        self.assertEqual([], [action for action in plan.actions if action.run_id == row.run_id])
        self.assertEqual(1, len(plan.unsupported))
        self.assertIn("exact target_charge_entry_id", plan.unsupported[0].reason)

    def test_expense_mapping_fails_closed_until_source_contract_exists(self) -> None:
        row = MODULE.ScheduleRow(
            order=1,
            run_id="RUN-EXPENSE",
            simulated_clock="2027-01-18",
            sequence="1",
            phase="Weekly operations",
            role="Property Manager",
            client="Web",
            entry_mode="Manual web form",
            workflow="Record, review, allocate, and pay operating expense",
            entity_refs="P001",
            asset_refs="",
            financial_refs="FIN-00001",
            exact_actions="create expense",
            expected_result="",
            evidence_required="",
            negative_or_retry_check="",
            completion_gate="",
        )

        plan = MODULE.build_plan(
            [row],
            events={"FIN-00001": self.financial_event("Expense")},
            assets={},
            id_map={row.run_id: {"property_id": "44"}},
        )

        self.assertEqual(1, len(plan.unsupported))
        self.assertIn("exact category/status/allocation/attachment", plan.unsupported[0].reason)
        self.assertEqual([], [action for action in plan.actions if action.run_id == row.run_id])

    def test_completion_ledger_appends_only_passed_proof_rows(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            ledger = root / "ledger.csv"
            proof = root / "proof.csv"
            rows = [self.row("RUN-PASS", "2027-01-03", "1", "Monthly operations", "Web")]
            write_csv(
                proof,
                ["run_id", "passed", "evidence"],
                [
                    {"run_id": "RUN-PASS", "passed": "true", "evidence": "readonly proof"},
                    {"run_id": "RUN-FAIL", "passed": "false", "evidence": "bad"},
                ],
            )

            appended = MODULE.append_completion_ledger(ledger, proof, rows)
            with ledger.open(newline="") as handle:
                ledger_rows = list(csv.DictReader(handle))

            self.assertEqual(1, appended)
            self.assertEqual(["RUN-PASS"], [row["run_id"] for row in ledger_rows])
            self.assertEqual("readonly proof", ledger_rows[0]["evidence"])

    def test_completion_ledger_skips_existing_completed_run_id(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            ledger = root / "ledger.csv"
            proof = root / "proof.csv"
            rows = [self.row("RUN-DONE", "2027-01-03", "1", "Monthly operations", "Web")]
            write_csv(
                ledger,
                ["sim_date", "run_id", "client", "entry_mode", "asset_or_reference", "result", "system_record", "amount", "allocation_or_balance", "evidence"],
                [
                    {
                        "sim_date": "2027-01-03",
                        "run_id": "RUN-DONE",
                        "client": "Web",
                        "entry_mode": "Manual",
                        "asset_or_reference": "",
                        "result": "Completed",
                        "system_record": "",
                        "amount": "",
                        "allocation_or_balance": "",
                        "evidence": "old",
                    }
                ],
            )
            write_csv(proof, ["run_id", "passed", "evidence"], [{"run_id": "RUN-DONE", "passed": "true", "evidence": "new"}])

            appended = MODULE.append_completion_ledger(ledger, proof, rows)
            with ledger.open(newline="") as handle:
                ledger_rows = list(csv.DictReader(handle))

            self.assertEqual(0, appended)
            self.assertEqual(1, len(ledger_rows))
            self.assertEqual("old", ledger_rows[0]["evidence"])

    def test_completion_ledger_dedupes_repeated_passed_proof_rows(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            ledger = root / "ledger.csv"
            proof = root / "proof.csv"
            rows = [self.row("RUN-DUP", "2027-01-03", "1", "Monthly operations", "Web")]
            write_csv(
                proof,
                ["run_id", "passed", "evidence"],
                [
                    {"run_id": "RUN-DUP", "passed": "true", "evidence": "first"},
                    {"run_id": "RUN-DUP", "passed": "true", "evidence": "second"},
                ],
            )

            appended = MODULE.append_completion_ledger(ledger, proof, rows)
            with ledger.open(newline="") as handle:
                ledger_rows = list(csv.DictReader(handle))

            self.assertEqual(1, appended)
            self.assertEqual(1, len(ledger_rows))
            self.assertEqual("first", ledger_rows[0]["evidence"])

    def test_http_runner_uses_verified_default_tls_and_optional_ca_cert(self) -> None:
        self.assertFalse(hasattr(MODULE, "local_dev_ssl_context"))
        default_runner = MODULE.HttpRunner("https://localhost:5666")
        self.assertIsNone(default_runner.context)
        with patch.object(MODULE.ssl, "create_default_context") as create_context:
            runner = MODULE.HttpRunner("https://localhost:5666", ca_cert=Path("ca.pem"))

        create_context.assert_called_once_with(cafile="ca.pem")
        self.assertEqual(create_context.return_value, runner.context)

    @staticmethod
    def row(run_id: str, simulated_clock: str, sequence: str, phase: str, entry_mode: str) -> object:
        return MODULE.ScheduleRow(
            order=1,
            run_id=run_id,
            simulated_clock=simulated_clock,
            sequence=sequence,
            phase=phase,
            role="Property Manager",
            client="Web",
            entry_mode=entry_mode,
            workflow="Post and allocate tenant receipt" if entry_mode == "Manual ACH" else "January recurring rent generation",
            entity_refs="P001; U001; L001",
            asset_refs="",
            financial_refs="FIN-00001",
            exact_actions="run scheduled finance worker",
            expected_result="ok",
            evidence_required="proof",
            negative_or_retry_check="retry",
            completion_gate="gate",
        )

    def write_fixture_files(self, root: Path) -> tuple[Path, Path, Path, Path, Path]:
        schedule = root / "schedule.csv"
        oracle = root / "financial-oracle.csv"
        journal = root / "journal.csv"
        control = root / "control-totals.json"
        scans = root / "scan-assets.csv"
        write_csv(
            schedule,
            SCHEDULE_FIELDS,
            [
                {
                    "run_id": "RUN-20270101-01",
                    "simulated_clock": "2027-01-01",
                    "sequence": "1",
                    "phase": "Monthly operations",
                    "role": "Workspace Administrator",
                    "client": "Web",
                    "entry_mode": "System automation + reconciliation",
                    "workflow": "January recurring rent generation",
                    "entity_refs": "",
                    "asset_refs": "",
                    "financial_refs": "FIN-00002",
                    "exact_actions": "run scheduled finance worker",
                    "expected_result": "",
                    "evidence_required": "",
                    "negative_or_retry_check": "",
                    "completion_gate": "",
                },
                {
                    "run_id": "RUN-20270103-03",
                    "simulated_clock": "2027-01-03",
                    "sequence": "3",
                    "phase": "Monthly operations",
                    "role": "Property Manager",
                    "client": "Web",
                    "entry_mode": "Manual ACH",
                    "workflow": "Post and allocate tenant receipt",
                    "entity_refs": "P006; U006; L006",
                    "asset_refs": "",
                    "financial_refs": "FIN-00012; FIN-00013",
                    "exact_actions": "tenant-account receipt form",
                    "expected_result": "",
                    "evidence_required": "",
                    "negative_or_retry_check": "",
                    "completion_gate": "",
                },
            ],
        )
        write_csv(
            oracle,
            ORACLE_FIELDS,
            [
                {
                    "event_id": "FIN-00013",
                    "entry_date": "2027-01-03",
                    "effective_date": "2027-01-03",
                    "event_type": "TenantReceipt",
                    "category": "Rent receipt",
                    "property_id": "P006",
                    "unit_id": "U006",
                    "lease_id": "L006",
                    "counterparty": "Tenant",
                    "source_mode": "Manual ACH",
                    "source_asset_id": "",
                    "reference": "PMT-1",
                    "amount_cents": "120000",
                    "ar_delta_cents": "",
                    "operating_cash_delta_cents": "",
                    "deposit_cash_delta_cents": "",
                    "reserve_cash_delta_cents": "",
                    "deposit_liability_delta_cents": "",
                    "loan_liability_delta_cents": "",
                    "income_cents": "",
                    "expense_cents": "",
                    "owner_equity_delta_cents": "",
                    "owner_distribution_cents": "",
                    "capital_asset_delta_cents": "",
                    "accumulated_depreciation_delta_cents": "",
                    "memo": "Receipt allocated to rent",
                }
            ],
        )
        write_csv(journal, ["event_id", "line_no"], [])
        control.write_text('{"financial_events": 1}\n')
        write_csv(scans, SCAN_FIELDS, [])
        return schedule, oracle, journal, control, scans

    @staticmethod
    def financial_event(event_type: str, amount_cents: int = 12345) -> object:
        return MODULE.FinancialEvent(
            event_id="FIN-00001",
            entry_date="2027-01-03",
            effective_date="2027-01-03",
            event_type=event_type,
            category="Rent receipt",
            property_id="P001",
            unit_id="U001",
            lease_id="L001",
            counterparty="Tenant",
            source_mode="Manual ACH",
            source_asset_id="",
            reference="REF-1",
            amount_cents=amount_cents,
            memo="memo",
        )

    @staticmethod
    def scan_asset(asset_id: str, filename: str, target: str) -> object:
        return MODULE.ScanAsset(
            asset_id=asset_id,
            planned_date="2027-01-02",
            filename=filename,
            document_family=target,
            intended_target=target,
            property_id="P001",
            unit_id="U001",
            lease_id="L001",
            financial_event_id="",
        )


if __name__ == "__main__":
    unittest.main()
