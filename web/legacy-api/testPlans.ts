import type { TestPlan, TestStep, TestExecution, TestStepResultItem, Test } from '$lib/types';
import { api } from '../client';

export const testPlans = {
	listForTask: (taskId: number) =>
		api.get<TestPlan[]>(`/tasks/${taskId}/test-plans`),

	get: (id: number) =>
		api.get<TestPlan>(`/test-plans/${id}`),

	create: (taskId: number, data: {
		name: string;
		requiredLevel: string;
		description?: string;
		source?: string;
		tests?: Array<{
			name: string;
			type: string;
			description?: string;
			testFile?: string;
			framework?: string;
			steps?: Array<{
				stepType: string;
				description: string;
				expectedResult?: string;
				automationCommand?: string;
				requiresManualVerification?: boolean;
			}>;
		}>;
	}) => api.post<TestPlan>(`/tasks/${taskId}/test-plans`, data),

	update: (id: number, data: Partial<TestPlan>) =>
		api.patch<TestPlan>(`/test-plans/${id}`, data),

	delete: (id: number) =>
		api.delete(`/test-plans/${id}`),

	addTest: (planId: number, data: {
		name: string;
		type: string;
		description?: string;
		testFile?: string;
		framework?: string;
	}) => api.post<Test>(`/test-plans/${planId}/tests`, data),

	updateTest: (testId: number, data: Partial<Test>) =>
		api.patch<Test>(`/tests/${testId}`, data),

	recordTestRun: (testId: number, data: {
		passed: boolean;
		output?: string;
	}) => api.post<Test>(`/tests/${testId}/run`, data),

	addStep: (testId: number, data: {
		stepType: string;
		description: string;
		expectedResult?: string;
		automationCommand?: string;
		requiresManualVerification?: boolean;
	}) => api.post<TestStep>(`/tests/${testId}/steps`, data),

	startExecution: (planId: number, data: {
		executionMode: string;
		executedBy?: string;
	}) => api.post<TestExecution>(`/test-plans/${planId}/execute`, data),

	getExecution: (id: number) =>
		api.get<TestExecution>(`/test-executions/${id}`),

	recordStepResult: (executionId: number, data: {
		testStepId: number;
		status: string;
		actualResult?: string;
		errorMessage?: string;
		screenshot?: string;
		durationMs?: number;
	}) => api.post<TestStepResultItem>(`/test-executions/${executionId}/step-results`, data),

	completeExecution: (executionId: number, data: {
		status: string;
		failureReason?: string;
	}) => api.patch<TestExecution>(`/test-executions/${executionId}/complete`, data),

	canComplete: (taskId: number) =>
		api.get<{ canComplete: boolean; reason?: string }>(`/tasks/${taskId}/can-complete`),
};
