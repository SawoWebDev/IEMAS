# IEMAS CMS

The IEMAS management website (React + TypeScript + Vite). Administrators and supervisors use it to configure mailboxes, employees, Windows Agents, reminder and escalation policies, and to follow inquiries.

- `npm install` then `npm run dev` for development (expects the API on the address in `VITE_API_BASE_URL`, default `/api/v1`).
- `npm run build` type-checks and builds to `dist/`; the Dockerfile serves it with nginx (`nginx.conf`), which also forwards `/api/` and `/hubs/` to the API.
- The menu and the Help page share one source: `src/components/layout/navigation.ts`.
- The Rollout Tutorial is a self-contained static page in `public/rollout-guide/` (copied as-is into the build). The CMS shows it at **Help → Rollout Tutorial** (`/tutorial`); it can also be opened on its own at `/rollout-guide/`.

See the repository [README](../README.md) for the whole system.
