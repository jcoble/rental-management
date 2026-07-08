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
        services.AddSingleton<IDocumentTemplateFieldCatalog, DocumentTemplateFieldCatalog>();
        services.AddScoped<IDocumentTemplateService, DocumentTemplateService>();
        services.AddScoped<ILeaseQaService, LeaseQaService>();
        // Lease e-sign workflow (send for signature, status, signed-document, webhook completion).
        services.AddScoped<ILeaseEsignService, LeaseEsignService>();
        services.AddScoped<RentalCommand.Api.Services.Security.IEsignWebhookSignatureValidator,
            RentalCommand.Api.Services.Security.EsignWebhookSignatureValidator>();
        // Residential lease agreement PDF rendering (QuestPDF). Stateless → singleton.
        services.AddSingleton<ILeaseAgreementPdfGenerator, LeaseAgreementPdfGenerator>();
        services.AddScoped<ILeaseAgreementRenderer, LeaseAgreementRenderer>();
        // Native e-sign: executed-PDF/certificate renderer (stateless → singleton) + the public,
        // token-scoped signing flow used by SignController.
        services.AddSingleton<RentalCommand.Api.Services.Esign.IExecutedLeasePdfGenerator,
            RentalCommand.Api.Services.Esign.ExecutedLeasePdfGenerator>();
        services.AddScoped<RentalCommand.Api.Services.Esign.INativeSigningService,
            RentalCommand.Api.Services.Esign.NativeSigningService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IExpenseService, ExpenseService>();
        services.AddScoped<ILoanService, LoanService>();
        services.AddScoped<IRecurringExpenseService, RecurringExpenseService>();
        services.AddScoped<IAccountingService, AccountingService>();
        services.AddScoped<IScheduleEService, ScheduleEService>();
        // Year-end accountant packet PDF rendering (QuestPDF). Stateless → singleton.
        services.AddSingleton<IYearEndPacketPdfGenerator, YearEndPacketPdfGenerator>();
        services.AddScoped<IOwnerDistributionService, OwnerDistributionService>();
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

        // --- accounting-integration backbone (provider-agnostic; QuickBooks is provider #1) ---
        // The resolver dispatches the AccountingProvider enum to the registered IAccountingProvider
        // implementations (none in Phase 1 — provider impls land in Phase 2). Scoped (mirrors
        // EdiPlatform's ErpProviderResolver registration) so it can consume the providers Phase 2
        // registers via AddHttpClient without a captive-dependency problem. The app-settings resolver
        // maps the enum to the right *Options POCO so the backbone never reads QuickBooksOptions
        // directly (AC-1).
        services.AddScoped<AccountingProviderResolver>();
        services.AddScoped<AccountingAppSettingsResolver>();
        services.AddScoped<AccountingConnectionService>();
        // Phase 2 — pull-into-domain import engine + the QuickBooks provider (provider #1).
        // AddAccountingProviders is shared with the Engine so the pull worker resolves the same
        // provider/resolver/import-engine the API uses for import-on-connect.
        services.AddScoped<AccountingImportService>();
        // Phase 3 — the ONE provider-agnostic token refresh+persist service, shared by the import path's
        // refresh-on-401 and the Engine's AccountingTokenRefreshWorker.
        services.AddScoped<AccountingTokenService>();
        services.AddAccountingProviders();
        // Per-lease carried-over balance from before the landlord migrated onto Rental Command.
        services.AddScoped<IOpeningBalanceService, OpeningBalanceService>();

        // --- controllers-ops-misc sub-unit ---
        services.AddScoped<IWorkOrderService, WorkOrderService>();
        services.AddScoped<IRecurringMaintenanceTaskService, RecurringMaintenanceTaskService>();
        services.AddScoped<IAppointmentService, AppointmentService>();
        services.AddScoped<IInspectionService, InspectionService>();
        // Inspection report PDF rendering (QuestPDF). Stateless → singleton.
        services.AddSingleton<IInspectionReportPdfGenerator, InspectionReportPdfGenerator>();
        services.AddScoped<IPortalService, PortalService>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<INotificationSettingsService, NotificationSettingsService>();
        // Pluggable SMS providers + resolver (BYO per-portfolio, platform-env fallback). Shared with
        // the Engine outbox path; the API uses it for the synchronous "send test SMS" verify endpoint.
        services.AddSmsProviders();
        services.AddScoped<INoticeDraftService, NoticeDraftService>();
        services.AddScoped<INoticeTemplateService, NoticeTemplateService>();
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

        // Unit Command Center aggregate (per-unit dashboard + timeline union).
        services.AddScoped<IUnitDashboardService, UnitDashboardService>();
        services.AddScoped<IUnitListingService, UnitListingService>();

        // Portfolio analytics overview (occupancy, rent collection, trend, work orders, lease expiry).
        services.AddScoped<IAnalyticsService, AnalyticsService>();

        // --- scan upload pipeline ---
        services.AddScoped<IScanFileService, ScanFileService>();
        services.AddScoped<IScanService, ScanService>();
        services.AddScoped<IAuditTrailService, AuditTrailService>();
        services.AddScoped<IVoiceIntakeService, VoiceIntakeService>();

        // --- unified audit trail (auto-capture + viewer) ---
        // HTTP-backed actor attribution, per-request de-dupe scope, the humanizer, the query service
        // behind /api/v1/audit, and the SaveChanges interceptor that auto-records IAuditable CRUD.
        services.AddScoped<RentalCommand.Core.Interfaces.ICurrentActor,
            RentalCommand.Api.Services.Auditing.HttpCurrentActor>();
        services.AddScoped<RentalCommand.Core.Interfaces.IAuditScope,
            RentalCommand.Data.Auditing.AuditScope>();
        services.AddSingleton<RentalCommand.Api.Services.Auditing.AuditDescriber>();
        services.AddSingleton<RentalCommand.Api.Services.Auditing.AuditDiffBuilder>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<RentalCommand.Data.Auditing.AuditSaveChangesInterceptor>();

        // --- AI (phase 3) ---
        services.AddScoped<IDailyBriefingService, DailyBriefingService>();
        services.AddScoped<IPortfolioQaService, PortfolioQaService>();
        services.AddScoped<IAssistantActionService, AssistantActionService>();
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

        // --- sandbox mode (graduate-once → wipe demo) ---
        // Guard short-circuits money-moving Stripe operations while a portfolio is in Sandbox.
        // SandboxService owns the one-way go-live wipe.
        services.AddScoped<RentalCommand.Core.Interfaces.ISandboxGuard, RentalCommand.Data.SandboxGuard>();
        services.AddScoped<ISandboxService, SandboxService>();

        // --- self-owner provisioning (the landlord IS the first owner) ---
        // Used by registration, Google sign-in, and go-live to auto-create the primary owner; the
        // backfill service is a one-off catch-up for portfolios created before the feature.
        services.AddScoped<ISelfOwnerProvisioner, SelfOwnerProvisioner>();
        services.AddScoped<SelfOwnerBackfillService>();

        return services;
    }
}
