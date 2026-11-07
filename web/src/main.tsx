import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { createBrowserRouter, RouterProvider } from "react-router-dom";
import { ApiError } from "./api/client";
import { Shell } from "./components/Shell";
import { LogDetailPage } from "./pages/LogDetailPage";
import { MissionsPage } from "./pages/MissionsPage";
import { FleetPage, JobsPage, NotFound, SettingsPage } from "./pages/OtherPages";
import { ReplayFuzzPage } from "./pages/ReplayFuzzPage";
import { RulesPage } from "./pages/RulesPage";
import { TrafficPage } from "./pages/TrafficPage";
import "./styles.css";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 5_000,
      refetchOnWindowFocus: false,
      retry: (n, e) => !(e instanceof ApiError && e.status < 500) && n < 2,
    },
  },
});

const router = createBrowserRouter([
  {
    element: <Shell />,
    children: [
      { path: "/", element: <MissionsPage /> },
      { path: "/missions/:id", element: <TrafficPage /> },
      { path: "/missions/:id/logs/:logId", element: <LogDetailPage /> },
      { path: "/missions/:id/rules", element: <RulesPage /> },
      { path: "/missions/:id/replay", element: <ReplayFuzzPage /> },
      { path: "/missions/:id/settings", element: <SettingsPage /> },
      { path: "/jobs", element: <JobsPage /> },
      { path: "/fleet", element: <FleetPage /> },
      { path: "*", element: <NotFound /> },
    ],
  },
]);

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
);
