import { Navigate, Route, Routes, useLocation } from "react-router-dom";
import TabbedHub from "./components/layout/TabbedHub";
import { LEGACY_REDIRECTS } from "./components/layout/navigation";
import AppShell from "./components/layout/AppShell";
import ProtectedRoute from "./routes/ProtectedRoute";
import LoginPage from "./pages/LoginPage";
import DashboardPage from "./pages/DashboardPage";
import EmployeesPage from "./pages/employees/EmployeesPage";
import EmailAccountsPage from "./pages/email-accounts/EmailAccountsPage";
import EmailMonitoringPage from "./pages/email-monitoring/EmailMonitoringPage";
import EmailClassificationPage from "./pages/email-classification/EmailClassificationPage";
import AiModelsPage from "./pages/ai-models/AiModelsPage";
import AiUsagePage from "./pages/ai-usage/AiUsagePage";
import HelpPage from "./pages/help/HelpPage";
import TutorialPage from "./pages/help/TutorialPage";
import CasesPage from "./pages/cases/CasesPage";
import InquirySupervisionPage from "./pages/inquiry-supervision/InquirySupervisionPage";
import InquiryAnalyticsPage from "./pages/inquiry-supervision/InquiryAnalyticsPage";
import SetupPage from "./pages/setup/SetupPage";
import CaseDetailPage from "./pages/cases/CaseDetailPage";
import AgentsPage from "./pages/agents/AgentsPage";
import ReminderPoliciesPage from "./pages/reminder-policies/ReminderPoliciesPage";
import EscalationPoliciesPage from "./pages/escalation-policies/EscalationPoliciesPage";
import EscalationGroupsPage from "./pages/escalation-groups/EscalationGroupsPage";
import EscalationHistoryPage from "./pages/escalation-history/EscalationHistoryPage";
import AuditLogPage from "./pages/audit-log/AuditLogPage";
import CaseWorkflowPage from "./pages/case-workflow/CaseWorkflowPage";
import ReplyVerificationPage from "./pages/reply-verification/ReplyVerificationPage";
import SystemHealthPage from "./pages/system-health/SystemHealthPage";
import OutboundEmailPage from "./pages/outbound-email/OutboundEmailPage";
import CaseHistoryPage from "./pages/case-history/CaseHistoryPage";
import NotificationsPage from "./pages/notifications/NotificationsPage";
import UsersPage from "./pages/users/UsersPage";
import EmployeeActivityPage from "./pages/employee-activity/EmployeeActivityPage";
import SystemSettingsPage from "./pages/system-settings/SystemSettingsPage";
import SystemConfigurationPage from "./pages/system-configuration/SystemConfigurationPage";
import MaintenancePage from "./pages/maintenance/MaintenancePage";

/** An old page address: goes to the tab that now holds the page, keeping any filters in the link (?status=..., #anchor). */
function LegacyRedirect({ to, tab }: { to: string; tab: string }) {
  const { search, hash } = useLocation();
  const params = new URLSearchParams(search);
  params.set("tab", tab);
  return <Navigate to={`${to}?${params.toString()}${hash}`} replace />;
}

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />

      <Route element={<ProtectedRoute />}>
        <Route element={<AppShell />}>
          <Route path="/" element={<DashboardPage />} />

          <Route path="/inquiry-supervision" element={<TabbedHub menuId="inquiries" tabs={[
            { id: "inquiries", element: <InquirySupervisionPage /> },
            { id: "analytics", element: <InquiryAnalyticsPage /> },
          ]} />} />
          <Route path="/cases" element={<TabbedHub menuId="cases" tabs={[
            { id: "cases", element: <CasesPage /> },
            { id: "history", element: <CaseHistoryPage /> },
          ]} />} />
          <Route path="/cases/:id" element={<CaseDetailPage />} />
          <Route path="/monitoring" element={<TabbedHub menuId="monitoring" tabs={[
            { id: "workflow", element: <CaseWorkflowPage /> },
            { id: "replies", element: <ReplyVerificationPage /> },
            { id: "mailboxes", element: <EmailMonitoringPage /> },
            { id: "escalations", element: <EscalationHistoryPage /> },
            { id: "activity", element: <EmployeeActivityPage /> },
          ]} />} />
          <Route path="/reports" element={<TabbedHub menuId="reports" tabs={[
            { id: "ai-usage", element: <AiUsagePage /> },
            { id: "audit", element: <AuditLogPage /> },
          ]} />} />
          <Route path="/employees" element={<TabbedHub menuId="employees" tabs={[
            { id: "people", element: <EmployeesPage /> },
            { id: "mailboxes", element: <EmailAccountsPage /> },
            { id: "outbound", element: <OutboundEmailPage /> },
            { id: "pcs", element: <AgentsPage /> },
          ]} />} />
          <Route path="/email-ai" element={<TabbedHub menuId="email-ai" tabs={[
            { id: "rules", element: <SystemConfigurationPage /> },
            { id: "classification", element: <EmailClassificationPage /> },
            { id: "models", element: <AiModelsPage /> },
          ]} />} />
          <Route path="/alerts" element={<TabbedHub menuId="alerts" tabs={[
            { id: "popups", element: <NotificationsPage /> },
            { id: "reminders", element: <ReminderPoliciesPage /> },
            { id: "policies", element: <EscalationPoliciesPage /> },
            { id: "groups", element: <EscalationGroupsPage /> },
          ]} />} />
          <Route path="/system" element={<TabbedHub menuId="system" tabs={[
            { id: "setup", element: <SetupPage /> },
            { id: "settings", element: <SystemSettingsPage /> },
            { id: "users", element: <UsersPage /> },
            { id: "health", element: <SystemHealthPage /> },
            { id: "pause", element: <MaintenancePage /> },
          ]} />} />
          <Route path="/help" element={<TabbedHub menuId="help" tabs={[
            { id: "help", element: <HelpPage /> },
            { id: "tutorial", element: <TutorialPage /> },
          ]} />} />

          {LEGACY_REDIRECTS.map((r) => (
            <Route key={r.from} path={r.from} element={<LegacyRedirect to={r.to} tab={r.tab} />} />
          ))}
        </Route>
      </Route>

      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
