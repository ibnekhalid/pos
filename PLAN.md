# Restaurant POS — Implementation Plan

Multi-tenant restaurant POS and management system for small and medium restaurants, starting in Pakistan.
It ships three clients that share one backend and one set of screens:

| Client | Who uses it | Where it runs |
|---|---|---|
| **Desktop POS** (`Restaurant.Desktop`, .NET MAUI Blazor Hybrid) | Cashiers and waiters at the counter, kitchen screen | Windows 10/11 counter PCs, touchscreens |
| **Web app** (`Restaurant.Web`, Blazor WebAssembly PWA) | Owners and managers (back office, reports), any device without the desktop app | Any modern browser |
| **API** (`Restaurant.Api`, ASP.NET Core) | Both clients. It owns every business rule. | Docker on the existing Ubuntu VM |

| | Production URL |
|---|---|
| Website / web app | `https://restaurant.dsnoopdoc.com` |
| API (desktop app, integrations) | `https://api.restaurant.dsnoopdoc.com` |

The structure, build, deployment, branding and receipts deliberately copy **Tailor Book**
(`D:\Repos\tailor\tailorbook`), which already runs in production on the same VM. Section 13 maps each
Tailor Book piece to its Restaurant equivalent.

---

## 1. Decisions

These are decided so work can start. Items marked **(review)** deviate from the original brief or
are business choices you may want to change. Each is cheap to change now and expensive later.

