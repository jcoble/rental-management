using RentalCommand.Api.Scanning;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Voice;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Extensions;

/// <summary>
/// Registers the per-entity domain feature services (scoped). Each domain controller depends on one of
/// these; they own portfolio-scoped CRUD and realtime broadcasts. Later sub-units append their own
/// service registrations here so the wiring stays in one place. Call from <c>Program.cs</c> via
/// <c>builder.Services.AddDomainServices()</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDomainServices(this IServiceCollection services)
    {
        services.AddOptions<PlaidOptions>()
            .BindConfiguration(PlaidOptions.SectionName);

        // --- controllers-inventory sub-unit ---
        services.AddScoped<IPortfolioService, PortfolioService>();
        services.AddScoped<IPropertyService, PropertyService>();
        services.AddScoped<IUnitService, UnitService>();
        services.AddScoped<IOwnerEntityService, OwnerEntityService>();
        services.AddScoped<IVendorService, VendorService>();
        services.AddScoped<IVendorDispatchService, VendorDispatchService>();

        // --- controllers-leasing-money sub-unit ---
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<ILeaseService, LeaseService>();
        services.AddScoped<ILeaseQaService, LeaseQaService>();
        // Lease e-sign workflow (send for signature, status, signed-document, webhook completion).
        services.AddScoped<ILeaseEsignService, LeaseEsignService>();
        services.AddScoped<RentalCommand.Api.Services.Security.IEsignWebhookSignatureValidator,
            RentalCommand.Api.Services.Security.EsignWebhookSignatureValidator>();
        // Residential lease agreement PDF rendering (QuestPDF). Stateless → singleton.
        services.AddSingleton<ILeaseAgreementPdfGenerator, LeaseAgreementPdfGenerator>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IExpenseService, ExpenseService>();
        services.AddScoped<IAccountingService, AccountingService>();
        services.AddScoped<IScheduleEService, ScheduleEService>();
        // Year-end accountant packet PDF rendering (QuestPDF). Stateless → singleton.
        services.AddSingleton<IYearEndPacketPdfGenerator, YearEndPacketPdfGenerator>();
        services.AddScoped<IOwnerStatementService, OwnerStatementService>();
        services.AddScoped<IOwnerStatementEmailService, OwnerStatementEmailService>();
        // Reports Hub: read-only report queries over existing data (rent roll, ledger, aging, cash flow,
        // occupancy, deposits, 1099, owner distributions, work orders) + the catalog. No schema changes.
        services.AddScoped<IReportsService, ReportsService>();
        services.AddScoped<ISecurityDepositService, SecurityDepositService>();
        // Security-deposit move-out statement PDF rendering (QuestPDF). Stateless → singleton.
        services.AddSingleton<IMoveOutStatementPdfGenerator, MoveOutStatementPdfGenerator>();
        services.AddScoped<IBankingService, BankingService>();
        services.AddHttpClient<IPlaidBankingProvider, PlaidBankingProvider>();
        // Per-lease carried-over balance from before the landlord migrated onto Rental Command.
        services.AddScoped<IOpeningBalanceService, OpeningBalanceService>();

        // --- controllers-ops-misc sub-unit ---
        services.AddScoped<IWorkOrderService, WorkOrderService>();
        services.AddScoped<IRecurringMaintenanceTaskService, RecurringMaintenanceTaskService>();
        services.AddScoped<IAppointmentService, AppointmentService>();
        services.AddScoped<IInspectionService, InspectionService>();
        // Inspection report PDF rendering (QuestPDF). Stateless → singleton.
        services.AddSingleton<IInspectionReportPdfGenerator, InspectionReportPdfGenerator>();
        services.AddScoped<IActivityService, ActivityService>();
        services.AddScoped<IPortalService, PortalService>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<INotificationSettingsService, NotificationSettingsService>();
        services.AddScoped<INoticeDraftService, NoticeDraftService>();
        services.AddScoped<ISmsInboundRentConfirmationService, SmsInboundRentConfirmationService>();
        services.AddScoped<ISmsInboundVendorDoneService, SmsInboundVendorDoneService>();
        services.AddScoped<ISmsInboundRouter, SmsInboundRouter>();
        services.AddScoped<IApplicationService, ApplicationService>();
        // Tenant screening (FCRA): request screening, read results, generate the adverse-action notice.
        services.AddScoped<IScreeningService, ScreeningService>();
        // Adverse-action notice PDF rendering (QuestPDF). Stateless → singleton.
        services.AddSingleton<IAdverseActionNoticePdfGenerator, AdverseActionNoticePdfGenerator>();
        services.AddScoped<RentalCommand.Api.Services.Security.ISmsWebhookSignatureValidator,
            RentalCommand.Api.Services.Security.SmsWebhookSignatureValidator>();

        // Aggregated read-only KPI rollup for the web dashboard.
        services.AddScoped<IDashboardService, DashboardService>();

        // Portfolio analytics overview (occupancy, rent collection, trend, work orders, lease expiry).
        services.AddScoped<IAnalyticsService, AnalyticsService>();

        // --- scan upload pipeline ---
        services.AddScoped<IScanFileService, ScanFileService>();
        services.AddScoped<IScanService, ScanService>();
        services.AddScoped<IAuditTrailService, AuditTrailService>();
        services.AddScoped<IVoiceIntakeService, VoiceIntakeService>();

        // --- AI (phase 3) ---
        services.AddScoped<IDailyBriefingService, DailyBriefingService>();
        services.AddScoped<IPortfolioQaService, PortfolioQaService>();
        services.AddScoped<IFairHousingReviewService, FairHousingReviewService>();
        // Knowledge base: markdown docs loaded + cached in memory once, shared by the public docs
        // endpoints and the chatbot how-to path. Pure file-load + in-memory retrieval (no DB, no
        // embeddings). Singleton so the parsed cache lives for the process lifetime.
        services.AddSingleton<IKnowledgeBaseService, KnowledgeBaseService>();

        // --- document hub ---
        services.AddScoped<IDocumentService, DocumentService>();

        // --- CSV / bulk import (migration on-ramp) ---
        services.AddScoped<RentalCommand.Api.Services.Import.ICsvImportService,
            RentalCommand.Api.Services.Import.CsvImportService>();

        // --- mobile push registration ---
        services.AddScoped<IDeviceService, DeviceService>();

        return services;
    }
}
