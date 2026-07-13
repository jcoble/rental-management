import { api } from "../client";
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
  markAsRead: (id: number) => api.post<void>(`/notifications/${id}/read`, {}),
  markAllAsRead: () => api.post<void>("/notifications/read-all", {}),
  broadcast: (data: {
    title: string;
    message: string;
    severity?: string;
    actionUrl?: string | null;
  }) => api.post<NotificationItem>("/notifications/broadcast", data),
  myAlerts: {
    get: () => api.get<MyAlertsResponse>("/notification-settings/my-alerts"),
    update: (request: UpdateMyAlertsRequest) =>
      api.put<MyAlertsResponse>("/notification-settings/my-alerts", request),
  },
  morningBriefing: {
    get: () =>
      api.get<MorningBriefingSettingsResponse>(
        "/notification-settings/morning-briefing"
      ),
    update: (request: UpdateMorningBriefingSettingsRequest) =>
      api.put<MorningBriefingSettingsResponse>(
        "/notification-settings/morning-briefing",
        request
      ),
  },
  teamRouting: {
    list: () =>
      api.get<TeamRoutingRuleResponse[]>("/notification-settings/team-routing"),
    replace: (request: UpsertTeamRoutingRuleRequest) =>
      api.put<TeamRoutingRuleResponse>(
        "/notification-settings/team-routing",
        request
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
      api.put<TenantNoticePolicyResponse>(
        `/notification-settings/tenant-notices/${encodeURIComponent(
          automationKey
        )}`,
        request
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
      api.post<void>(
        "/notification-settings/tenant-notices/templates/seed",
        {}
      ),
    createTemplateVersion: (
      systemKey: string,
      request: CreateWorkspaceNoticeTemplateVersionRequest
    ) =>
      api.post<TenantNoticePolicyResponse>(
        `/notification-settings/tenant-notices/templates/${encodeURIComponent(
          systemKey
        )}/versions`,
        request
      ),
    restoreDefault: (systemKey: string) =>
      api.post<TenantNoticePolicyResponse>(
        `/notification-settings/tenant-notices/templates/${encodeURIComponent(
          systemKey
        )}/restore-default`,
        {}
      ),
  },
};
