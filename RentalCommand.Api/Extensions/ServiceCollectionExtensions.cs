using RentalCommand.Api.Scanning;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Sms;
using RentalCommand.Api.Services.Voice;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Listings;
using RentalCommand.Data.Atomic;

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
        services.AddScoped<ILeaseManagementQueryService, LeaseManagementQueryService>();
        services.AddScoped<ITenantAccountQueryService, TenantAccountQueryService>();
        services.AddSingleton<IDocumentTemplateFieldCatalog, DocumentTemplateFieldCatalog>();
        services.AddScoped<IDocumentTemplateService, DocumentTemplateService>();
        services.AddScoped<ILeaseQaService, LeaseQaService>();
        // Residential lease agreement PDF rendering (QuestPDF). Stateless → singleton.
        services.AddSingleton<ILeaseAgreementPdfGenerator, LeaseAgreementPdfGenerator>();
        services.AddSingleton<ILeaseAddendumPdfGenerator, LeaseAddendumPdfGenerator>();
        services.AddScoped<ILeaseAgreementRenderer, LeaseAgreementRenderer>();
        services.AddScoped<RentalCommand.Core.Leasing.ILegalDocumentIssuanceDraftReader,
            RentalCommand.Data.Leasing.LegalDocumentIssuanceDraftReader>();
        services.AddScoped<ILegalDocumentIssuancePreparationService,
            LegalDocumentIssuancePreparationService>();
        // Native e-sign: executed-PDF/certificate renderer (stateless → singleton) + the public,
        // token-scoped signing flow used by SignController.
        services.AddSingleton<RentalCommand.Api.Services.Esign.IExecutedLeasePdfGenerator,
            RentalCommand.Api.Services.Esign.ExecutedLeasePdfGenerator>();
        services.AddScoped<RentalCommand.Data.Esign.INativeEsignExecutionClaimStore,
            RentalCommand.Data.Esign.NativeEsignExecutionClaimStore>();
        services.AddScoped<RentalCommand.Api.Services.Esign.INativeEsignExecutionService,
            RentalCommand.Api.Services.Esign.NativeEsignExecutionService>();
        services.AddScoped<RentalCommand.Api.Services.Esign.INativeSigningService,
            RentalCommand.Api.Services.Esign.NativeSigningService>();
        services.AddScoped<IExpenseService, ExpenseService>();
        services.AddScoped<ICapitalAssetService, CapitalAssetService>();
        services.AddScoped<IPropertyDispositionService, PropertyDispositionService>();
        services.AddScoped<ILoanService, LoanService>();
        services.AddScoped<IRecurringExpenseService, RecurringExpenseService>();
        services.AddScoped<IAccountingService, AccountingService>();
        services.AddScoped<IScheduleEService, ScheduleEService>();
        // Year-end accountant packet PDF rendering (QuestPDF). Stateless → singleton.
        services.AddSingleton<IYearEndPacketPdfGenerator, YearEndPacketPdfGenerator>();
        services.AddScoped<IOwnerDistributionService, OwnerDistributionService>();
        services.AddScoped<IOwnerContributionService, OwnerContributionService>();
        services.AddScoped<IOwnerStatementService, OwnerStatementService>();
        services.AddScoped<IOwnerPortalService, OwnerPortalService>();
        services.AddScoped<ILeasingWorkspaceService, LeasingWorkspaceService>();
        services.AddScoped<IOwnerStatementEmailService, OwnerStatementEmailService>();
        // Reports Hub: read-only report queries over existing data (rent roll, ledger, aging, cash flow,
        // occupancy, deposits, 1099, owner distributions, work orders) + the catalog. No schema changes.
        services.AddScoped<IReportsService, ReportsService>();
        services.AddScoped<ITenantAccountMoveOutStatementService,
            TenantAccountMoveOutStatementService>();
        // Security-deposit move-out statement PDF rendering (QuestPDF). Stateless → singleton.
        services.AddSingleton<IMoveOutStatementPdfGenerator, MoveOutStatementPdfGenerator>();
        services.AddScoped<IBankingService, BankingService>();
        services.AddHttpClient<IPlaidBankingProvider, PlaidBankingProvider>();

        // --- accounting-integration backbone (provider-agnostic; QuickBooks is provider #1) ---
        // The app-settings resolver maps the enum to the provider's *Options POCO so the backbone
        // never reads QuickBooksOptions directly (AC-1).
        services.AddScoped<AccountingAppSettingsResolver>();
        services.AddScoped<AccountingConnectionService>();
        // Phase 2 — pull-into-domain import engine + the QuickBooks provider (provider #1).
        // AddAccountingProviders is shared with the Engine so the pull worker resolves the same
        // provider/resolver/import-engine the API uses for import-on-connect.
        services.AddScoped<AccountingImportService>();
        services.AddScoped<RentalCommand.Data.Accounting.IAccountingConnectionClaimStore,
            RentalCommand.Data.Accounting.AccountingConnectionClaimStore>();
        // Phase 3 — the ONE provider-agnostic token refresh+persist service, shared by the import path's
        // refresh-on-401 and the Engine's AccountingTokenRefreshWorker.
        services.AddScoped<AccountingTokenService>();
        services.AddAccountingProviders();
        // --- controllers-ops-misc sub-unit ---
        services.AddScoped<IWorkOrderService, WorkOrderService>();
        services.AddScoped<ITechnicianExperienceService, TechnicianExperienceService>();
        services.AddScoped<IRecurringMaintenanceTaskService, RecurringMaintenanceTaskService>();
        services.AddScoped<IAppointmentService, AppointmentService>();
        services.AddScoped<IInspectionService, InspectionService>();
        // Inspection report PDF rendering (QuestPDF). Stateless → singleton.
        services.AddSingleton<IInspectionReportPdfGenerator, InspectionReportPdfGenerator>();
        services.AddScoped<IPortalService, PortalService>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IMessagingProviderSettingsResolver, MessagingProviderSettingsResolver>();
        services.AddScoped<INotificationFoundationService, NotificationFoundationService>();
        // Pluggable SMS providers + resolver (BYO per-portfolio, platform-env fallback). Shared with
        // the Engine outbox path; the API uses it for the synchronous "send test SMS" verify endpoint.
        services.AddSmsProviders();
        services.AddScoped<INoticeDraftService, NoticeDraftService>();
        services.AddScoped<IEvictionCaseService, EvictionCaseService>();
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
        services.AddSingleton<IListingChannelAdapter, DisabledZillowListingChannelAdapter>();
        services.AddScoped<IListingWorkspaceService, ListingWorkspaceService>();

        // Portfolio analytics overview (occupancy, rent collection, trend, work orders, lease expiry).
        services.AddScoped<IAnalyticsService, AnalyticsService>();

        // --- scan upload pipeline ---
        services.AddScoped<IScanUploadService, ScanUploadService>();
        services.AddScoped<IScanService, ScanService>();
        services.AddScoped<IWorkspaceLlmCredentialService, WorkspaceLlmCredentialService>();
        services.AddScoped<IWorkspaceLlmCredentialResolver>(sp =>
            sp.GetRequiredService<IWorkspaceLlmCredentialService>());
        services.AddScoped<ILlmUsageEvidenceRecorder>(sp =>
            sp.GetRequiredService<IWorkspaceLlmCredentialService>());
        services.AddScoped<IAuditTrailService, AuditTrailService>();
        services.AddScoped<IVoiceIntakeService, VoiceIntakeService>();

        // --- unified audit trail (canonical atomic viewer) ---
        services.AddScoped<RentalCommand.Core.Interfaces.ICurrentActor,
            RentalCommand.Api.Services.Auditing.HttpCurrentActor>();
        services.AddSingleton<RentalCommand.Api.Services.Auditing.AuditDescriber>();
        services.AddSingleton<RentalCommand.Api.Services.Auditing.AuditDiffBuilder>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();

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
        services.AddPendingFileUploadStore();
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

        return services;
    }
}
