import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
import basicSsl from '@vitejs/plugin-basic-ssl';
import { defineConfig, loadEnv } from 'vite';

export default defineConfig(({ mode }) => {
	const env = loadEnv(mode, '.', '');
	const apiTarget = env.API_URL || 'https://localhost:5666';

	return {
		plugins: [basicSsl(), tailwindcss(), sveltekit()],
		server: {
			port: 5667,
			strictPort: true,
			host: true,
			https: true,
			proxy: {
				'/api': {
					target: apiTarget,
					changeOrigin: true,
					secure: false
				}
			}
		}
	};
});
