import { cleanup, fireEvent, render, waitFor } from '@testing-library/svelte';
import { afterEach, describe, expect, it, vi } from 'vitest';
import UnitRecurringMaintenanceViewHarness from './UnitRecurringMaintenanceViewHarness.svelte';
import { recurringMaintenance, type RecurringMaintenanceTask } from '$lib/api/endpoints/recurring-maintenance';

afterEach(async () => {
	cleanup();
	vi.restoreAllMocks();
	await new Promise((resolve) => setTimeout(resolve, 50));
});

const createdTask = {
	id: 901,
	portfolioId: 1,
	propertyId: 42,
	unitId: 314,
	vendorId: null,
	propertyName: 'QA Property',
	unitNumber: '2B',
	vendorName: null,
	title: 'QA-20260810 change HVAC filter',
	description: null,
	category: 'HVAC',
	recurrenceInterval: 'Quarterly',
	nextDueDate: '2027-01-15',
	scheduledTime: null,
	estimatedCost: null,
	monthlyEstimatedCost: null,
	generatedWorkOrderCount: 0,
	lastGeneratedWorkOrderId: null,
	lastGeneratedAtUtc: null,
	isActive: true,
	priority: 'Normal',
	createdAt: '2026-08-10T00:00:00Z',
	updatedAt: '2026-08-10T00:00:00Z',
	testId: 'recurring-maintenance-901',
} as RecurringMaintenanceTask;

describe('unit recurring maintenance create flow', () => {
	it('opens with property and unit context locked, submits the scoped payload, and refreshes after save', async () => {
		const create = vi.spyOn(recurringMaintenance, 'create').mockResolvedValue(createdTask);
		const listPage = vi.spyOn(recurringMaintenance, 'listPage').mockResolvedValue({
			items: [],
			totalCount: 0,
			skip: 0,
			take: 10,
		});
		const view = render(UnitRecurringMaintenanceViewHarness);

		await waitFor(() => expect(listPage).toHaveBeenCalled());
		const initialListCallCount = listPage.mock.calls.length;

		await fireEvent.click(view.getByTestId('unit-recurring-create-button'));
		expect(view.getByTestId('recurring-task-dialog-title').textContent).toContain('New Recurring Task');
		expect(view.getByTestId('recurring-task-property-input-trigger').textContent).toContain('QA Property');
		expect(view.getByTestId('recurring-task-unit-input-trigger').textContent).toContain('Unit 2B');
		expect((view.getByTestId('recurring-task-property-input-trigger') as HTMLButtonElement).disabled).toBe(true);
		expect((view.getByTestId('recurring-task-unit-input-trigger') as HTMLButtonElement).disabled).toBe(true);

		await fireEvent.input(view.getByTestId('recurring-task-title-input'), {
			target: { value: 'QA-20260810 change HVAC filter' },
		});
		await fireEvent.input(view.getByTestId('recurring-task-next-due-input'), {
			target: { value: '01/15/2027' },
		});
		await fireEvent.click(view.getByTestId('recurring-task-form-save'));

		await waitFor(() => expect(create).toHaveBeenCalledOnce());
		expect(create.mock.calls[0][0]).toMatchObject({
			propertyId: 42,
			unitId: 314,
			title: 'QA-20260810 change HVAC filter',
			nextDueDate: '2027-01-15',
		});
		await waitFor(() => expect(listPage.mock.calls.length).toBeGreaterThan(initialListCallCount));
		expect(listPage.mock.calls.at(-1)?.[0]).toMatchObject({ unitId: 314 });
	});
});