| # | Decision | Why |
|---|---|---|
| D1 | **The web client is a Blazor WebAssembly PWA, not Interactive Server (review).** It's served by nginx and calls the API, exactly like Tailor Book. | The brief asked for "interactive server rendering", but the desktop app must share screens and go through the API. With WebAssembly, the web and desktop clients run the *same* components against the *same* typed API client. A Server-mode circuit also drops on flaky counter Wi-Fi and loses the cart. One code path, tested once. |
| D2 | **ASP.NET Core Identity** stores users (password hashing, lockout, reset tokens). Clients get a **JWT access token (15 min) plus a rotating refresh token** (Tailor Book's model). | Identity covers the security basics. JWT works the same in the browser and in MAUI, where cookies are awkward. |
| D3 | **Tenant roles and permissions live in our own tables, not Identity roles.** Identity has only one role, `PlatformAdmin`, which is separate from restaurant ownership. | Identity roles are global, but restaurant roles are per tenant and owners can edit them. |
| D4 | **Tenant and branch come from the token plus a membership check on each request**, never from the request body. | Required by the brief. Section 4 describes it. |
| D5 | **`BusinessSession` (the branch's business day) and `RegisterSession` (one cash drawer's shift) are separate entities (review).** By default each branch has one register, and *Start day* opens both in one step, so a small restaurant sees a single flow. | The brief allows this split. It keeps "what was sold today" apart from "how much cash is in this drawer", and it supports two tills or shift handovers later without a schema change. |
| D6 | **Stock is deducted when an order is sent to the kitchen**, once per order line, enforced by a unique key. Cancelling after that point writes a reversal, also with a unique key. | Required by the brief. Payment retries, reprints and status changes never touch stock. |
| D7 | **Orders that are open or unpaid, and open kitchen tickets, block closing the day (review).** The close screen lists each blocker with its fix: settle, void (with reason and permission) or complete. Nothing is carried over or discarded silently. | This is the safest default. "Carry an unpaid order over to the next day" could be added as an audited action later. |
| D8 | **Online-only POS in release 1.** The desktop app reconnects and safely retries (every write carries an idempotency key), and it caches the menu for display, but it does **not** take orders offline. Offline order capture is an optional Phase 6 with its own design. | The brief says not to claim offline without durable sync and conflict handling. |
| D9 | **Desktop receipt printing sends raw ESC/POS bytes to a Windows-installed printer** (winspool `WritePrinter`, RAW datatype). PDF and browser printing are the fallback. The web app prints an HTML receipt sized for 80/58 mm. | Raw ESC/POS is fast and silent, supports auto-cut and the cash-drawer kick, and works with the generic drivers that cheap Pakistani thermal printers ship with. |
| D10 | **Integration tests use real PostgreSQL** (Testcontainers). Fast unit tests cover the pure domain rules. | The brief requires it. Tailor Book's SQLite tests can't prove locking, uniqueness or concurrency. |
| D11 | Money is `numeric(18,2)` in PKR, with no floats anywhere. Timestamps are stored as UTC `timestamptz`, and business dates as `date` in the branch's timezone (default `Asia/Karachi`). | Required by the brief. |
| D12 | **Product name:** "Restaurant POS" for now. All branding comes from the tenant (section 13). The product name appears only on the public site and the sign-in screen. | Easy to rename before launch. |
| D13 | **Windows is the only desktop target in release 1.** The MAUI project is set up so that an Android tablet build (waiter ordering at the table) can be added later. | This is the requested scope. Tailor Book already runs the same stack on Android. |

---

## 2. Solution structure

```
D:\Repos\restaurant
├── Restaurant.slnx                     single solution (VS 2026 / dotnet 10)
├── Directory.Build.props               net10.0, nullable, implicit usings, warnings
├── Directory.Packages.props            central package versions
├── PLAN.md  README.md
├── docs/DEPLOYMENT.md                  VM, DNS, Nginx Proxy Manager, secrets
├── docker/docker-compose.yml           dev PostgreSQL (localhost:5434)
├── devops/                             production images, compose, nginx, health check
├── scripts/                            blue-green deploy, restore, container entrypoint
├── .github/workflows/deploy.yml        test → copy → .env → deploy
├── src/
│   ├── api/
│   │   ├── Restaurant.Domain           entities, value objects, enums, rules (no dependencies)
│   │   ├── Restaurant.Contracts        DTOs/requests/enums shared with the clients (no dependencies)
│   │   ├── Restaurant.Application      use cases, validation, interfaces, permission catalog
│   │   ├── Restaurant.Infrastructure   EF Core + Npgsql, Identity stores, QuestPDF, file storage
│   │   └── Restaurant.Api              controllers, auth, SignalR hubs, middleware, health
│   └── clients/
│       ├── Restaurant.Client.Core      typed API client, session/auth, branding state, i18n, platform interfaces
│       ├── Restaurant.Client.UI        Razor class library: every screen and component, CSS design system
│       ├── Restaurant.Web              WebAssembly PWA host (nginx) + web platform services
│       └── Restaurant.Desktop          MAUI Blazor Hybrid Windows host + Windows platform services
└── tests/
    └── Restaurant.Tests                Unit/ (pure rules) + Integration/ (API + PostgreSQL via Testcontainers)
```

`Restaurant.Contracts` is the one project the brief didn't list. Tailor Book linked the API's DTO source
files into the client, which works but is fragile. A dependency-free contracts project gives the API and
both clients the same types without pulling EF Core into the browser.

**Dependency rule:**
- `Domain`, `Contracts` ← `Application` ← `Infrastructure` ← `Api`
- `Contracts` ← `Client.Core` ← `Client.UI` ← `Web` / `Desktop`
- The clients never reference `Domain`, `Application` or `Infrastructure`, so they can't touch the
  database or business rules. The desktop app never talks to PostgreSQL.

---

## 3. Architecture

```
 Windows counter PC                     Any browser
 ┌──────────────────────────┐           ┌──────────────────────────┐
 │ Restaurant.Desktop (MAUI)│           │ Restaurant.Web (WASM PWA)│
 │  └ Restaurant.Client.UI  │           │  └ Restaurant.Client.UI  │
 │  └ WindowsEscPosPrinter  │           │  └ BrowserPrinter        │
 └────────────┬─────────────┘           └────────────┬─────────────┘
              │ HTTPS + WSS                          │ same origin /api, /hubs
              ▼                                      ▼
   api.restaurant.dsnoopdoc.com            restaurant.dsnoopdoc.com
              │      Nginx Proxy Manager (TLS) on the VM   │
              ▼                                      ▼
        rp-api :8097  ◄──────── /api /hubs /uploads ── rp-web :8096 (nginx + static WASM)
        (ASP.NET Core: REST + SignalR /hubs/kitchen, /hubs/orders)
              │
        rp-postgres (private network only)
```

- **A modular monolith:** one API process, with feature folders per module (Tenancy, Staff, Menu, Deals,
  Sessions, Orders, Kitchen, Payments, Receipts, Inventory, Customers, Reports, Audit, Branding). No
  microservices, no message broker.
- **Real-time:** SignalR hubs in the API, with one group per branch and kitchen station. Every message
  is only a hint: clients always re-read persisted state from the API, so a missed message or a
  reconnect loses nothing.
- **Background work** uses `IHostedService` only where needed (refresh-token clean-up, low-stock digests).
  Every job runs inside an explicit tenant scope.

---

## 4. Multi-tenancy

**Model:** `Tenant` (the restaurant business) → `Branch` (one outlet) → `Register` (one cash drawer or
till). Almost every table has `TenantId`; branch-scoped tables also have `BranchId`.

**How a request is scoped:**
1. **Sign-in** issues a token for the user. If the user belongs to several tenants, they pick one
   (Tailor Book's *Select business* screen). The token is reissued with a `tid` (tenant) claim and a
   `mid` (membership) claim.
2. **Each request** passes through `TenantResolutionMiddleware`:
   - it loads the membership (cached for 60 s and evicted on any membership change);
   - it checks the membership is active and belongs to `tid`;
   - it builds `ITenantContext { TenantId, UserId, MembershipId, Permissions, AllowedBranchIds }`.
   - A revoked membership stops working within 60 s, without waiting for the token to expire.
3. **Branch:** the client sends `X-Branch-Id`. The middleware rejects it with 403 unless it is in
   `AllowedBranchIds`. Owners have every branch. Nothing in a URL or request body sets the tenant.
4. **EF Core global query filters** on every `ITenantOwned` entity:
   `e => e.TenantId == _tenant.TenantId`.
   - Branch-scoped reads also filter on the current branch in the application services.
   - The filter is a backstop. Services still scope their queries explicitly.
5. **A `SaveChanges` interceptor:**
   - stamps `TenantId` (and `BranchId` where it is missing) on inserts;
   - **throws** if an insert, update or delete touches an entity of another tenant;
   - stamps the audit fields (`CreatedBy`, `UpdatedAt`).
6. **Background jobs and reports** open an explicit `TenantScope.For(tenantId)`. There is no unscoped
   `DbContext` outside platform-admin code, and that code uses a separate `PlatformDbContext` view.
7. **Tests** (section 15, T1 and T2) prove that cross-tenant and cross-branch reads and writes return
   404 or 403 and leave the data unchanged.

**Platform administration** (create tenants, suspend tenants, support access) uses the `PlatformAdmin`
Identity role and separate `/api/platform/*` endpoints. A platform admin is never a member of a tenant
by default. Support access, when added, is an explicit, time-boxed, audited membership.

---

## 5. Staff, roles and permissions

- **Permissions are fixed strings in code**, for example `orders.create` or `sessions.close`.
  - Roles are per-tenant rows holding a set of permissions.
  - Controllers use `[RequirePermission(Permissions.Orders.Create)]`, which is a policy, never a role
    name check.
  - The UI hides what you can't do, but the server always enforces it.
- **Membership:** `StaffMembership` links a user to a tenant, one or more roles, and either all branches
  or specific ones. Owners can create custom roles from the permission catalog.
- **System roles** are seeded per tenant. They can be copied, but Owner can't be edited:

| Permission group | Owner | Manager | Cashier | Waiter | Kitchen | Inventory |
|---|---|---|---|---|---|---|
| tenant.manage, branches.manage, ownership.transfer | ✔ | | | | | |
| staff.manage, roles.manage | ✔ | staff.view | | | | |
| settings.manage (tax, service charge, payment methods, receipt, day rules) | ✔ | | | | | |
| branding.manage | ✔ | | | | | |
| menu.manage, deals.manage | ✔ | menu.availability, deals.view | | | | |
| sessions.start, sessions.close | ✔ | ✔ | own register | | | |
| sessions.reopen (audited correction) | ✔ | | | | | |
| orders.create, orders.edit | ✔ | ✔ | ✔ | ✔ (dine-in) | | |
| orders.send_to_kitchen | ✔ | ✔ | ✔ | ✔ | | |
| orders.cancel_request | ✔ | ✔ | ✔ | ✔ | | |
| orders.void (after kitchen), discounts.manual | ✔ | ✔ | | | | |
| payments.take, receipts.print | ✔ | ✔ | ✔ | | | |
| payments.refund | ✔ | ✔ | permitted refunds (limit) | | | |
| cash.movements (paid-out, cash-in) | ✔ | ✔ | ✔ (limit) | | | |
| kitchen.view, kitchen.update | ✔ | ✔ | | view | ✔ | |
| inventory.receive, inventory.count, inventory.wastage | ✔ | ✔ | | | | ✔ |
| inventory.adjust (incl. negative) | ✔ | ✔ | | | | with approval |
| customers.view, customers.manage | ✔ | ✔ | view | view | | |
| reports.sales, reports.financial | ✔ | sales only | own shift totals | | | |
| reports.inventory | ✔ | ✔ | | | | ✔ |
| audit.view | ✔ | | | | | |

- **Approval:** an action the user lacks permission for (for example a void) can be approved on the
  spot by a manager entering their PIN. The audit log records both the requester and the approver.
- **PIN sign-in:** shared counter PCs use a 4–6 digit staff PIN. It is hashed and rate-limited, and only
  works from a registered device after a full sign-in on that device. This keeps the counter fast
  without sharing passwords.
- **Account creation:** the owner signs up. Staff are invited by phone or email with a one-time setup
  link or code, or the owner creates them with a temporary password that must be changed. Password
  reset uses Identity tokens.

---

## 6. Domain model

Every table also has `Id` (uuid), `TenantId`, `CreatedAt`, `CreatedBy`, `UpdatedAt` and `RowVersion`
(xmin concurrency token) unless noted.

**Tenancy and staff**
- `Tenant`: name, legal name, NTN/STRN (tax registration), currency (PKR), status.
- `Branch`: name, address, phone, timezone (`Asia/Karachi`), `DayStartHour` (default 06:00), tax
  settings, service charge, order-number prefix, receipt overrides.
- `Register`: branch, name, is default, printer name, cash drawer enabled.
- `AppUser` (Identity `IdentityUser<Guid>`): name, phone, PIN hash.
- `StaffMembership`: user, tenant, status, all branches or a `MembershipBranch` list, and `MembershipRole`s.
- `TenantRole` and `RolePermission`.
- `RefreshToken`, `RegisteredDevice`.

**Settings**
- `TaxRate`: name, percent, inclusive or exclusive, applies to dine-in, takeaway or delivery. One rate
  can apply to cash and another to card payments, because some provinces tax them differently. The
  rates are configurable data, never hard-coded.
- `ServiceCharge`: percent, order types.
- `PaymentMethodConfig`: Cash, Card (external machine), Bank transfer, JazzCash, Easypaisa, Custom. Each
  has `IsCash` and `RequiresReference`.
- `BusinessDayPolicy`:
  - one open session per branch (default) or per register;
  - the cash-difference threshold that requires a reason;
  - insufficient-stock behaviour (Allow / Warn / Block).

**Menu**
- `MenuCategory`: sort order, colour (for touch tiles), active.
- `MenuItem`: category, name, Urdu name, description, image, SKU, base price, tax category, kitchen
  station, active, and type `Simple | HasVariants`.
- `MenuItemVariant`: Regular, Large, Family; price; SKU.
- `ModifierGroup`: min and max selections, required. `ModifierOption`: name, price delta.
  `MenuItemModifierGroup` links them.
- `BranchMenuItem`: per-branch availability, price override and state
  `Available | OutOfStock | TemporarilyUnavailable`.
- `KitchenStation`: Kitchen, Grill, Drinks, Tandoor. There is one default station.

**Deals and discounts**
- `Deal`:
  - type `FixedPriceBundle | BuyXGetY | PercentOff | AmountOff`;
  - scope (order or item);
  - start and end dates, day-of-week and hour windows;
  - branches;
  - whether it can be combined with other deals;
  - an optional coupon code and usage limits.
- `DealItem`: menu item or variant, or a "choice group" (for example "any 1 drink").
- `DiscountReason`: preset reasons for manual discounts.

**Tables**
- `DiningArea` (Hall, Family, Rooftop) and `RestaurantTable` (number, seats, state
  `Available | Occupied | Reserved | Cleaning`).

**Sessions and cash**
- `BusinessSession`:
  - branch, `BusinessDate` (date), `StartedAt`/`StartedBy`;
  - `ClosedAt`/`ClosedBy`, `Status Open | Closed`;
  - closing totals snapshot (JSON plus key columns), notes;
  - `ReopenedAt`/`ReopenedBy`/`ReopenReason`.
- `RegisterSession`:
  - business session, register, opened by, `OpeningCash`;
  - `ExpectedCash`, `ActualCash`, `CashDifference`, `DifferenceReason`;
  - `Status`, closed by and at.
- `CashMovement`: register session, type `OpeningFloat | CashSale | CashRefund | PaidOut | CashIn`,
  amount, reason, reference (payment or refund), actor.

**Orders**
- `Order`:
  - branch, business session, `OrderNumber`, type `DineIn | Takeaway | Delivery`, source `Counter | Waiter`
    (later `Foodpanda`, `Website`);
  - table, covers (guests), customer, created by, waiter;
  - `Status`, `PaymentStatus Unpaid | PartiallyPaid | Paid | Refunded | PartiallyRefunded`;
  - `Subtotal`, `DiscountTotal`, `TaxTotal`, `ServiceCharge`, `RoundingAdjustment`, `Total`, `PaidTotal`;
  - `ClientRequestId` (unique per tenant), notes, delivery address snapshot.
- `OrderLine` is a snapshot:
  - menu item and variant IDs (for reporting only), plus `NameSnapshot`, `VariantSnapshot`, `SkuSnapshot`;
  - `UnitPrice`, `Quantity`, `ModifiersTotal`, `DiscountAmount`;
  - tax rate, amount and inclusive flag;
  - `LineTotal`, notes, `KitchenStationId`, `Status Active | Voided`, void reason and approver;
  - `DealApplicationId`.
- `OrderLineModifier`: snapshot of the option name and price.
- `OrderDealApplication`: snapshot of the deal name, rule, original amount, discount and the lines it
  covers.
- `OrderStatusHistory`: from status, to status, actor, reason, timestamp.
- `OrderSequence`: `(BranchId, BusinessDate)` → `LastNumber`. It's incremented under a row lock, and
  the number looks like `T-042`.

**Kitchen**
- `KitchenTicket`:
  - order, station, `TicketNumber` (round 1, 2… as items are added);
  - `Status New | Accepted | Preparing | Ready | Served | Cancelled`;
  - `SentAt`, `AcceptedAt`, `ReadyAt`, `BumpedAt`.
- `KitchenTicketItem`: order line, quantity, name and modifiers snapshot, notes, item status, and a
  `ChangeType New | Added | Cancelled | QtyChanged`.

**Payments**
- `Payment`:
  - order, register session, method, `Amount`, `Tendered`, `ChangeGiven`, reference;
  - `ClientRequestId`, status `Completed | Reversed`.
- `Refund`: payment, amount, reason, approved by, method, register session, `ClientRequestId`.
- `Receipt`: order, receipt number, kind `Sale | Refund`, `PrintCount`, first printed at.
- `ReceiptPrint`: who, when, `IsReprint`, printer or template.

**Inventory**
- `UnitOfMeasure`: g, kg, ml, l, pc, bottle. `UnitConversion`: from, to, factor (explicit, per item where
  needed, for example "1 tray = 30 pc").
- `InventoryItem`: name, base unit, reorder level, `CostingMethod = WeightedAverage`, `AverageCost`,
  `OnHand` (cached per branch, in `InventoryBalance`).
- `Recipe`: per menu item or variant, yield. `RecipeIngredient`: inventory item, quantity, unit (converted
  to the base unit at save time). Modifier options can also have recipes ("Extra cheese = 20 g cheese").
- `StockMovement`, the ledger:
  - branch, item, `Type Opening | Receipt | SaleConsumption | SaleReversal | Wastage | Adjustment | CountVariance | TransferIn | TransferOut`;
  - signed quantity in the base unit, unit cost, value;
  - `SourceType` + `SourceId`, and a unique `IdempotencyKey`.
- `StockReceipt` and lines: supplier, invoice number, received by. `StockAdjustment`: reason, approved by.
  `Wastage`: reason.
- `StockCount` and lines: status `Draft | Submitted | Approved`, counted vs system quantity, variance
  posted as `CountVariance`.
- `Supplier`. `PurchaseOrder` is optional and comes in a later phase.

**Customers**
- `Customer`: name, phone (unique per tenant when present), notes, addresses, marketing opt-in.
- `TotalSpend` and `OrderCount` are computed in queries, not stored.

**Branding, receipts and files** (copied from Tailor Book)
- `BrandingSettings`: colours, logo, display name, tagline, address, phone, WhatsApp, receipt header and
  footer, social links, default receipt template.
- `ReceiptTemplate`: system templates `thermal_80mm`, `thermal_58mm`, `a4_invoice`, `kitchen_80mm`.
- `FileAsset`.

**Audit**
- `AuditLog`: tenant, branch, actor, approver, action, entity type and ID, reason, before and after JSON,
  IP and device, timestamp. It's append-only: there is no update or delete endpoint, and the DB user
  gets no `UPDATE`/`DELETE` grant on it in production.

---

## 7. Business day and register sessions

**Start day** (permission `sessions.start`):
1. Pick the branch and enter the opening cash.
2. This creates the `BusinessSession`. `BusinessDate` is the branch's local date at `StartedAt`, shifted
   by `DayStartHour`: a session started at 02:00 counts toward the previous date only if the owner sets
   it up that way. Normally the start date is the business date.
3. It also opens the default register's `RegisterSession` and writes a `CashMovement(OpeningFloat)`.
4. A partial unique index `(BranchId) WHERE Status = 'Open'` makes overlapping sessions impossible, even
   with two clicks at the same moment. With the per-register policy, the index is
   `(RegisterId) WHERE Status = 'Open'`.

**While open:**
- Every order references the open `BusinessSession`. Creating an order with no open session returns
  409 *Start the day first*.
- Timestamps stay real UTC. A session that runs past midnight keeps its business date.
- The header shows the session status, business date and elapsed time.

**Close day** (permission `sessions.close`), as a three-step wizard:
1. **Blockers:** open orders, unpaid or partly paid orders, kitchen tickets not served or cancelled, and
   other registers still open. Each one links to its fix (decision D7).
2. **Cash count:** enter the counted cash, with an optional denomination counter (5000/1000/500/100/50/
   20/10 notes and coins). The screen shows `ExpectedCash`, `ActualCash` and `Difference`. A reason is
   required when |difference| exceeds the policy threshold.
3. **Summary and confirm:**
   - sales, discounts, refunds, voids, taxes, service charge, payments by method, cash movements;
   - the full formula for expected cash;
   - "Close day" commits one transaction that stores the totals snapshot, closes the register and
     business sessions, and writes an audit row.

**Expected cash:**
```
ExpectedCash = OpeningCash + CashSales + CashIn − CashRefunds − PaidOuts
CashDifference = ActualCash − ExpectedCash
```
(Cash sales are net of the change given: `Tendered − Change`.)

- **Closed means immutable.** The API rejects any write that targets a closed session.
- **Reopen** needs `sessions.reopen`, a reason, and creates an audit row. Corrections are new compensating
  records (a refund, a cash movement), never edits.
- After a reopen, closing again records a new totals snapshot. The old snapshot is kept in the audit log.

---

## 8. Orders, kitchen and payments

**State machine (order status):**
```
Draft ─► Confirmed ─► SentToKitchen ─► Preparing ─► Ready ─► Served / HandedOver ─► Completed
  │          │              │              │          │
  └─ Cancelled (no kitchen) └──────── Voided (needs orders.void + reason) ─────────┘
```
- **Allowed transitions** live in one `OrderStateMachine` in the Domain project, unit-tested as a table.
  Kitchen statuses (`Preparing`, `Ready`) are driven by the kitchen tickets.
- **Payment status is separate** (D5 and the brief). An order can be `Preparing` and `Unpaid`.
  `Completed` requires `Paid` and every ticket `Served`.
- **Adding items** to an open dine-in order creates a new kitchen ticket round with only the new lines.
- **Changing lines after kitchen submission** requires a reason. Removing a sent line is a *void line*:
  the line is kept with `Voided`, a kitchen `Cancelled` change is sent, and the stock is reversed.

**Server-side pricing:**
- The client sends only IDs, quantities, modifier option IDs, notes and requested deals or coupons.
- The server loads current prices, availability and deals, calculates every number and returns the
  priced order. Any total from the client is ignored.
- **Calculation order:**
  1. line base and modifiers;
  2. deals (best eligible deal per line set, respecting the combinable flag);
  3. manual discount (permission and limit);
  4. service charge (dine-in);
  5. tax (inclusive or exclusive);
  6. optional rounding to the nearest whole rupee, recorded as `RoundingAdjustment`.
- **Unit tests** cover this calculation with known worked examples (T6).

**Duplicate protection:**
- The client generates `ClientRequestId` (uuid) when the cart opens and reuses it on every retry. A
  unique index plus a check makes the second request return the first order (T7).
- Payments and refunds work the same way.
- The UI also disables the button while a request is in flight.

**Kitchen tickets** are created in the *same transaction* that moves the order to `SentToKitchen`
(T8 and T15):
- one ticket per kitchen station that has lines;
- a SignalR `TicketsChanged(branchId, stationId)` message after the commit.

**The Kitchen Display screen:**
- It loads the persisted tickets, so a refresh or reconnect shows exactly the same state.
- It shows the order number, type, table, elapsed timer, items, modifiers and notes. It never shows
  customer or price data, and the API returns none to the `kitchen.*` permissions.
- **Colours:** New is blue, Late (> X min, set per branch) is red, Ready is green.
- The touch buttons are Accept → Preparing → Ready → Bump. The screen has optional sound and a station
  filter.
- **Change notices:** quantity changes and voids after submission show as a highlighted change ticket
  ("CANCEL 1 × Zinger") until the kitchen acknowledges them.

**Payments:**
- Split tender is supported (several `Payment` rows).
- Cash records the amount tendered and the change. Card, JazzCash and Easypaisa are *recorded* methods
  with an optional reference. There is **no gateway integration** (none was selected).
- Overpayment is allowed only for cash, as change.
- **Refunds:**
  - they create a `Refund` and, for cash, a `CashMovement(CashRefund)`;
  - they need `payments.refund` (or a manager PIN above the cashier limit) and a reason;
  - they never delete or edit a payment;
  - a mistaken payment is reversed with a compensating record.

---

## 9. Inventory

- **The ledger is the source of truth.** `InventoryBalance.OnHand` is a cache updated in the *same
  transaction* as each `StockMovement`. A nightly self-check, and a test, recompute it from the ledger
  and flag any drift.
- **Deduction on `SentToKitchen`** (D6):
  - For each line it expands the recipe: variant recipe, else item recipe, plus modifier recipes, times
    quantity. A deal line expands each included item.
  - It writes one `SaleConsumption` movement per ingredient with the idempotency key
    `sale:{orderLineId}:{ingredientId}` (unique), so a retry can't deduct twice (T9).
- **Reversal** on void or cancellation after deduction writes `SaleReversal` with the key
  `rev:{orderLineId}:{ingredientId}`. A second cancel can't double-reverse (T10).
- **Insufficient stock** follows the branch policy:
  - *Allow* (default): goes negative and appears on the low-stock report;
  - *Warn*: the cashier sees a warning;
  - *Block*: returns 409 with the missing items.
  - A manual negative adjustment needs `inventory.adjust` plus approval.
- **Units:** every recipe quantity and movement is stored in the item's *base* unit. Conversions are
  explicit rows, and the UI shows a conversion preview ("250 g = 0.25 kg").
- **Costing:** weighted-average cost, updated on each `Receipt` (the same rule as Tailor Book's
  `StockLedger`). Inventory value is `OnHand × AverageCost`. Reports say "Estimated, weighted average"
  and never call sales "profit". Gross margin, if added, is labelled as recipe-cost based.
- **Stock count:** take a snapshot of the system quantities, enter the counts (they can be saved
  partway), submit, then approve. Approval posts `CountVariance` movements and records variance value.

---

## 10. Receipts and printing

**One resolved model, three renderers.** This is the Tailor Book approach (`ReceiptDocument`, section 13).
The API's `ReceiptService` builds a `ReceiptDocument`:
- branding fallbacks are applied;
- times are converted to the branch's timezone;
- the logo bytes are loaded;
- the lines, modifiers, deals, taxes, service charge, payments, tendered amount, change, cashier and
  business date are filled in.

It then renders the document in one of three ways:

| Renderer | Used by | Output |
|---|---|---|
| `QuestPdfReceiptRenderer` | Web and desktop "Download / Share PDF", A4/A5 invoice, WhatsApp | PDF (80 mm, 58 mm, A4) |
| `ReceiptPreview.razor` + print CSS | Web app printing, and the live preview in Branding settings | HTML (`@page { size: 80mm auto }`) → `window.print()` |
| `EscPosReceiptRenderer` (in `Client.Core`) | Desktop silent printing | ESC/POS bytes: text, bold and double-height totals, optional raster logo, cut, drawer kick |

**Rules:**
- The kitchen ticket (KOT) is the same pipeline with `kitchen_80mm`: large item text, no prices.
- **Reprints:** the API endpoint `POST /api/orders/{id}/receipt/print` logs a `ReceiptPrint` and returns
  the document with `IsReprint = true` from the second print on. The receipt then shows a
  **"DUPLICATE — REPRINT #n"** banner. Printing never creates a sale, payment or stock movement (T11).
- **Contents:** branch name and address, NTN/STRN, receipt and order number, business date and time,
  cashier, table, items with modifiers, deal lines (original price, discount), subtotal, service charge,
  tax breakdown, rounding, total, payments by method, tendered and change, refund references, footer.
- **58 mm:** the same document with a narrower column layout. The ESC/POS renderer wraps at 32 characters
  instead of 48.
- **Urdu text on thermal printers:** most cheap ESC/POS printers have no Urdu font. For Urdu item names
  the desktop app renders that line to a bitmap (raster mode, GS v 0) with Windows GDI, which joins the
  letters and lays them out right to left. Only lines with non-ASCII text become bitmaps; the rest stay
  text, so receipts stay fast.

**Desktop printing** (`IReceiptPrinter`, Windows implementation):
- `WindowsRawPrinter` opens the printer named in the register settings through winspool
  (`OpenPrinter`/`StartDocPrinter` with the `RAW` datatype/`WritePrinter`).
- It works with any printer that has a Windows driver installed, including the "Generic / Text Only"
  driver.
- Settings: the printer list (from `EnumPrinters`), a test print, "open cash drawer on cash payment" and
  auto-print the KOT.
- **Fallback:** if raw printing fails, it prints the PDF through the default viewer, or the user opens it.
- **Documented in `docs/PRINTING.md`:**
  - driver set-up and USB vs network (port 9100) printers;
  - how to find the printer name;
  - known printer models;
  - that the web app can only print through the browser dialog (Chrome kiosk `--kiosk-printing` makes
    that silent).

---

## 11. Clients

**Shared screens** (`Restaurant.Client.UI`). Every screen is written once and runs in both hosts:
- POS (order entry), table map, order list and details, payment dialog
- kitchen display
- business day (start, status, close wizard), cash movements
- menu (categories, items, variants, modifiers, availability), deals
- inventory (items, recipes, receipts, counts, wastage, adjustments), customers
- reports, staff and roles, settings (branch, tax, payment methods, receipt, branding, printers on desktop)

**The POS screen** is built for speed:
- the category rail on the left, large item tiles in the middle and the cart on the right;
- order type (Dine-in / Takeaway / Delivery) and table picker at the top;
- search box with `/` or `F3`; the customer picker is optional;
- deal suggestions ("Add fries + drink = Zinger Meal, save Rs 150");
- Pay button showing the total, with no nested cards or animations.

**Keyboard shortcuts** (desktop and web):

| Shortcut | Action |
|---|---|
| `F2` | New order |
| `F3` or `/` | Search |
| `F4` | Table |
| `F8` | Send to kitchen |
| `F9` | Pay |
| `F10` | Print |
| `+` / `−` | Change quantity |
| `Del` | Remove line |
| `Esc` | Close dialog |
| `Ctrl+Enter` | Confirm |

The shortcuts are listed on a `?` overlay.

**Platform interfaces** (`Restaurant.Client.Core/Platform`):

| Interface | Web | Desktop (Windows) |
|---|---|---|
| `IReceiptPrinter` | `BrowserReceiptPrinter` (print dialog) | `WindowsRawPrinter` (ESC/POS) + PDF fallback |
| `ICashDrawer` | not supported | ESC/POS drawer kick through the receipt printer |
| `IKeyValueStore` | localStorage | file store in `%LOCALAPPDATA%` (Tailor Book `FileKeyValueStore`) |
| `ISecureStore` | sessionStorage (refresh token: memory + opt-in remember) | Windows Credential Locker / DPAPI |
| `IConnectivity` | `navigator.onLine` + API probe | `Connectivity` + API probe |
| `IFileShareService` | download | save to Downloads and open |
| `ISoundPlayer` | HTML audio | HTML audio inside the WebView |
| `IPlatformInfo` | `IsDesktop = false` | `IsDesktop = true`, device ID |

**Connection handling:**
- The `ApiClient` treats 502/503 and timeouts as `NetworkException` (a Tailor Book lesson).
- A banner shows "Reconnecting…", and writes retry with the same idempotency key.
- SignalR uses automatic reconnect plus a full re-read when it reconnects.

**Offline statement:** release 1 needs a connection to take orders (D8). When offline, the app shows
the cached menu and the current order, and blocks Send and Pay with a clear message. Nothing is queued
silently.

**Language:** English and Urdu with RTL, using Tailor Book's `Localizer` and `translations.json`.
Receipts are printed in the branch's chosen language.

---

## 12. API surface (outline)

`/api/auth`
- `register-owner`, `login`, `pin-login`, `refresh`, `logout`, `select-tenant`,
  `forgot-password`, `reset-password`, `accept-invite`

`/api/platform` (PlatformAdmin)
- `tenants`, suspend/activate

`/api/tenant`
- settings, branches, registers, tax rates, service charge, payment methods, day policy
- `branding` (+ `logo`), receipt templates

`/api/staff`
- members, invites, roles, `permissions` (catalog)

Menu and deals
- `/api/menu/categories`, `/api/menu/items` (variants, modifiers, recipes, availability)
- `/api/modifier-groups`, `/api/deals`, `/api/deals/evaluate`

Sessions
- `/api/sessions` (current, start, close-preview, close, reopen, history)
- `/api/sessions/{id}/cash-movements`

Orders
- `/api/orders`: price-preview, create, add-lines, void-line, send-to-kitchen, transition, cancel,
  void, list, detail
- `/api/tables` (+ state)

Kitchen
- `/api/kitchen/tickets`, `/api/kitchen/tickets/{id}/status`
- the hub `/hubs/kitchen`

Payments and receipts
- `/api/orders/{id}/payments`, `/api/payments/{id}/refunds`
- `/api/orders/{id}/receipt` (document JSON, pdf, print-log)

Inventory and customers
- `/api/inventory/items`, `units`, `receipts`, `adjustments`, `wastage`, `counts`, `balances`, `movements`
- `/api/customers` (+ order history)

Reports, audit and health
- `/api/reports/sales`, `orders`, `products`, `payments`, `session/{id}`, `inventory`
- `/api/audit`
- `/health` (API + DB)

Errors use RFC 7807 problem details. Validation errors return a field map, which is what Tailor Book's
`ValidationAppException` produces.

---

## 13. Branding and receipts: the Tailor Book approach

| Tailor Book | Restaurant | Change |
|---|---|---|
| `Domain/Entities/BrandingSettings.cs` (one-to-one with the business) | `BrandingSettings` (one-to-one with the tenant) + per-branch address and phone overrides | Adds `NtnNumber`, `StrnNumber`, `ReceiptLanguage` |
| `ReceiptTemplate` system templates + per-business default | Same | Templates `thermal_80mm`, `thermal_58mm`, `a4_invoice`, `kitchen_80mm` |
| `FileAsset` + `IFileStorageService` (`LocalFileStorageService`, `/uploads`) | Same | Menu item images use it too |
| `BrandingService` (get, update, logo upload with size and type checks) + `BrandingController` | Same endpoints under `/api/tenant/branding` | |
| `Features/Receipts/Models/ReceiptDocument.cs`: a resolved document, renderers only lay it out | Same rule | Adds the tax breakdown, service charge, tendered and change, cashier, table, business date, `IsReprint`, kitchen variant |
| `Infrastructure/Receipts/QuestPdfReceiptRenderer.cs` (Noto Naskh fonts for Urdu, copied into the image) | Same | Adds a 58 mm layout; the Dockerfile font step is copied as-is |
| `Client.Core/BrandingState.cs`: CSS variables `--tb-primary`, `--tb-accent`… applied at the app root | `BrandingState` with `--rp-*` variables | The whole UI is themed by the tenant, never hard-coded |
| `Client.UI/Components/ReceiptPreview.razor` + `Settings/BrandingSettings.razor` (colour fields, logo picker, live preview in a split layout) + `ReceiptSample.razor` | Same screens | The preview shows the 80/58 mm toggle and a kitchen-ticket sample |
| `Format.WithAlpha` (hex → alpha, otherwise `color-mix`) | Same helper | |
| `tailorbook.css` design system (`tb-` classes) | `restaurant.css` (`rp-` classes) | Adds the POS and kitchen layouts and larger touch targets (min 48 px) |

---

## 14. Reports (formulas shown on each report)

| Figure | Formula |
|---|---|
| Gross sales | Σ line (unit price + modifiers) × qty, for non-voided lines on non-voided orders |
| Discounts | Σ deal discounts + manual discounts |
| Net sales | Gross sales − discounts (before tax and service charge) |
| Tax | Σ line tax |
| Service charge | Σ order service charge |
| Total billed | Net sales + tax + service charge ± rounding |
| Payments collected | Σ completed payments (per method) |
| Refunds | Σ refunds (per method) |
| Voids | Count and value of voided lines and orders, with reasons |
| Average order value | Net sales ÷ number of non-voided orders |
| Expected cash | See section 7 |
| Inventory value (estimated) | Σ on hand × weighted-average cost |

**Filters:** business date or range, session, branch, order type, cashier, payment method.

**Reports:**
- sales summary;
- orders by status, type, hour and day;
- cancelled and voided orders with reasons;
- best sellers;
- quantity by item and by category;
- deal performance;
- payments by method;
- outstanding balances;
- session closing report (printable on 80 mm);
- current stock, low stock, receipts and adjustments, wastage, consumption, inventory value.

**Consistency:** every report is a query over the same ledger tables. A test seeds known orders and
asserts each figure (T14).

---

## 15. Testing and acceptance criteria

`tests/Restaurant.Tests`:
- `Unit/`: state machine, pricing and deals, cash formula, unit conversion, business-date calculation.
- `Integration/`:
  - `WebApplicationFactory<Program>` + Testcontainers PostgreSQL;
  - one container per test run, a fresh database per test class (template DB copy for speed);
  - auth helpers issue tokens for seeded users of two tenants.

| # | Acceptance test | Level |
|---|---|---|
| T1 | Tenant A cannot read or update or delete tenant B's menu, orders, customers, sessions, stock (404, data unchanged) | Integration |
| T2 | Staff without a branch assignment get 403 for that branch | Integration |
| T3 | Only `sessions.start` / `sessions.close` holders can start or close; the second concurrent start fails | Integration |
| T4 | A session started at 10:00 Mon closes 02:00 Tue with business date Monday | Unit + Integration |
| T5 | Creating an order in a closed or no session returns 409 | Integration |
| T6 | Deal and discount calculations; an old order keeps its prices after a price or deal edit | Unit + Integration |
| T7 | Same `ClientRequestId` twice creates one order or payment (also concurrently) | Integration |
| T8 | Kitchen tickets are persisted and re-read identically (simulated reconnect) | Integration |
| T9 | Stock deducted exactly once on send-to-kitchen, including retries and status changes | Integration |
| T10 | Cancel or void reverses once; a second cancel doesn't double-reverse | Integration |
| T11 | Reprint logs `ReceiptPrint` and creates no order, payment or stock movement | Integration |
| T12 | Close-day expected cash = formula over seeded sales, refunds, cash-in, paid-outs | Unit + Integration |
| T13 | Refund and void require permission or approval | Integration |
| T14 | Report figures equal sums over the seeded orders and payments | Integration |
| T15 | A failure mid-operation (forced exception) leaves no partial order, payment, ticket or stock rows | Integration |

CI (GitHub Actions) runs every test on each push. Docker is available on `ubuntu-latest` for
Testcontainers.

---

## 16. Deployment (identical pattern to Tailor Book)

| | Value |
|---|---|
| VM folder | `/root/restaurant` (never `/root/app`, which belongs to dsnoopdoc) |
| Compose project | `restaurant`. Services `rp-postgres`, `rp-api`, `rp-web`; containers `restaurant_*` |
| Ports | `rp-web` → host `8096`; `rp-api` → `127.0.0.1:8097` (Tailor Book uses 8098/8099, dsnoopdoc 8088) |
| Networks | `restaurant_internal` (private) + external `www_default` for `rp-web` only |
| DNS | `A restaurant` → VM IP, `A api.restaurant` → VM IP |
| Nginx Proxy Manager | `restaurant.dsnoopdoc.com` → `127.0.0.1:8096`; `api.restaurant.dsnoopdoc.com` → `127.0.0.1:8097`. **Websockets ON** (SignalR), SSL + Force SSL, `client_max_body_size 8m;` |
| GitHub secrets | `RP_POSTGRES_PASSWORD`, `RP_JWT_SIGNING_KEY`, and the existing `SSH_HOST`, `SSH_USER`, `SSH_PRIVATE_KEY` |
| Pipeline | test (incl. Testcontainers) → copy to VM → write `.env` → `scripts/deploy-production.sh` (`dotnet restore` + EF migrations → build → blue-green standby → smoke tests → promote) |
| Backups | nightly `pg_dump` of `restaurant` to `/root/backups/restaurant` (14 daily + 8 weekly kept), with optional copy off the VM; restore steps in `docs/DEPLOYMENT.md` |
| Desktop app | `dotnet publish src/clients/Restaurant.Desktop -f net10.0-windows10.0.19041.0 -c Release`. Packaged with Inno Setup by `installer/build-installer.ps1`; the app shows an update notice from `/api/desktop-release` |

The desktop Release build calls `https://api.restaurant.dsnoopdoc.com`. Debug builds call
`http://localhost:5080`, which can be overridden with `RESTAURANT_API_URL`. The web app calls `/api` on
its own domain.

---

## 17. Phases

Each phase ends with:
- `dotnet build Restaurant.slnx` clean;
- all tests green;
- the README checklist updated;
- a "done / remaining / limitations" note.

The desktop app builds and runs at the end of **every** phase.

### Phase 0 — Scaffold (done with this plan)
- [x] Solution, the 10 projects, central package versions, project references
- [x] API skeleton: Serilog, health endpoint (API + DB), JWT auth wiring, CORS, forwarded headers, ProblemDetails
- [x] EF Core `RestaurantDbContext` + design-time factory (`RESTAURANT_CONNECTION_STRING`)
- [x] Web PWA shell and Desktop MAUI Windows shell, both rendering the shared `Client.UI` layout
- [x] Platform interfaces in `Client.Core`
- [x] Dev PostgreSQL (`docker/`), production Dockerfile, compose, nginx (with `/hubs` websockets), blue-green deploy scripts, GitHub workflow
- [x] Tests project with a first API test

### Phase 1 — Backend foundation (done)
- [x] Domain base types, tenant and branch entities, Identity (`AppUser`), membership, roles, permissions
- [x] Tenant resolution middleware, global query filters, the tenant-guard `SaveChanges` interceptor (also stamps `CreatedBy`/`UpdatedAt`/`UpdatedBy`), audit log written in the same transaction as each change
- [x] Auth: owner sign-up (creates tenant, first branch, default register, system roles, branding, payment methods), login by email or mobile, lockout, refresh rotation with reuse detection, select tenant, password reset and change, invites, registered devices and PIN login
- [x] Settings: branch (timezone, day start, service charge, rounding, session scope, cash threshold, stock policy, late-ticket minutes), registers, tax rates, payment methods
- [x] Branding, receipt templates and file storage ported from Tailor Book
- [x] Initial migration and seed data (system receipt templates, PlatformAdmin role; first platform admin from configuration)
- [x] Testcontainers fixture; tests T1, T2, auth, permissions, invites/PIN, branding, platform (46 tests)

Notes and deviations:
- The business-day policy (D5/D7 settings) is stored as columns on `Branch`, not a separate `BusinessDayPolicy` table.
- Permissions are re-read per request (cached 60 s, cleared on any staff, role or branch change), not stored in the token.
- No email/SMS provider yet: forgot-password only logs the request (and the token in Development). Owners reset staff passwords by issuing a new invite code; that never resets the password of someone who also works for another restaurant.
- Not built yet: ownership transfer, optimistic concurrency tokens (added with orders in Phase 3), receipt rendering (`ReceiptDocument` + QuestPDF, Phase 3, when there are orders to print).

### Phase 2 — Clients and shared UI (done)
- [x] Ported `ApiClient` (token refresh, X-Branch-Id), `SessionStore`, `Localizer` (English + Urdu, RTL), `BrandingState`, `Format`, Ionicons; new `UserContext` (permissions, branches) and `DeviceRegistration`; `rp-` design system
- [x] Sign-in, sign-up, accept invite, forgot/reset password, choose restaurant, branch switcher, PIN sign-in and lock screen (manual lock + idle auto-lock), app layout with permission-aware navigation
- [x] Settings: my account (language, PIN, password, auto-lock), business, branches + operating rules + registers, taxes, payment methods, branding with live 80/58 mm receipt preview, counter devices, activity log
- [x] Staff (add with one-time invite code, edit roles/branches, disable, new code) and roles (permission editor that only offers what the editor holds)
- [x] Desktop: file store, DPAPI secure store (works unpackaged), connectivity, opens maximised, full screen (F11)
- [x] Verified in the browser (sign-up, branding re-theme, invite → cashier view, Urdu RTL, phone width) and in the desktop app (sign-in, device registration, lock, PIN unlock as another user)

Notes:
- Unbuilt sections stay in the navigation with a "Soon" badge and open a "planned for phase N" page.
- Invite codes are typed, never put in links, so they can't leak through URLs or logs.

### Phase 3 — Core POS (done)
- [x] Menu: categories (colour, Urdu name), items, sizes, add-on groups with min/max, kitchen stations, per-branch availability and price
- [x] Business day and register sessions with the three-step close (blockers → cash count with denominations → summary); reopen with a reason (T3, T4, T5, T12)
- [x] Tables and dining areas; a table holds one open order, and tapping a busy table continues it
- [x] POS screen (F2/F3/F8/F9), server-side pricing (`OrderPricer`), order numbers per branch and business date, idempotent create/add/pay (T6, T7)
- [x] Kitchen tickets per station, written in the same transaction; SignalR hints with reconnect; kitchen display with late highlighting, sound and change notices (T8)
- [x] Split payments, cash tendered and change, refunds; voids, refunds and manual discounts with manager PIN approval (T13)
- [x] Receipts: one resolved document; PDF (QuestPDF, 80/58 mm/A4, kitchen ticket), browser print, desktop ESC/POS with paper cut and cash drawer, reprints marked (T11)
- [x] 73 tests (pricing, ESC/POS, flows against PostgreSQL); checked in the browser and the desktop app

Notes and deviations:
- One business day per branch always; with several registers each drawer has its own register session.
- Table states are derived (occupied while an order is open). Reservations and cleaning states are not built.
- The cash/card tax split follows the first payment's method (an order is priced as cash until then).
- Service charge is calculated on the pre-tax amount and is not itself taxed.
- Customers are a name and phone on the order (profiles were added in Phase 4).
- Thermal printing was text-mode ESC/POS at first (Urdu printed as "?"); Urdu lines are printed as bitmaps since the deferred-items batch after Phase 5.

### Phase 4 — Inventory and deals
- [x] Units and conversions, inventory items, recipes (item, variant, modifier)
- [x] Stock receipts, wastage, adjustments, counts; the ledger and balance cache
- [x] Deduction on send-to-kitchen, reversal on void or cancel (T9, T10)
- [x] Deals engine (bundle, BOGO, % and amount, time windows, coupons, combinability) (T6)
- [x] Customers with order history

Phase 4 notes:
- On hand is always the sum of the `StockMovement` ledger (indexed by branch and item), so there is no
  balance cache to drift and no nightly self-check is needed. `InventoryBalance` keeps only the
  weighted-average cost per branch, updated on each receipt.
- Ledger keys: `sale:{line}:{item}`, `rev:{line}:{item}`, `receipt:{id}:{n}`, `count:{id}:{item}`,
  `adj:{clientRequestId}`, under a unique (tenant, key) index.
- A size's own recipe replaces the item's recipe; add-on recipes are added on top. Cancelling a held
  order needs no reversal, because nothing was deducted before send-to-kitchen.
- Combo lines carry their normal prices, and the `OrderDealApplication` holds the discount (normal price
  minus combo price). Add-ons stay charged, and voiding one combo line voids the whole combo.
  Automatic deals are frozen on the order as `DealRulesJson` when it is placed and are re-evaluated on
  the non-combo lines at every repricing.
- Customer search takes the phone or name in a POST body, so phone numbers stay out of URLs and logs.
  A phone number entered at the POS links the order to a profile, creating one if it is new.
  "Remove personal data" anonymises the profile and its orders.
- Not done yet: low-stock alerts beyond the badge, purchase orders and suppliers as records, and stock
  transfers between branches.

### Phase 5 — Reporting and release
- [x] Sales, orders, products, payments, session and inventory reports (T14)
- [x] Transaction-failure tests (T15), concurrency tests
- [x] Security review: rate limits, lockout, headers, logging hygiene
- [x] Backups cron, restore drill, monitoring
- [x] `docs/PRINTING.md`, desktop installer (Inno Setup) + update check
- [ ] Production deploy, smoke test on a real counter PC and thermal printer (needs the VM, GitHub secrets and a counter PC)

Phase 5 notes:
- Reports live at `/api/reports/{sales,orders,products,payments,inventory}` with one filter (business-date
  range, session, order type, cashier, payment method, `allBranches`). They read the same order, payment,
  refund and stock rows as the close-day summary with the same formulas, and a test checks that a
  one-day sales report equals that day's close-day figures. Nothing is pre-aggregated; a range is capped
  at 366 days.
- Payments and refunds count toward the business day of the drawer they were taken in. Hours are the
  branch's local time when the order was placed; weekdays come from the business date.
- `reports.sales` sees sales without money collected; `reports.financial` adds payments. Cashiers' "own
  shift totals" are the business-day screen, not a report.
- Stock reports are always for the selected branch. On hand and value are current (the ledger has no
  as-of snapshot yet); the period's activity is the ledger between the first day's start hour and the day
  after the last.
- The closing report prints on 80 mm through the browser print dialog (desktop and web). Other reports
  print on A4.
- T15 injects a database failure *after* a mid-transaction insert (kitchen tickets, cash movements, stock
  movements, the close-day audit row) and checks nothing was kept and the retry works. The concurrency
  tests found two races, now fixed: simultaneous voids returned 500 (the second reversal hit the ledger's
  unique key) and simultaneous first receipts of an item returned 500 (both created its balance row).
  Both now return 409 and the client can retry.
- Security: per-user rate limit on every endpoint (600/min; anonymous 120/min per IP; sign-in endpoints
  keep their 10/min per IP), security headers and HSTS on the API, an 8 MB request cap, and the request
  log records paths only (the hub's token is in the query string). Lockout (5 tries, 5 min) and PIN
  lockout were already in place.
- Backups: `scripts/backup-database.sh` dumps the database and the uploads nightly (14 daily, 8 weekly)
  and before every deploy's migrations (5 kept), checks each dump with `pg_restore --list`, and can copy
  off the VM (rsync or rclone). `restore-drill.sh` restores the newest backup into a throwaway container
  every Sunday and checks it. `monitor.sh` checks health, backup age, failure markers and disk every
  5 minutes and alerts a webhook after two failing runs. `install-ops.sh` installs all three (cron in
  `/etc/cron.d/restaurant-ops`, settings in `/etc/restaurant/ops.env`) on every deploy. Tested against
  the local database and a throwaway Linux container, not yet on the VM.
- Desktop: `installer/build-installer.ps1` publishes the app self-contained and compiles
  `installer/Restaurant.Desktop.iss` (needs Inno Setup 6). The app checks `GET /api/desktop-release` on
  start and shows a notice (or a "no longer supported" banner below `MinimumVersion`); installers are
  served from `/downloads/`. There is no silent auto-update.
- The desktop app caches its register's printers for 5 minutes (and forgets them when they are saved on
  that PC).
- Left for go-live: compile the installer, add the GitHub secrets and deploy, and run the go-live
  checklist in `docs/PRINTING.md` on a real counter PC and thermal printer.

### Deferred items, batch 1 (after Phase 5)
- [x] Ownership transfer: an owner makes an active staff member an owner (all branches), confirmed with
  their own password, and can step down to Manager in the same transaction. Audited as
  `ownership.transferred`.
- [x] Cashiers' own shift totals: `GET /api/reports/my-shift` (orders.create or payments.take) and a
  "My shift today" panel on the business-day screen. It shows only the caller's own orders and payments
  in the open day, so it needs no report permission.
- [x] Per-PC tills: each counter device can be set to a register in Settings → Counter devices. The
  desktop app reads it with its device token (`GET /api/devices/current`) and uses that register's
  printers and cash drawer, and sends its payments, refunds and cash movements to that drawer. With no
  register set it uses the branch's default, as before.
- [x] Low-stock alerts: `GET /api/inventory/low-stock` (items at or below a reorder level that is set, or
  below zero), a count on the Inventory menu item and a list on the home screen. They refresh on
  navigation and when orders change, at most once a minute.
- [x] Urdu on thermal printers (raster lines, see section 10). It hasn't been tried on a real printer yet.
- Found and fixed on the way: opening a second register in a business day that had already started
  always failed with a 500, because the new drawer was saved as an UPDATE.
- Still deferred: purchase orders and suppliers as records, stock transfers between branches, table
  reservations and cleaning states.

### Phase 6 — Optional, after release
- [ ] Offline order capture on the desktop (local SQLite outbox, offline order-number range per register, conflict rules)
- [ ] Android tablet build for waiters, a customer-facing display
- [ ] FBR / PRA POS invoice integration (fiscal invoice number field reserved on `Receipt`)
- [ ] Foodpanda or website orders as an order source, delivery rider assignment
- [ ] WhatsApp receipts (Tailor Book has a `WhatsAppMessage` model), loyalty

---

## 18. Open questions (don't block Phase 1)

1. **Tax:** which provinces or authorities first (PRA Punjab, SRB Sindh, KPRA, ICT)? Do cash and card
   need different tax rates? The model supports both; the question only decides the seed defaults.
2. **Day close:** confirm D7 (block on unpaid orders) or allow carrying unpaid orders into the next day.
3. **Registers:** one till per branch to start? (D5 supports more.)
4. **Rounding:** round totals to the nearest Rs 1 (common in Pakistan) or Rs 5/10, or not at all?
5. **Pricing:** will tenants pay (plans, trial)? Billing is not in this plan.
6. **Product name and logo** for the public site and the desktop installer.

## 19. Known limitations (release 1)

- No offline order taking (D8).
- No card terminal or payment gateway integration. Card and wallet payments are recorded manually.
- No FBR or PRA fiscal integration yet.
- Silent printing only on the desktop app. The web app uses the browser print dialog.
- Delivery is a data model and an order type only, with no rider or aggregator integration.
