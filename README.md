# Restaurant POS

Multi-tenant restaurant POS and management system. It has three parts:

- an ASP.NET Core API;
- a Blazor WebAssembly web app;
- a .NET MAUI Blazor Hybrid desktop app for Windows counter PCs.

The two apps share one set of screens. The design, the decisions and the phase checklist are in
**[PLAN.md](PLAN.md)**.

| | Production |
|---|---|
| Web app | https://restaurant.dsnoopdoc.com |
| API | https://api.restaurant.dsnoopdoc.com |

## Status

- **Phase 0 (scaffold):** done. Every project builds, and the API, web app and desktop app run and talk
  to each other.
- **Phase 1 (backend foundation):** done, API only. It covers sign-up and sign-in (password and counter
  PIN), restaurants and branches, staff, roles and permissions, tenant isolation, settings, branding and
  the audit log. 46 tests run against real PostgreSQL.
- **Phase 2 (screens):** done, in both the web app and the Windows desktop app. It covers sign-in and
  sign-up, staff invites, the counter PIN lock screen, staff and roles, and settings (branches, taxes,
  payment methods, branding with a live receipt preview, counter devices, activity log). English and
  Urdu.
- **Phase 3 (core POS):** done. It covers the menu, business day and cash count, tables, the POS screen,
  the kitchen display (live), split payments, refunds and voids with manager approval, and receipts (PDF,
  browser print, thermal printing from the desktop app). Printer set-up: [docs/PRINTING.md](docs/PRINTING.md).
- **Phase 4 (inventory and deals):** done. Stock items have units and pack sizes, recipes cover items,
  sizes and add-ons, and stock is received, wasted, adjusted and counted against a ledger. Stock is
  deducted when an order goes to the kitchen and put back on a void. Deals cover combos, buy-X-get-Y,
  percent and amount off, happy hours and coupons. Customer profiles hold order history.
- **Phase 5 (reports and release):** built; go-live is left. Reports cover sales, orders, products and
  deals, payments, stock and the day's closing report (80 mm print). Tests cover failures part-way through
  an operation and simultaneous requests (107 tests). The API has rate limits and security headers. The VM
  gets nightly backups, a weekly restore drill and monitoring ([docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)).
  The desktop app has an Inno Setup installer and an update notice. Still to do: compile the installer,
  deploy to production, and test on a real counter PC and thermal printer (the go-live checklist in
  [docs/PRINTING.md](docs/PRINTING.md)).

## Projects

| Project | What it is |
|---|---|
| `src/api/Restaurant.Domain` | Entities and business rules (no dependencies) |
| `src/api/Restaurant.Contracts` | DTOs and the permission catalog, shared with the clients |
| `src/api/Restaurant.Application` | Use cases, validation, interfaces |
| `src/api/Restaurant.Infrastructure` | EF Core + PostgreSQL, Identity, receipts, file storage |
| `src/api/Restaurant.Api` | REST API, auth, SignalR hubs, health |
| `src/clients/Restaurant.Client.Core` | Typed API client, platform interfaces |
| `src/clients/Restaurant.Client.UI` | Every screen and component (Razor class library) |
| `src/clients/Restaurant.Web` | Web app (WebAssembly PWA, served by nginx) |
| `src/clients/Restaurant.Desktop` | Windows desktop app (MAUI Blazor Hybrid) |
| `tests/Restaurant.Tests` | Unit and integration tests |

## Run locally

You need the .NET 10 SDK, Docker, and the MAUI workload (`dotnet workload install maui-windows`) for the
desktop app.

1. Start the dev database (PostgreSQL on `localhost:5434`):
   ```bash
   docker compose -f docker/docker-compose.yml up -d
   ```
2. Start the API on http://localhost:5080. It applies migrations on start in Development, and the health
   check is at `/health`.
   ```bash
   dotnet run --project src/api/Restaurant.Api --launch-profile http
   ```
3. Start the web app on http://localhost:5090. The app itself is at `/app`.
   ```bash
   dotnet watch --project src/clients/Restaurant.Web --launch-profile http
   ```
4. For the desktop app, open `Restaurant.slnx` in Visual Studio and run **Restaurant.Desktop** (Windows
   Machine). Debug builds call `http://localhost:5080`; set `RESTAURANT_API_URL` to use a different API.

Run the tests (Docker must be running; integration tests start their own PostgreSQL container):
```bash
dotnet test tests/Restaurant.Tests/Restaurant.Tests.csproj
```

Add a migration:
```bash
dotnet ef migrations add <Name> --project src/api/Restaurant.Infrastructure
```

## Online and offline

The **desktop app on a registered counter PC** keeps taking orders when the connection drops: it numbers
them itself, prints the tickets, and sends them when the connection is back. Anything that doesn't match
is flagged for a manager to check. See [docs/OFFLINE.md](docs/OFFLINE.md).

Waiters use the web app on tablets or phones: it can be added to the home screen like an app (**Get the
apps** in the menu). The Windows installer is downloaded from the same page. A second screen can show the
customer their order (**Customer display** on the POS). The web app is online-only. Its service worker caches the app itself so it opens fast, but it doesn't
queue orders.

## Deployment

Pushing to `main` deploys to the VM through GitHub Actions. The steps are in
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).
