import importlib.util
import inspect
import csv
import re
import sys
import unittest
from collections import defaultdict
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("generate-year-simulation-plan.py")
SPEC = importlib.util.spec_from_file_location("generate_year_simulation_plan", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


def reset_module_state() -> None:
    for collection_name in [
        "properties",
        "units",
        "leases",
        "assets",
        "schedules",
        "financial_events",
        "journal",
        "coverage",
        "screen_inventory",
        "field_inventory",
        "role_journeys",
        "notification_scenarios",
        "crud_lifecycles",
    ]:
        getattr(MODULE, collection_name).clear()
    MODULE.event_sequence_by_date.clear()


class LoanPaymentOracleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        reset_module_state()
        MODULE.build_portfolio()
        MODULE.build_recurring_and_owner_finance()

    def test_financed_properties_have_one_canonical_escrow_component(self) -> None:
        financed = [prop for prop in MODULE.properties if prop.loan_id]

        self.assertEqual(20, len(financed))
        self.assertTrue(all(prop.monthly_escrow_cents == 31_800 for prop in financed))
        self.assertTrue(
            all(
                prop.monthly_escrow_cents == 0
                for prop in MODULE.properties
                if not prop.loan_id
            )
        )
        self.assertEqual(
            7_632_000,
            sum(prop.monthly_escrow_cents * 12 for prop in financed),
        )

    def test_all_loan_payments_use_principal_interest_and_escrow_once(self) -> None:
        properties = {prop.property_id: prop for prop in MODULE.properties}
        payments = [
            event for event in MODULE.financial_events if event.event_type == "LoanPayment"
        ]

        self.assertEqual(240, len(payments))
        principal_and_interest = sum(
            -payment.loan_liability_delta_cents + payment.expense_cents
            for payment in payments
        )
        full_payments = sum(payment.amount_cents for payment in payments)
        self.assertEqual(32_304_000, principal_and_interest)
        self.assertEqual(39_936_000, full_payments)
        self.assertEqual(7_632_000, full_payments - principal_and_interest)
        for payment in payments:
            prop = properties[payment.property_id]
            expected_total = (
                -payment.loan_liability_delta_cents
                + payment.expense_cents
                + prop.monthly_escrow_cents
            )
            self.assertEqual("Mortgage payment", payment.category)
            self.assertEqual(expected_total, payment.amount_cents)
            self.assertEqual(-expected_total, payment.operating_cash_delta_cents)

    def test_each_loan_payment_journal_has_the_full_balanced_split(self) -> None:
        properties = {prop.property_id: prop for prop in MODULE.properties}
        payments = {
            event.event_id: event
            for event in MODULE.financial_events
            if event.event_type == "LoanPayment"
        }
        payment_lines: dict[str, list[dict]] = {event_id: [] for event_id in payments}
        for line in MODULE.journal:
            if line["event_id"] in payment_lines:
                payment_lines[line["event_id"]].append(line)

        for event_id, payment in payments.items():
            prop = properties[payment.property_id]
            expected_total = (
                -payment.loan_liability_delta_cents
                + payment.expense_cents
                + prop.monthly_escrow_cents
            )
            self.assertEqual(
                {
                    "2200 Mortgage Loans Payable": (-payment.loan_liability_delta_cents, 0),
                    "5040 Mortgage Interest": (payment.expense_cents, 0),
                    "1030 Mortgage Escrow Asset": (prop.monthly_escrow_cents, 0),
                    "1000 Operating Cash": (0, expected_total),
                },
                {
                    line["account"]: (line["debit_cents"], line["credit_cents"])
                    for line in payment_lines[event_id]
                },
            )

    def test_generator_has_no_principal_and_interest_only_payment_category(self) -> None:
        source = inspect.getsource(MODULE.build_recurring_and_owner_finance)

        self.assertNotIn('"Mortgage principal and interest"', source)


class RentDueDateOracleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        reset_module_state()
        MODULE.build_portfolio()
        MODULE.build_leases()
        MODULE.build_monthly_rent_and_payments()

    def test_l031_is_the_only_day_31_opening_rent_due_day(self) -> None:
        due_days = {lease.lease_id: lease.rent_due_day for lease in MODULE.leases}

        self.assertEqual(31, due_days["L031"])
        self.assertTrue(
            all(day == 1 for lease_id, day in due_days.items() if lease_id != "L031")
        )

    def test_l031_due_day_31_clamps_to_actual_month_end(self) -> None:
        l031 = MODULE.lease_by_id("L031")

        self.assertEqual(
            {
                "2027-01": "2027-01-31",
                "2027-02": "2027-02-28",
                "2027-03": "2027-03-31",
                "2027-04": "2027-04-30",
                "2027-11": "2027-11-30",
            },
            {
                f"2027-{month:02d}": MODULE.rent_due_date(
                    l031, MODULE.date(2027, month, 1)
                ).isoformat()
                for month in [1, 2, 3, 4, 11]
            },
        )

    def test_recurring_rent_charge_events_use_lease_due_dates(self) -> None:
        rent_charges = {
            (event.lease_id, event.reference): event
            for event in MODULE.financial_events
            if event.event_type == "TenantCharge" and event.category == "Rent"
        }

        self.assertEqual(
            "2027-02-28",
            rent_charges[("L031", "RENT-202702-L031")].entry_date,
        )
        self.assertEqual(
            "2027-04-30",
            rent_charges[("L031", "RENT-202704-L031")].entry_date,
        )
        self.assertEqual(
            "2027-02-01",
            rent_charges[("L030", "RENT-202702-L030")].entry_date,
        )
        self.assertEqual(
            "2027-02-01",
            rent_charges[("L043", "RENT-202702-L043")].entry_date,
        )

    def test_recurring_rent_schedule_splits_due_day_batches(self) -> None:
        rent_charges = {
            (event.lease_id, event.reference): event.event_id
            for event in MODULE.financial_events
            if event.event_type == "TenantCharge" and event.category == "Rent"
        }
        feb_l031_id = rent_charges[("L031", "RENT-202702-L031")]
        jan_l031_id = rent_charges[("L031", "RENT-202701-L031")]
        rent_runs = {
            (schedule.simulated_clock, schedule.workflow): schedule
            for schedule in MODULE.schedules
            if "recurring rent generation" in schedule.workflow
        }

        feb_first = rent_runs[("2027-02-01", "February recurring rent generation")]
        feb_month_end = rent_runs[
            ("2027-02-28", "February recurring rent generation due day 31")
        ]
        jan_month_end = rent_runs[
            ("2027-01-31", "January recurring rent generation due day 31")
        ]

        self.assertIn("36 active tenant accounts due 2027-02-01", feb_first.entity_refs)
        self.assertNotIn(feb_l031_id, feb_first.financial_refs)
        self.assertEqual(36, len(feb_first.financial_refs.split("; ")))
        self.assertEqual("1 active tenant accounts due 2027-02-28", feb_month_end.entity_refs)
        self.assertEqual(feb_l031_id, feb_month_end.financial_refs)
        self.assertEqual(jan_l031_id, jan_month_end.financial_refs)

    def test_l031_early_receipts_are_marked_as_advance_receipts(self) -> None:
        receipts = [
            event
            for event in MODULE.financial_events
            if event.lease_id == "L031" and event.event_type == "TenantReceipt"
        ]
        receipt_runs = [
            schedule
            for schedule in MODULE.schedules
            if schedule.workflow == "Post advance tenant receipt"
            and "L031" in schedule.entity_refs
        ]

        self.assertEqual(12, len(receipts))
        self.assertEqual(12, len(receipt_runs))
        self.assertTrue(all(event.memo.startswith("Advance receipt held for FIN-") for event in receipts))
        self.assertTrue(
            all(
                MODULE.date.fromisoformat(receipt.entry_date)
                < MODULE.date.fromisoformat(
                    MODULE.financial_event_by_id(receipt.memo.removeprefix("Advance receipt held for ")).entry_date
                )
                for receipt in receipts
            )
        )


class HistoricalScheduleFreezeTests(unittest.TestCase):
    @staticmethod
    def _financial_refs(row: dict) -> list[str]:
        return [part.strip() for part in row["financial_refs"].split(";") if part.strip()]

    @classmethod
    def _generated_schedule_rows(cls) -> list[dict]:
        with (MODULE.OUT / "schedule.csv").open(newline="", encoding="utf-8") as handle:
            return list(csv.DictReader(handle))

    @classmethod
    def _financial_event_rows(cls) -> dict[str, dict]:
        with (MODULE.OUT / "financial-oracle.csv").open(newline="", encoding="utf-8") as handle:
            return {row["event_id"]: row for row in csv.DictReader(handle)}

    def test_generated_schedule_preserves_historical_rows_through_jan_30(self) -> None:
        with MODULE.HISTORICAL_SCHEDULE_FIXTURE.open(newline="", encoding="utf-8") as handle:
            frozen_rows = list(csv.DictReader(handle))
        generated_rows = [
            row
            for row in self._generated_schedule_rows()
            if row["simulated_clock"] <= MODULE.HISTORICAL_SCHEDULE_FREEZE_END.isoformat()
        ]

        self.assertEqual(frozen_rows, generated_rows)

    def test_recurring_rent_batches_reference_only_charges_due_on_that_date(self) -> None:
        events = self._financial_event_rows()
        for row in self._generated_schedule_rows():
            if "recurring rent generation" not in row["workflow"]:
                continue
            for event_id in self._financial_refs(row):
                event = events[event_id]
                self.assertEqual("TenantCharge", event["event_type"])
                self.assertEqual("Rent", event["category"])
                self.assertEqual(
                    row["simulated_clock"],
                    event["entry_date"],
                    f"{row['run_id']} references {event_id} due on {event['entry_date']}",
                )

    def test_advance_receipt_rows_are_not_labeled_as_allocated_receipts(self) -> None:
        events = self._financial_event_rows()
        rows_by_financial_ref: defaultdict[str, list[dict]] = defaultdict(list)
        for row in self._generated_schedule_rows():
            for event_id in self._financial_refs(row):
                rows_by_financial_ref[event_id].append(row)

        advance_receipts = [
            event
            for event in events.values()
            if event["event_type"] == "TenantReceipt"
            and event["memo"].startswith("Advance receipt held for ")
        ]

        self.assertTrue(advance_receipts)
        for receipt in advance_receipts:
            charge_id = receipt["memo"].removeprefix("Advance receipt held for ")
            self.assertLess(receipt["entry_date"], events[charge_id]["entry_date"])
            receipt_rows = rows_by_financial_ref[receipt["event_id"]]
            self.assertTrue(receipt_rows, f"No schedule row references {receipt['event_id']}")
            for row in receipt_rows:
                self.assertEqual("Post advance tenant receipt", row["workflow"])
                self.assertIn("advance receipt", row["exact_actions"])
                self.assertIn("Advance receipt is append-only", row["expected_result"])
                self.assertNotIn("allocated once to the correct rent charge", row["expected_result"])

    def test_completed_run_ids_keep_original_workflows(self) -> None:
        rows = {
            row.run_id: row
            for row in MODULE.load_historical_schedule_freeze()
        }

        self.assertEqual(
            "Fill, save, reload, edit, and validate every declared field on web/src/lib/components/applications/PrepareMoveInDialog.svelte",
            rows["RUN-20270121-03"].workflow,
        )
        self.assertEqual(
            "Fill, save, reload, edit, and validate every declared field on web/src/lib/components/document-templates/LeaseTemplateDesigner.svelte",
            rows["RUN-20270128-02"].workflow,
        )
        self.assertEqual(
            "Fill, save, reload, edit, and validate every declared field on web/src/lib/components/forms/LeaseTermFields.svelte",
            rows["RUN-20270128-03"].workflow,
        )


class RetiredAssistantLaneTests(unittest.TestCase):
    def test_retired_floating_assistant_files_and_imports_are_removed(self) -> None:
        for source_file in MODULE.RETIRED_WEB_FIELD_SOURCES:
            self.assertFalse((MODULE.ROOT / source_file).exists(), source_file)

        retired_import = re.compile(
            r"import\s+[^;\n]*(?:assistantChat|AssistantBubble|AssistantPanel|AssistantMessage)"
            r"|from\s+['\"][^'\"]*(?:assistantChat|AssistantBubble|AssistantPanel|AssistantMessage)",
            re.DOTALL,
        )
        scanned_files = [
            path
            for path in (MODULE.ROOT / "web" / "src").rglob("*")
            if path.suffix in {".svelte", ".ts"}
        ]
        offenders = [
            str(path.relative_to(MODULE.ROOT))
            for path in scanned_files
            if retired_import.search(path.read_text(encoding="utf-8"))
        ]

        self.assertEqual([], offenders)

    def test_generator_does_not_schedule_retired_assistant_panel_fields(self) -> None:
        reset_module_state()
        MODULE.build_screen_and_field_inventory()

        retired_field_rows = [
            row
            for row in MODULE.field_inventory
            if row["source_file"] in MODULE.RETIRED_WEB_FIELD_SOURCES
            or "AssistantPanel.svelte" in row["source_file"]
            or "AssistantPanel.svelte" in row["screen_or_component"]
        ]
        retired_schedule_rows = [
            schedule
            for schedule in MODULE.schedules
            if "AssistantPanel.svelte" in schedule.workflow
            or (
                "FLD-0024" in schedule.entity_refs
                and "AssistantPanel.svelte" in schedule.workflow
            )
        ]

        self.assertEqual([], retired_field_rows)
        self.assertEqual([], retired_schedule_rows)


class WebNativeControlPatternTests(unittest.TestCase):
    def test_matches_native_controls_only(self) -> None:
        source = """
            <Select.Root>
                <Select.Trigger />
                <Select.Content>
                    <Select.Item value="house">House</Select.Item>
                </Select.Content>
            </Select.Root>
            <select name="type"><option>House</option></select>
            <input name="address" />
            <textarea name="notes"></textarea>
        """

        matches = [match.group(1).lower() for match in MODULE.WEB_NATIVE_CONTROL.finditer(source)]

        self.assertEqual(["select", "input", "textarea"], matches)

    def test_component_names_are_not_case_folded_into_native_controls(self) -> None:
        source = '<Input data-testid="name" /><Select data-testid="type" />'

        self.assertEqual([], list(MODULE.WEB_NATIVE_CONTROL.finditer(source)))


class WebSharedFieldInventoryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        reset_module_state()
        MODULE.build_screen_and_field_inventory()

    def assert_component_fields(
        self, source_file: str, expected: dict[str, str]
    ) -> None:
        rows = [
            row for row in MODULE.field_inventory if row["source_file"] == source_file
        ]

        self.assertEqual(len(expected), len(rows))
        self.assertEqual(
            expected,
            {row["field_key"]: row["label_or_hint"] for row in rows},
        )

    def test_lease_term_fields_keep_all_distinct_controls(self) -> None:
        self.assert_component_fields(
            "web/src/lib/components/forms/LeaseTermFields.svelte",
            {
                "${testidPrefix}-number-input": "Lease number",
                "${testidPrefix}-start-input": "Start date",
                "${testidPrefix}-end-input": "End date",
                "${testidPrefix}-rent-input": "Monthly rent",
                "${testidPrefix}-deposit-input": "Security deposit",
                "${testidPrefix}-late-fee-input": "Late fee",
                "${testidPrefix}-due-day-input": "Due day",
                "${testidPrefix}-status-input": "Status",
                "${testidPrefix}-notes-input": "Notes",
            },
        )

    def test_property_fields_keep_all_distinct_controls(self) -> None:
        self.assert_component_fields(
            "web/src/lib/components/forms/PropertyFields.svelte",
            {
                "${testidPrefix}-single-rental": "One rental",
                "${testidPrefix}-multi-rental": "Multiple rentals",
                "${testidPrefix}-name-input": "Name",
                "${testidPrefix}-address-input": "Address",
                "${testidPrefix}-city-input": "City",
                "${testidPrefix}-state-input": "State",
                "${testidPrefix}-postal-input": "ZIP",
                "${testidPrefix}-type-input": "Type",
            },
        )

    def test_tenant_fields_keep_all_distinct_controls(self) -> None:
        self.assert_component_fields(
            "web/src/lib/components/forms/TenantFields.svelte",
            {
                "${testidPrefix}-first-name-input": "First name",
                "${testidPrefix}-last-name-input": "Last name",
                "${testidPrefix}-email-input": "Email",
                "${testidPrefix}-phone-input": "Phone",
                "${testidPrefix}-emergency-input": "Emergency contact",
            },
        )

    def test_unit_fields_keep_all_distinct_controls(self) -> None:
        self.assert_component_fields(
            "web/src/lib/components/forms/UnitFields.svelte",
            {
                "${testidPrefix}-number-input": "Unit number",
                "${testidPrefix}-bedrooms-input": "Beds",
                "${testidPrefix}-bathrooms-input": "Baths",
                "${testidPrefix}-rent-input": "Rent",
                "${testidPrefix}-square-feet-input": "Square feet",
                "${testidPrefix}-floor-plan-input": "Floor plan",
                "${testidPrefix}-notes-input": "Notes",
            },
        )

    def test_addendum_create_dialog_keeps_all_visible_declared_controls(self) -> None:
        self.assert_component_fields(
            "web/src/lib/components/leases/AddendumCreateDialog.svelte",
            {
                "addendum-create-number": "Addendum number",
                "purpose": "What is changing?",
                "documentTemplateId": "Document template",
                "effectiveFromOn": "Change begins",
                "effectiveThroughOn": "Change ends (optional)",
                "signer.signingOrder": "Order",
                "signer.nameSnapshot": "Name",
                "signer.emailSnapshot": "Email",
                "signer.signerRole": "Role",
                "effect.effectType": "Money change",
                "effect.amount": "Amount",
                "effect.currency": "Currency",
                "effect.chargeCode": "Charge code",
                "effect.dueOn": "Due date",
                "effect.effectiveFromOn": "Begins",
                "effect.effectiveThroughOn": "Ends (optional)",
                "effect.description": "Description",
            },
        )


if __name__ == "__main__":
    unittest.main()
