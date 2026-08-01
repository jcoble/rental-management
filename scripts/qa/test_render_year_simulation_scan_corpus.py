import csv
import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("render-year-simulation-scan-corpus.py")
SPEC = importlib.util.spec_from_file_location("render_year_simulation_scan_corpus", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


def rows(name: str) -> list[dict[str, str]]:
    with (MODULE.PLAN / name).open(newline="", encoding="utf-8-sig") as handle:
        return list(csv.DictReader(handle))


class SelectedFebruaryMortgageTests(unittest.TestCase):
    def setUp(self) -> None:
        self.assets = [MODULE.Asset(**row) for row in rows("scan-assets.csv")]
        self.assets_by_id = {asset.asset_id: asset for asset in self.assets}
        self.properties = {row["property_id"]: row for row in rows("portfolio.csv")}
        self.events = {row["event_id"]: row for row in rows("financial-oracle.csv")}

    def context(self, asset_id: str) -> dict[str, str]:
        return MODULE.data_context(
            self.assets_by_id[asset_id],
            self.properties,
            {},
            {},
            self.events,
            {},
        )

    def test_selected_february_mortgage_manifest_inputs_are_narrow(self) -> None:
        populated = {
            asset.asset_id: asset.opening_unpaid_principal_cents
            for asset in self.assets
            if asset.opening_unpaid_principal_cents
        }

        self.assertEqual(
            {"SCN-0478": "12539400", "SCN-0479": "13045800"},
            populated,
        )
        for asset_id in ("SCN-0478", "SCN-0479"):
            self.assertEqual(
                "loan; due date; payment; principal; interest; escrow; "
                "opening unpaid principal; ending unpaid principal",
                self.assets_by_id[asset_id].expected_fields,
            )

    def test_selected_february_mortgage_context_derives_exact_endings(self) -> None:
        expected = {
            "SCN-0478": {
                "opening_unpaid_principal": "$125,394.00",
                "principal": "$431.00",
                "interest": "$615.00",
                "escrow": "$318.00",
                "mortgage_due": "$1,364.00",
                "ending_unpaid_principal": "$124,963.00",
            },
            "SCN-0479": {
                "opening_unpaid_principal": "$130,458.00",
                "principal": "$442.00",
                "interest": "$628.00",
                "escrow": "$318.00",
                "mortgage_due": "$1,388.00",
                "ending_unpaid_principal": "$130,016.00",
            },
        }

        for asset_id, expected_context in expected.items():
            context = self.context(asset_id)
            for key, value in expected_context.items():
                self.assertEqual(value, context[key], f"{asset_id} {key}")

    def test_selected_february_mortgage_rows_preserve_unselected_behavior(self) -> None:
        for asset_id in ("SCN-0478", "SCN-0479"):
            labels = [
                label
                for label, _ in MODULE.field_rows(
                    self.assets_by_id[asset_id],
                    self.context(asset_id),
                )
            ]
            self.assertIn("Opening unpaid principal", labels)
            self.assertIn("Ending unpaid principal", labels)
            self.assertNotIn("Unpaid principal balance", labels)

        unselected = next(
            asset
            for asset in self.assets
            if asset.document_family == "Monthly mortgage statement"
            and asset.asset_id not in {"SCN-0478", "SCN-0479"}
        )
        unselected_context = self.context(unselected.asset_id)
        unselected_labels = [
            label for label, _ in MODULE.field_rows(unselected, unselected_context)
        ]
        self.assertEqual("", unselected_context["opening_unpaid_principal"])
        self.assertEqual("", unselected_context["ending_unpaid_principal"])
        self.assertIn("Unpaid principal balance", unselected_labels)
        self.assertNotIn("Opening unpaid principal", unselected_labels)
        self.assertNotIn("Ending unpaid principal", unselected_labels)


class BankStatementCorpusTests(unittest.TestCase):
    def setUp(self) -> None:
        self.assets = [MODULE.Asset(**row) for row in rows("scan-assets.csv")]
        self.properties = {row["property_id"]: row for row in rows("portfolio.csv")}
        self.bank_assets = [
            asset for asset in self.assets if asset.document_family == "Operating bank statement"
        ]
        self.events = rows("financial-oracle.csv")
        self.journal_cash = MODULE.journal_cash_by_event(rows("journal.csv"))
        self.monthly_controls = {row["month"]: row for row in rows("monthly-controls.csv")}
        self.control_totals = json.loads(
            (MODULE.PLAN / "control-totals.json").read_text(encoding="utf-8")
        )

    def test_manifest_keeps_twelve_operating_statement_upload_assets(self) -> None:
        self.assertEqual(953, len(self.assets))
        self.assertEqual(
            [
                "SCN-0469",
                "SCN-0498",
                "SCN-0527",
                "SCN-0556",
                "SCN-0585",
                "SCN-0614",
                "SCN-0643",
                "SCN-0672",
                "SCN-0701",
                "SCN-0730",
                "SCN-0759",
                "SCN-0788",
            ],
            [asset.asset_id for asset in self.bank_assets],
        )

    def test_operating_statement_transactions_foot_to_journal_and_monthly_controls(self) -> None:
        opening = 15_000_000
        for asset in self.bank_assets:
            month = asset.planned_date[:7]
            packet = MODULE.build_bank_statement_packet(
                asset,
                self.events,
                self.journal_cash,
                self.monthly_controls,
            )
            statement = packet["operating"]
            self.assertEqual("4242", statement.account_mask)
            self.assertEqual(opening, statement.opening_balance_cents)
            month_delta = sum(item.amount_cents for item in statement.transactions)
            self.assertEqual(
                int(self.monthly_controls[month]["operating_cash_delta_cents"]),
                month_delta,
                month,
            )
            self.assertEqual(opening + month_delta, statement.closing_balance_cents)
            self.assertEqual(
                len(statement.transactions),
                len({item.provider_transaction_id for item in statement.transactions}),
            )
            for item in statement.transactions:
                self.assertRegex(item.provider_transaction_id, r"^ys2027-4242-FIN-\d{5}$")
            opening = statement.closing_balance_cents

    def test_january_operating_statement_includes_all_mortgage_escrow(self) -> None:
        statement = MODULE.build_bank_statement_packet(
            self.bank_assets[0],
            self.events,
            self.journal_cash,
            self.monthly_controls,
        )["operating"]

        self.assertEqual(5_935_445, statement.withdrawals_cents)
        self.assertEqual(14_514_555, statement.closing_balance_cents)

    def test_all_mortgage_events_and_render_context_use_one_full_total(self) -> None:
        loan_events = [row for row in self.events if row["event_type"] == "LoanPayment"]
        mortgage_assets = [
            asset for asset in self.assets if asset.document_family == "Monthly mortgage statement"
        ]

        self.assertEqual(240, len(loan_events))
        self.assertEqual(240, len(mortgage_assets))
        self.assertTrue(all(row["category"] == "Mortgage payment" for row in loan_events))
        events_by_id = {row["event_id"]: row for row in loan_events}
        for asset in mortgage_assets:
            event = events_by_id[asset.financial_event_id]
            prop = self.properties[asset.property_id]
            expected_total = (
                -int(event["loan_liability_delta_cents"])
                + int(event["expense_cents"])
                + int(prop["monthly_escrow_cents"])
            )
            self.assertEqual(expected_total, int(event["amount_cents"]))
            self.assertEqual(-expected_total, int(event["operating_cash_delta_cents"]))
            context = MODULE.data_context(
                asset,
                self.properties,
                {},
                {},
                events_by_id,
                {},
            )
            self.assertEqual(MODULE.money(expected_total), context["mortgage_due"])
            self.assertEqual(MODULE.money(prop["monthly_escrow_cents"]), context["escrow"])

    def test_full_year_escrow_increases_cash_withdrawals_by_7632000_cents(self) -> None:
        loan_events = [row for row in self.events if row["event_type"] == "LoanPayment"]
        annual_escrow = sum(
            int(self.properties[row["property_id"]]["monthly_escrow_cents"])
            for row in loan_events
        )

        self.assertEqual(7_632_000, annual_escrow)
        self.assertEqual(7_632_000, self.control_totals["year_mortgage_escrow_cents"])
        self.assertEqual(3_910, self.control_totals["journal_lines"])
        self.assertEqual(1_197_802_346, self.control_totals["journal_debits_cents"])
        self.assertEqual(
            self.control_totals["journal_debits_cents"],
            self.control_totals["journal_credits_cents"],
        )

    def test_companion_account_statement_data_foots_to_monthly_controls(self) -> None:
        for asset in self.bank_assets:
            month = asset.planned_date[:7]
            packet = MODULE.build_bank_statement_packet(
                asset,
                self.events,
                self.journal_cash,
                self.monthly_controls,
            )
            for key, control_column in [
                ("trust", "deposit_cash_delta_cents"),
                ("reserve", "reserve_cash_delta_cents"),
            ]:
                statement = packet[key]
                self.assertEqual(
                    int(self.monthly_controls[month][control_column]),
                    sum(item.amount_cents for item in statement.transactions),
                    f"{month} {key}",
                )

    def test_sidecar_writer_emits_supported_banking_import_shape(self) -> None:
        asset = self.bank_assets[0]
        packet = MODULE.build_bank_statement_packet(
            asset,
            self.events,
            self.journal_cash,
            self.monthly_controls,
        )
        with tempfile.TemporaryDirectory() as tmp:
            pdf = Path(tmp) / asset.filename
            pdf.write_bytes(b"%PDF synthetic test placeholder")
            sidecars = MODULE.write_bank_statement_sidecars(asset, pdf, packet)

            self.assertEqual(3, len(sidecars))
            import_payload = json.loads(pdf.with_suffix(".import.json").read_text())
            self.assertEqual("Manual", import_payload["provider"])
            self.assertEqual("4242", import_payload["accountMask"])
            self.assertGreater(len(import_payload["transactions"]), 0)
            first = import_payload["transactions"][0]
            self.assertIn("providerTransactionId", first)
            self.assertIn("postedAt", first)
            self.assertIn("description", first)
            self.assertIn("amount", first)
            statement_data = json.loads(pdf.with_suffix(".statement-data.json").read_text())
            self.assertEqual(["operating", "trust", "reserve"], [row["key"] for row in statement_data["accounts"]])


if __name__ == "__main__":
    unittest.main()
