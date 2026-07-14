import { api } from "../client";
import { idempotentMutation } from "../idempotency";
import type {
  NotificationItem,
  NotificationListParams,
  UnreadCountResponse,
  MyAlertsResponse,
  UpdateMyAlertsRequest,
  MorningBriefingSettingsResponse,
  UpdateMorningBriefingSettingsRequest,
  TeamRoutingRuleResponse,
  TeamRoutingRuleRecipientResponse,
  TeamRoutingRecipientPreview,
  UpsertTeamRoutingRuleRequest,
  TenantNoticePolicyResponse,
  UpsertTenantNoticePolicyRequest,
  WorkspaceNoticeTemplateResponse,
  CreateWorkspaceNoticeTemplateVersionRequest,
  NoticeDeliveryStatusResponse,
} from "$lib/api/types/notification";

export const notifications = {
  list: (params: NotificationListParams = {}) => {
    const search = new URLSearchParams();
    if (params.unreadOnly) search.set("unreadOnly", "true");
    if (params.take) search.set("take", String(params.take));
    if (params.skip) search.set("skip", String(params.skip));
    const query = search.toString();
    return api.get<NotificationItem[]>(
      `/notifications${query ? `?${query}` : ""}`
    );
  },

  unreadCount: () =>
    api.get<UnreadCountResponse>("/notifications/unread-count"),
  markAsRead: (id: number) =>
    idempotentMutation(`notifications:read:${id}`, (operationKey) =>
      api.post<void>(
        `/notifications/${id}/read`,
        {},
        {
          headers: { "Idempotency-Key": operationKey },
        }
      )
    ),
  markAllAsRead: () =>
    idempotentMutation("notifications:read-all", (operationKey) =>
      api.post<void>(
        "/notifications/read-all",
        {},
        {
          headers: { "Idempotency-Key": operationKey },
        }
      )
    ),
  broadcast: (data: {
    title: string;
    message: string;
    severity?: string;
    actionUrl?: string | null;
  }) =>
    idempotentMutation(
      `notifications:broadcast:${JSON.stringify(data)}`,
      (operationKey) =>
        api.post<NotificationItem>("/notifications/broadcast", data, {
          headers: { "Idempotency-Key": operationKey },
        })
    ),
  myAlerts: {
    get: () => api.get<MyAlertsResponse>("/notification-settings/my-alerts"),
    update: (request: UpdateMyAlertsRequest) =>
      idempotentMutation(
        `notification-settings:my-alerts:${JSON.stringify(request)}`,
        (operationKey) =>
          api.put<MyAlertsResponse>(
            "/notification-settings/my-alerts",
            request,
            {
              headers: { "Idempotency-Key": operationKey },
            }
          )
      ),
  },
  morningBriefing: {
    get: () =>
      api.get<MorningBriefingSettingsResponse>(
        "/notification-settings/morning-briefing"
      ),
    update: (request: UpdateMorningBriefingSettingsRequest) =>
      idempotentMutation(
        `notification-settings:morning-briefing:${JSON.stringify(request)}`,
        (operationKey) =>
          api.put<MorningBriefingSettingsResponse>(
            "/notification-settings/morning-briefing",
            request,
            { headers: { "Idempotency-Key": operationKey } }
          )
      ),
  },
  teamRouting: {
    list: () =>
      api.get<TeamRoutingRuleResponse[]>("/notification-settings/team-routing"),
    replace: (request: UpsertTeamRoutingRuleRequest) =>
      idempotentMutation(
        `notification-settings:team-routing:${JSON.stringify(request)}`,
        (operationKey) =>
          api.put<TeamRoutingRuleResponse>(
            "/notification-settings/team-routing",
            request,
            { headers: { "Idempotency-Key": operationKey } }
          )
      ),
    recipients: (ruleId: number) =>
      api.get<TeamRoutingRuleRecipientResponse[]>(
        `/notification-settings/team-routing/${ruleId}/recipients`
      ),
    preview: (ruleId: number) =>
      api.get<TeamRoutingRecipientPreview[]>(
        `/notification-settings/team-routing/${ruleId}/preview`
      ),
  },
  tenantNotices: {
    listPolicies: () =>
      api.get<TenantNoticePolicyResponse[]>(
        "/notification-settings/tenant-notices"
      ),
    updatePolicy: (
      automationKey: string,
      request: UpsertTenantNoticePolicyRequest
    ) =>
      idempotentMutation(
        `notification-settings:tenant-notice:${automationKey}:${JSON.stringify(
          request
        )}`,
        (operationKey) =>
          api.put<TenantNoticePolicyResponse>(
            `/notification-settings/tenant-notices/${encodeURIComponent(
              automationKey
            )}`,
            request,
            { headers: { "Idempotency-Key": operationKey } }
          )
      ),
    listTemplates: () =>
      api.get<WorkspaceNoticeTemplateResponse[]>(
        "/notification-settings/tenant-notices/templates"
      ),
    listDeliveries: (take = 50) =>
      api.get<NoticeDeliveryStatusResponse[]>(
        `/notification-settings/tenant-notices/deliveries?take=${encodeURIComponent(
          String(take)
        )}`
      ),
    seedTemplates: () =>
      idempotentMutation(
        "notification-settings:tenant-notices:seed",
        (operationKey) =>
          api.post<void>(
            "/notification-settings/tenant-notices/templates/seed",
            {},
            { headers: { "Idempotency-Key": operationKey } }
          )
      ),
    createTemplateVersion: (
      systemKey: string,
      request: CreateWorkspaceNoticeTemplateVersionRequest
    ) =>
      idempotentMutation(
        `notification-settings:tenant-notice-template:${systemKey}:${JSON.stringify(
          request
        )}`,
        (operationKey) =>
          api.post<TenantNoticePolicyResponse>(
            `/notification-settings/tenant-notices/templates/${encodeURIComponent(
              systemKey
            )}/versions`,
            request,
            { headers: { "Idempotency-Key": operationKey } }
          )
      ),
    restoreDefault: (systemKey: string) =>
      idempotentMutation(
        `notification-settings:tenant-notice-template:${systemKey}:restore`,
        (operationKey) =>
          api.post<TenantNoticePolicyResponse>(
            `/notification-settings/tenant-notices/templates/${encodeURIComponent(
              systemKey
            )}/restore-default`,
            {},
            { headers: { "Idempotency-Key": operationKey } }
          )
      ),
  },
};
