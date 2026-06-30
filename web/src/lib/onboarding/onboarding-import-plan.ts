export type OnboardingImportPhaseKey =
	| 'welcome'
	| 'portfolio'
	| 'properties'
	| 'people'
	| 'leases'
	| 'money'
	| 'automation'
	| 'review';

export interface OnboardingImportPhase {
	key: OnboardingImportPhaseKey;
	label: string;
	title: string;
	explanation: string;
	manualAction: string;
	scanAction: string;
	tooltip: string;
	rewardLabel: string;
}

export const ONBOARDING_IMPORT_PHASES: OnboardingImportPhase[] = [
	{
		key: 'welcome',
		label: 'Start',
		title: 'Choose how you want to bring everything in',
		explanation:
			'Start with the path that matches the paperwork you have. You can type records, scan documents, or mix both without losing your place.',
		manualAction: 'Start with a guided checklist',
		scanAction: 'Scan the first document',
		tooltip: 'A guided import means the app asks for a few things at a time and saves each phase as you go.',
		rewardLabel: 'Import path chosen'
	},
	{
		key: 'portfolio',
		label: 'Business',
		title: 'Name the rental business and owner',
		explanation:
			'This establishes the business name, owner identity, and default settings that show up across reports, notices, and records.',
		manualAction: 'Enter business basics',
		scanAction: 'Scan owner paperwork',
		tooltip: 'Use the name you use for taxes or day-to-day records. You can change it later.',
		rewardLabel: 'Business foundation saved'
	},
	{
		key: 'properties',
		label: 'Properties',
		title: 'Import properties and units',
		explanation:
			'Add every address and the rentable units inside it. A house is one unit; duplexes and apartments get one row for each rentable space.',
		manualAction: 'Add properties and units',
		scanAction: 'Scan a lease, tax bill, or listing',
		tooltip: 'Units are the spaces people rent. Property records group those units under a building or address.',
		rewardLabel: 'Property bundle complete'
	},
	{
		key: 'people',
		label: 'People',
		title: 'Link tenants to the units they occupy',
		explanation:
			'Tenants need a unit connection before leases, balances, portal access, and reminders can work reliably.',
		manualAction: 'Add tenants by unit',
		scanAction: 'Scan applications or leases',
		tooltip: 'Only add the contact details you have. Missing email or phone can be filled in later.',
		rewardLabel: 'Tenant map complete'
	},
	{
		key: 'leases',
		label: 'Leases',
		title: 'Import current and historical leases',
		explanation:
			'Import signed lease history instead of overwriting it. Current leases drive rent, while older leases stay available for records and turnover history.',
		manualAction: 'Enter lease terms',
		scanAction: 'Scan lease agreements',
		tooltip: 'This is an import of an existing signed lease, not a new agreement being sent to a tenant.',
		rewardLabel: 'Lease history preserved'
	},
	{
		key: 'money',
		label: 'Money',
		title: 'Set starting balances, deposits, and recent activity',
		explanation:
			'Capture what matters on day one: security deposits held, unpaid rent, prepaid rent, and recent payments or expenses.',
		manualAction: 'Enter opening money state',
		scanAction: 'Scan receipts, checks, and deposit records',
		tooltip: 'Opening balances tell the ledger where to start without forcing you to recreate years of history.',
		rewardLabel: 'Money starting point set'
	},
	{
		key: 'automation',
		label: 'Automation',
		title: 'Choose what the app should help with next',
		explanation:
			'Turn on reminders, email delivery, tenant portal access, and maintenance intake only when you are ready. Everything stays reviewable.',
		manualAction: 'Review automation settings',
		scanAction: 'Skip scans for this phase',
		tooltip: 'Automation can stay off while you finish importing records. The records still save normally.',
		rewardLabel: 'Automation choices saved'
	},
	{
		key: 'review',
		label: 'Launch',
		title: 'Review the portfolio map and launch',
		explanation:
			'Before finishing, review what was imported, what is missing, and the next best steps for records that still need attention.',
		manualAction: 'Review and launch',
		scanAction: 'Add another document',
		tooltip: 'You can come back to this import center later when you buy a property, take over a unit, or find old documents.',
		rewardLabel: 'Portfolio ready'
	}
];

export type OnboardingPhaseCompletion = Partial<Record<OnboardingImportPhaseKey, boolean>>;

export function onboardingResumePhase(completion: OnboardingPhaseCompletion): OnboardingImportPhaseKey {
	return ONBOARDING_IMPORT_PHASES.find((phase) => !completion[phase.key])?.key ?? 'review';
}

export function onboardingImportPhaseProgress({
	completedPhaseKeys,
	totalPhaseCount
}: {
	completedPhaseKeys: readonly OnboardingImportPhaseKey[];
	totalPhaseCount: number;
}): { completed: number; total: number; percent: number } {
	const total = Math.max(totalPhaseCount, 1);
	const completed = Math.min(completedPhaseKeys.length, total);
	return {
		completed,
		total,
		percent: Math.round((completed / total) * 100)
	};
}
