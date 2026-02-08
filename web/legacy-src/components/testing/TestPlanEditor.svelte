<script lang="ts">
	import type { TestLevel, TestType, TestStepType } from '$lib/types';
	import { testPlans as testPlansApi } from '$lib/api/endpoints/testPlans';
	import { Plus, Trash2, GripVertical, ChevronDown, ChevronRight } from '@lucide/svelte';

	let {
		taskId,
		onsave,
		oncancel,
	}: {
		taskId: number;
		onsave?: () => void;
		oncancel?: () => void;
	} = $props();

	interface EditorStep {
		stepType: TestStepType;
		description: string;
		expectedResult: string;
		automationCommand: string;
		requiresManualVerification: boolean;
	}

	interface EditorTest {
		name: string;
		type: TestType;
		description: string;
		testFile: string;
		framework: string;
		steps: EditorStep[];
		expanded: boolean;
	}

	let name = $state('');
	let description = $state('');
	let requiredLevel = $state<TestLevel>('Smoke');
	let tests = $state<EditorTest[]>([]);
	let saving = $state(false);

	function addTest() {
		tests = [...tests, {
			name: '',
			type: 'UI',
			description: '',
			testFile: '',
			framework: '',
			steps: [],
			expanded: true,
		}];
	}

	function removeTest(index: number) {
		tests = tests.filter((_, i) => i !== index);
	}

	function addStep(testIndex: number) {
		tests = tests.map((t, i) => i === testIndex ? {
			...t,
			steps: [...t.steps, {
				stepType: 'Action' as TestStepType,
				description: '',
				expectedResult: '',
				automationCommand: '',
				requiresManualVerification: false,
			}]
		} : t);
	}

	function removeStep(testIndex: number, stepIndex: number) {
		tests = tests.map((t, i) => i === testIndex ? {
			...t,
			steps: t.steps.filter((_, si) => si !== stepIndex)
		} : t);
	}

	function toggleTest(index: number) {
		tests = tests.map((t, i) => i === index ? { ...t, expanded: !t.expanded } : t);
	}

	async function save() {
		if (!name.trim() || tests.length === 0) return;
		// Ensure all tests have names
		if (tests.some(t => !t.name.trim())) return;
		saving = true;
		try {
			await testPlansApi.create(taskId, {
				name: name.trim(),
				description: description.trim() || undefined,
				requiredLevel,
				tests: tests.map((t) => ({
					name: t.name.trim(),
					type: t.type,
					description: t.description.trim() || undefined,
					testFile: t.testFile.trim() || undefined,
					framework: t.framework.trim() || undefined,
					steps: t.steps.length > 0 ? t.steps.map((s) => ({
						stepType: s.stepType,
						description: s.description,
						expectedResult: s.expectedResult || undefined,
						automationCommand: s.automationCommand || undefined,
						requiresManualVerification: s.requiresManualVerification,
					})) : undefined,
				})),
			});
			onsave?.();
		} finally {
			saving = false;
		}
	}

	const testTypes: TestType[] = ['Unit', 'Integration', 'UI', 'Manual'];
	const stepTypes: TestStepType[] = ['Setup', 'Action', 'Assertion', 'Teardown'];
	const levels: TestLevel[] = ['Smoke', 'Comprehensive', 'FullE2E'];
</script>

