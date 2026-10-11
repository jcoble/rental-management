import { cleanup, fireEvent, render } from '@testing-library/svelte';
import { afterEach, describe, expect, it, vi } from 'vitest';
import LeaseArtifactActions from '$lib/components/leases/LeaseArtifactActions.svelte';
import type { LegalDocumentArtifactSummary } from '$lib/types';

afterEach(() => cleanup());

function artifact(kind: string): LegalDocumentArtifactSummary {
	return {
		legalDocumentArtifactId: kind === 'issued' ? 41 : 42,
		publicId: `${kind}-artifact`,
		artifactKind: kind,
		fileName: `lease-${kind}.pdf`,
		contentType: 'application/pdf',
		byteLength: 12,
		contentSha256: `${kind}-hash`,
		createdAtUtc: '2027-01-01T12:00:00Z'
	};
}

describe('rendered lease artifact actions', () => {
	it('routes issued and executed view/download clicks with the exact artifact payload', async () => {
		const issued = artifact('issued');
		const executed = artifact('executed');
		const onview = vi.fn();
		const ondownload = vi.fn();
		const view = render(LeaseArtifactActions, {
			props: {
				issuedArtifact: issued,
				executedArtifact: executed,
				onview,
				ondownload,
				testidPrefix: 'lease-42'
			}
		});

		await fireEvent.click(view.getByTestId('lease-42-view-issued'));
		await fireEvent.click(view.getByTestId('lease-42-download-issued'));
		await fireEvent.click(view.getByTestId('lease-42-view-executed'));
		await fireEvent.click(view.getByTestId('lease-42-download-executed'));

		expect(onview.mock.calls.map(([payload]) => payload)).toEqual([issued, executed]);
		expect(ondownload.mock.calls.map(([payload]) => payload)).toEqual([issued, executed]);
	});

	it('renders the plain no-signed-document state when no executed artifact exists', () => {
		const view = render(LeaseArtifactActions, {
			props: {
				issuedArtifact: artifact('issued'),
				executedArtifact: null,
				onview: vi.fn(),
				ondownload: vi.fn(),
				testidPrefix: 'lease-19',
				noExecutedArtifactMessage: 'No signed lease document yet'
			}
		});

		expect(view.getByTestId('lease-19-no-executed').textContent).toContain('No signed lease document yet');
		expect(view.queryByTestId('lease-19-view-executed')).toBeNull();
		expect(view.queryByTestId('lease-19-download-executed')).toBeNull();
	});
});
