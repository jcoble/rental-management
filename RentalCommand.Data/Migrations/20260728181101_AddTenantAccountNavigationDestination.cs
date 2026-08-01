using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantAccountNavigationDestination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Notifications_NavigationIntentShape",
                table: "Notifications");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Notifications_NavigationIntentShape",
                table: "Notifications",
                sql: "(\n    \"NavigationExperience\" IS NULL\n    AND \"NavigationDestination\" IS NULL\n    AND \"NavigationAccessContextId\" IS NULL\n    AND \"NavigationAccessRevision\" IS NULL\n    AND \"NavigationResourceKind\" IS NULL\n    AND \"NavigationResourceId\" IS NULL\n    AND \"NavigationParentResourceKind\" IS NULL\n    AND \"NavigationParentResourceId\" IS NULL\n    AND \"NavigationChildResourceKind\" IS NULL\n    AND \"NavigationChildResourceId\" IS NULL\n    AND \"NavigationAction\" IS NULL\n    AND \"NavigationExpiresAtUtc\" IS NULL\n    AND \"NavigationFallbackDestination\" IS NULL\n)\nOR\n(\n    \"NavigationExperience\" IS NOT NULL\n    AND \"NavigationExperience\" IN ('Management', 'Leasing', 'Maintenance', 'Owner', 'Tenant')\n    AND \"NavigationDestination\" IS NOT NULL\n    AND \"NavigationDestination\" IN (\n        'Home', 'Notifications', 'Rentals', 'Owners', 'Money', 'Work', 'Inbox',\n        'UnitSummary', 'UnitTenantLease', 'UnitMoney', 'UnitMaintenance', 'UnitRecords',\n        'TenantLedgerEntry', 'Expense', 'ScanDraft', 'Message', 'WorkOrder',\n        'TechnicianWork', 'LeasingRental', 'LeasingApplication', 'LeasingAppointment',\n        'LeasingConversation', 'LeasingMoveIn', 'TenantAccount'\n    )\n    AND \"NavigationAccessContextId\" IS NOT NULL\n    AND \"NavigationAccessContextId\" > 0\n    AND \"NavigationAccessRevision\" IS NOT NULL\n    AND \"NavigationAccessRevision\" > 0\n    AND \"NavigationAction\" IS NOT NULL\n    AND \"NavigationAction\" IN ('Open', 'Review', 'Resolve')\n    AND \"NavigationExpiresAtUtc\" IS NOT NULL\n    AND \"NavigationFallbackDestination\" IS NOT NULL\n    AND \"NavigationFallbackDestination\" IN ('Home', 'Notifications')\n    AND ((\"NavigationResourceKind\" IS NULL AND \"NavigationResourceId\" IS NULL)\n        OR (\"NavigationResourceKind\" IS NOT NULL\n            AND btrim(\"NavigationResourceKind\") <> ''\n            AND \"NavigationResourceId\" IS NOT NULL\n            AND \"NavigationResourceId\" > 0))\n    AND ((\"NavigationParentResourceKind\" IS NULL AND \"NavigationParentResourceId\" IS NULL)\n        OR (\"NavigationParentResourceKind\" IS NOT NULL\n            AND btrim(\"NavigationParentResourceKind\") <> ''\n            AND \"NavigationParentResourceId\" IS NOT NULL\n            AND \"NavigationParentResourceId\" > 0))\n    AND ((\"NavigationChildResourceKind\" IS NULL AND \"NavigationChildResourceId\" IS NULL)\n        OR (\"NavigationChildResourceKind\" IS NOT NULL\n            AND btrim(\"NavigationChildResourceKind\") <> ''\n            AND \"NavigationChildResourceId\" IS NOT NULL\n            AND \"NavigationChildResourceId\" > 0))\n)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Notifications_NavigationIntentShape",
                table: "Notifications");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Notifications_NavigationIntentShape",
                table: "Notifications",
                sql: "(\n    \"NavigationExperience\" IS NULL\n    AND \"NavigationDestination\" IS NULL\n    AND \"NavigationAccessContextId\" IS NULL\n    AND \"NavigationAccessRevision\" IS NULL\n    AND \"NavigationResourceKind\" IS NULL\n    AND \"NavigationResourceId\" IS NULL\n    AND \"NavigationParentResourceKind\" IS NULL\n    AND \"NavigationParentResourceId\" IS NULL\n    AND \"NavigationChildResourceKind\" IS NULL\n    AND \"NavigationChildResourceId\" IS NULL\n    AND \"NavigationAction\" IS NULL\n    AND \"NavigationExpiresAtUtc\" IS NULL\n    AND \"NavigationFallbackDestination\" IS NULL\n)\nOR\n(\n    \"NavigationExperience\" IS NOT NULL\n    AND \"NavigationExperience\" IN ('Management', 'Leasing', 'Maintenance', 'Owner', 'Tenant')\n    AND \"NavigationDestination\" IS NOT NULL\n    AND \"NavigationDestination\" IN (\n        'Home', 'Notifications', 'Rentals', 'Owners', 'Money', 'Work', 'Inbox',\n        'UnitSummary', 'UnitTenantLease', 'UnitMoney', 'UnitMaintenance', 'UnitRecords',\n        'TenantLedgerEntry', 'Expense', 'ScanDraft', 'Message', 'WorkOrder',\n        'TechnicianWork', 'LeasingRental', 'LeasingApplication', 'LeasingAppointment',\n        'LeasingConversation', 'LeasingMoveIn'\n    )\n    AND \"NavigationAccessContextId\" IS NOT NULL\n    AND \"NavigationAccessContextId\" > 0\n    AND \"NavigationAccessRevision\" IS NOT NULL\n    AND \"NavigationAccessRevision\" > 0\n    AND \"NavigationAction\" IS NOT NULL\n    AND \"NavigationAction\" IN ('Open', 'Review', 'Resolve')\n    AND \"NavigationExpiresAtUtc\" IS NOT NULL\n    AND \"NavigationFallbackDestination\" IS NOT NULL\n    AND \"NavigationFallbackDestination\" IN ('Home', 'Notifications')\n    AND ((\"NavigationResourceKind\" IS NULL AND \"NavigationResourceId\" IS NULL)\n        OR (\"NavigationResourceKind\" IS NOT NULL\n            AND btrim(\"NavigationResourceKind\") <> ''\n            AND \"NavigationResourceId\" IS NOT NULL\n            AND \"NavigationResourceId\" > 0))\n    AND ((\"NavigationParentResourceKind\" IS NULL AND \"NavigationParentResourceId\" IS NULL)\n        OR (\"NavigationParentResourceKind\" IS NOT NULL\n            AND btrim(\"NavigationParentResourceKind\") <> ''\n            AND \"NavigationParentResourceId\" IS NOT NULL\n            AND \"NavigationParentResourceId\" > 0))\n    AND ((\"NavigationChildResourceKind\" IS NULL AND \"NavigationChildResourceId\" IS NULL)\n        OR (\"NavigationChildResourceKind\" IS NOT NULL\n            AND btrim(\"NavigationChildResourceKind\") <> ''\n            AND \"NavigationChildResourceId\" IS NOT NULL\n            AND \"NavigationChildResourceId\" > 0))\n)");
        }
    }
}
