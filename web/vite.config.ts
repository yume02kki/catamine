import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// `npm run dev` proxies /api to a running api (docker compose exposes it on :8081).
export default defineConfig({
  plugins: [react()],
  worker: { format: "es" },
  server: {
    proxy: { "/api": { target: process.env.API_URL ?? "http://localhost:8081", changeOrigin: true } },
  },
  build: { sourcemap: true, chunkSizeWarningLimit: 800 },
});
