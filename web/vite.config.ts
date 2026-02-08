import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig, loadEnv } from 'vite';

export default defineConfig(({ mode }) => {
	const env = loadEnv(mode, '.', '');
	const apiTarget = env.API_URL || 'http://localhost:5666';

	return {
		plugins: [tailwindcss(), sveltekit()],
		server: {
			port: 5667,
			strictPort: true,
			host: true,
			proxy: {
				'/api': {
					target: apiTarget,
					changeOrigin: true
				}
			}
		}
	};
});
