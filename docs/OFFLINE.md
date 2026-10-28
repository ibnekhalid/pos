# Taking orders offline (desktop counter PCs)

When the internet or the server drops, a **registered counter PC** keeps selling. The web app doesn't:
it has no device to number orders with and nowhere safe to keep them.

## What staff see

- The POS shows an **Offline** banner, and the top bar shows an *Offline* chip with the number of orders
  waiting.
- **Send** and **Pay** work as usual. The order gets a provisional number from this PC (for example
  `K7-03`). The kitchen ticket and the receipt print here, marked *OFFLINE*.
- Payment offline is **one payment for the whole bill**, in any method. Cash shows the change.
- These aren't available offline: **deals and coupons, manual discounts, adding to an order that is
  already open, split payments and customer lookup**. Their rules live on the server.
- When the connection is back, the orders are sent by themselves, oldest first. **Offline orders**
  (the top-bar chip, or POS → *View*) lists:
  - what is waiting;
  - what the server refused, and why;
  - what was sent, matching each paper number to the real order number.
- **Closing the day is blocked on this PC** while any of its offline orders are still waiting. Otherwise
  they would land in the next day.

## What the server does with them

Each order goes through the normal order and payment code, with the menu, taxes and stock rules in force
when it syncs. The order:

- gets a normal order number and keeps its paper number (shown on the order, and in the orders list);
- is dated when it was taken, and booked to the person who took it;
- has its kitchen tickets booked as **served** (the kitchen cooked from paper);
- uses stock even if the branch normally blocks short stock (the food is gone);
- gets its payment booked to this PC's till.

Sending again is always safe: whatever already arrived is skipped.

Some things can't be booked exactly as they were taken. The order is still booked, but marked **Check**,
with the reason. It then appears under **Orders → To check**. A manager (orders.void) fixes it with the
usual refund, payment or void, then presses **Mark as checked**.

| What happened | What the server does |
|---|---|
| The server's total differs from what this PC charged (a price, tax or deal changed) | Books the server's total. A cash difference stays as a balance due; card money over the total is capped. Marked Check. |
| The table was given to someone else meanwhile | Books the order without a table. Marked Check. |
| The day it was taken in was closed on another PC | Books it to the day that's open now. Marked Check. |
| No business day is open | Refuses. The order waits on the PC, and is sent once a day is started. |
| A menu item was deleted meanwhile | Refuses, with the reason. A manager re-enters it by hand from the paper ticket, then discards it from the outbox. |
| The PC's clock is more than 10 minutes ahead, or the order is over 7 days old | Refuses. Fix the clock (Windows → Date & time → Set automatically). |

## Lock screen and restarts

- **Auto-lock pauses** while offline. Unlocking normally needs the server.
- If someone does press **Lock**, only the person signed in can unlock offline, with their PIN. The PC can
  check a PIN offline only if that person has signed in on this PC with it before; five wrong tries need
  the connection again.
- **If the app restarts while offline:** a password sign-in with *Keep me signed in* comes back, with its
  last permissions, and can keep selling. A PIN sign-in can't, because it is never saved on the PC. Its
  orders stay safe in the outbox and go out after the next sign-in.

## Where the data is

- The outbox, the last menu snapshot per branch, and the counter for paper numbers are kept in the app's
  data folder (MAUI's `FileSystem.AppDataDirectory`, under `%LOCALAPPDATA%` for the Windows user). They
  belong to that Windows user, so give the counter one Windows account (docs/PRINTING.md).
- Every write goes to disk before it replaces the old file, so a power cut keeps the old copy or the new
  one, never half of one. An unreadable outbox file is kept aside under a new name instead of being
  emptied.
- Each PC's paper numbers start with two characters from its device id, so two PCs offline at the same
  time never print the same number.
