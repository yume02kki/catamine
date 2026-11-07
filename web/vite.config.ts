import { defineConfig } from "vite";

// Dev server proxies the api so the UI can run with `npm run dev` against `docker compose up api`.
export default defineConfig({
  server: {
    proxy: { "/api": { target: process.env.API_URL ?? "http://localhost:8081", changeOrigin: true } },
  },
});
