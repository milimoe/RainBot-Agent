import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// 构建产物输出到 ASP.NET 的 wwwroot/webui，由 UseStaticFiles 直接托管（/webui/）
export default defineConfig({
  base: '/webui/',
  plugins: [react(), tailwindcss()],
  build: {
    outDir: '../wwwroot/webui',
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 900,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:8080', changeOrigin: true },
    },
  },
});