<div class="rounded-lg border border-border bg-surface p-4">
	<h3 class="mb-3 text-sm font-semibold text-text-primary">New Test Plan</h3>

	<div class="space-y-3">
		<div class="grid grid-cols-2 gap-3">
			<div>
				<label class="mb-1 block text-xs text-text-tertiary">Plan Name</label>
				<input
					type="text"
					bind:value={name}
					class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
					placeholder="e.g., Login Flow Tests"
				/>
			</div>
			<div>
				<label class="mb-1 block text-xs text-text-tertiary">Test Level</label>
				<select
					bind:value={requiredLevel}
					class="w-full rounded-md border border-border bg-bg px-2 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
				>
					{#each levels as level}
						<option value={level}>{level}</option>
					{/each}
				</select>
			</div>
		</div>

		<div>
			<label class="mb-1 block text-xs text-text-tertiary">Description</label>
			<textarea
				bind:value={description}
				rows={2}
				class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none resize-none"
				placeholder="What does this test plan cover?"
			></textarea>
		</div>

		<!-- Tests -->
		<div>
			<div class="mb-2 flex items-center justify-between">
				<label class="text-xs text-text-tertiary">Tests ({tests.length})</label>
				<button
					onclick={addTest}
					class="flex items-center gap-1 text-xs text-accent hover:text-accent-hover"
				>
					<Plus class="h-3 w-3" />
					Add Test
				</button>
			</div>

			<div class="space-y-2">
				{#each tests as test, ti}
					<div class="rounded-md border border-border bg-bg">
						<!-- Test header -->
						<div class="flex items-center gap-2 px-2.5 py-2">
							<button onclick={() => toggleTest(ti)} class="text-text-tertiary">
								{#if test.expanded}
									<ChevronDown class="h-3.5 w-3.5" />
								{:else}
									<ChevronRight class="h-3.5 w-3.5" />
								{/if}
							</button>
							<select
								bind:value={test.type}
								class="rounded border border-border bg-surface px-1.5 py-0.5 text-xs text-text-primary focus:border-accent focus:outline-none"
							>
								{#each testTypes as tt}
									<option value={tt}>{tt}</option>
								{/each}
							</select>
							<input
								type="text"
								bind:value={test.name}
								class="flex-1 rounded border border-border bg-surface px-2 py-0.5 text-xs text-text-primary focus:border-accent focus:outline-none"
								placeholder="Test name..."
							/>
							<button
								onclick={() => removeTest(ti)}
								class="rounded p-0.5 text-text-tertiary hover:text-danger"
							>
								<Trash2 class="h-3 w-3" />
							</button>
						</div>

						{#if test.expanded}
							<div class="border-t border-border px-2.5 py-2 space-y-2">
								<!-- Test metadata -->
								<div class="grid grid-cols-2 gap-2">
									<input
										type="text"
										bind:value={test.testFile}
										class="rounded border border-border bg-surface px-2 py-1 text-xs font-mono text-text-primary focus:border-accent focus:outline-none"
										placeholder="Test file path..."
									/>
									<input
										type="text"
										bind:value={test.framework}
										class="rounded border border-border bg-surface px-2 py-1 text-xs text-text-primary focus:border-accent focus:outline-none"
										placeholder="Framework (e.g., xUnit)"
									/>
								</div>

								<!-- Steps within this test -->
								<div>
									<div class="mb-1 flex items-center justify-between">
										<span class="text-[10px] text-text-tertiary">Steps ({test.steps.length})</span>
										<button
											onclick={() => addStep(ti)}
											class="flex items-center gap-0.5 text-[10px] text-accent hover:text-accent-hover"
										>
											<Plus class="h-2.5 w-2.5" />
											Step
										</button>
									</div>

									{#each test.steps as step, si}
										<div class="mb-1 rounded border border-border bg-surface p-2">
											<div class="mb-1 flex items-center gap-2">
												<GripVertical class="h-3 w-3 text-text-tertiary cursor-grab" />
												<span class="text-[10px] font-medium text-text-tertiary">#{si + 1}</span>
												<select
													bind:value={step.stepType}
													class="rounded border border-border bg-bg px-1 py-0.5 text-[10px] text-text-primary focus:border-accent focus:outline-none"
												>
													{#each stepTypes as st}
														<option value={st}>{st}</option>
													{/each}
												</select>
												<label class="ml-auto flex items-center gap-1 text-[10px] text-text-tertiary">
													<input type="checkbox" bind:checked={step.requiresManualVerification} class="rounded" />
													Manual
												</label>
												<button
													onclick={() => removeStep(ti, si)}
													class="rounded p-0.5 text-text-tertiary hover:text-danger"
												>
													<Trash2 class="h-2.5 w-2.5" />
												</button>
											</div>
											<input
												type="text"
												bind:value={step.description}
												class="mb-1 w-full rounded border border-border bg-bg px-2 py-0.5 text-[10px] text-text-primary focus:border-accent focus:outline-none"
												placeholder="Step description..."
											/>
											<div class="grid grid-cols-2 gap-1">
												<input
													type="text"
													bind:value={step.expectedResult}
													class="rounded border border-border bg-bg px-2 py-0.5 text-[10px] text-text-primary focus:border-accent focus:outline-none"
													placeholder="Expected result..."
												/>
												<input
													type="text"
													bind:value={step.automationCommand}
													class="rounded border border-border bg-bg px-2 py-0.5 text-[10px] font-mono text-text-primary focus:border-accent focus:outline-none"
													placeholder="agent-browser command..."
												/>
											</div>
										</div>
									{/each}
								</div>
							</div>
						{/if}
					</div>
				{/each}
			</div>

			{#if tests.length === 0}
				<button
					onclick={addTest}
					class="w-full rounded-md border-2 border-dashed border-border p-3 text-xs text-text-tertiary hover:border-accent hover:text-accent"
				>
					Add your first test
				</button>
			{/if}
		</div>
	</div>

	<div class="mt-4 flex items-center gap-2">
		<button
			onclick={save}
			disabled={!name.trim() || tests.length === 0 || tests.some(t => !t.name.trim()) || saving}
			class="rounded-md bg-accent px-4 py-1.5 text-sm text-white transition-colors hover:bg-accent-hover disabled:opacity-50"
		>
			{saving ? 'Creating...' : 'Create Plan'}
		</button>
		{#if oncancel}
			<button
				onclick={oncancel}
				class="rounded-md border border-border px-4 py-1.5 text-sm text-text-secondary transition-colors hover:bg-surface-hover"
			>
				Cancel
			</button>
		{/if}
	</div>
</div>
