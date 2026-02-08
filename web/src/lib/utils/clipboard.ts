export async function pasteScreenshotToTask(taskId: number): Promise<Record<string, unknown> | null> {
	try {
		const clipboardItems = await navigator.clipboard.read();
		for (const item of clipboardItems) {
			const imageType = item.types.find((t) => t.startsWith('image/'));
			if (imageType) {
				const blob = await item.getType(imageType);
				const file = new File([blob], `screenshot-${Date.now()}.png`, { type: imageType });
				const formData = new FormData();
				formData.append('file', file);
				const res = await fetch(`/api/tasks/${taskId}/attachments`, {
					method: 'POST',
					body: formData,
				});
				if (!res.ok) return null;
				return (await res.json()) as Record<string, unknown>;
			}
		}
	} catch {
		// Clipboard API not available or no permission
	}
	return null;
}
