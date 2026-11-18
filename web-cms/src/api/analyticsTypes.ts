export interface JobStatusDto {
  id: string;
  label: string;
  core: boolean;
  status: "Healthy" | "Stale" | "Failing" | "NotRegistered";
  statusReason: string;
  cron: string | null;
  expectedIntervalMinutes: number;
  lastSuccessAt: string | null;
  lastFailureAt: string | null;
  lastError: string | null;
  lastDurationMs: number | null;
  averageDurationMs: number | null;
  runsTracked: number;
  consecutiveFailures: number;
  failedLast24h: number;
  retryingCount: number;
  enqueuedCount: number;
  scheduledCount: number;
  processingCount: number;
  failedInStorage: number;
  nextExecution: string | null;
}

export interface JobMonitoringReport {
  overallStatus: string;
  generatedAt: string;
  jobs: JobStatusDto[];
}

export interface DurationStatsDto { count: number; averageMinutes: number | null; medianMinutes: number | null; p90Minutes: number | null }
export interface VolumePointDto { bucketStart: string; received: number; resolved: number }
export interface EmployeeResponseDto {
  employeeId: string | null; agentName: string; inquiries: number; responded: number;
  firstResponse: DurationStatsDto; resolution: DurationStatsDto; open: number; overdue: number;
}
export interface InquiryAnalyticsDto {
  from: string; to: string; granularity: string; timeBasis: string;
  total: number; open: number; resolved: number; closed: number; overdue: number; escalatedCases: number;
  escalationsTotal: number; escalationsByLevel: Array<{ level: number; count: number }>;
  firstResponse: DurationStatsDto; resolution: DurationStatsDto;
  reopenedCases: number; totalReopens: number; resolutionRejections: number;
  volume: VolumePointDto[]; employees: EmployeeResponseDto[];
  resolutionAi: { suggested: number; confirmedCases: number; rejectedCases: number; pendingCases: number; acceptanceRate: number | null };
  classification: { confidentDecisions: number; manuallyCorrected: number; importantCases: number; falsePositiveCases: number; accuracy: number | null };
}
