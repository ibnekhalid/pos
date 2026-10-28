# Printing receipts and kitchen tickets

Every print is logged by the API, so the second copy of a receipt says "DUPLICATE — REPRINT #1" and
printing never creates a sale. The server builds the document; each app only lays it out.

## Setting up a counter PC

Windows 10 (1809 or later) or Windows 11, 64-bit, always online (the app has no offline mode yet).

1. **Windows account**: use one Windows user for the till, and keep using it. The remembered sign-in and
   the device registration are encrypted for that Windows user, so another user on the same PC starts
   from scratch.
2. **Power**: set the screen and sleep to *Never* while plugged in (Settings → System → Power). In
   Device Manager, for each USB Root Hub (and the printer, if listed), untick *Allow the computer to turn
   off this device to save power*, or a USB printer can drop off after a quiet hour.
3. **Windows Update**: set *Active hours* to cover trading hours so the PC never restarts mid-service.
4. **Install the app**: run `Restaurant-POS-Setup-<version>.exe` (docs/DEPLOYMENT.md, "Building the
   installer"). The default per-user install needs no admin rights. The installer warns if the Microsoft
   Edge WebView2 Runtime is missing; install it before starting the app.
5. **First start**: sign in as the owner or a manager, then **Settings → Counter devices → Register this
   computer**. Staff can then sign in with their PIN, and the screen locks itself after the idle time
   chosen under **Settings → My account**.
6. **Full screen**: F11 or the user menu. The app opens maximised.
7. **Printers**: as below. Then do the checklist at the end of this page.

When a newer version is published, the app shows a notice with a download link at the top of every
screen. Run the new installer at a quiet moment. It closes the app, and the sign-in, registration and
preferences are kept.

## Desktop app (counter PC) — recommended

The desktop app sends ESC/POS straight to the printer. That's fast and needs no dialog; it also cuts the
paper and opens the cash drawer on cash payments.

1. Install the printer's Windows driver. Most 80 mm thermal printers (Epson TM, Xprinter, Bixolon, Black
   Copper, generic Chinese models) ship one; "Generic / Text Only" also works for USB and network printers.
   The app sends raw bytes through the Windows spooler, so the driver only has to accept RAW jobs. Print
   a Windows test page first (printer → *Printer properties* → *Print Test Page*); if that fails, the app
   can't print either.
2. Find the exact printer name under **Windows Settings → Printers & scanners**. Rename it to something
   short and stable, such as `Receipt` or `Kitchen` (*Printer properties* → *General*). A driver
   reinstall can add " (Copy 1)" to the name, and the old name then stops working.
3. In the app, open **Settings → Branches → (branch) → Registers → edit**:
   - **Receipt printer**: the name from step 2.
   - **Kitchen printer**: optional. When set, each "Send to kitchen" prints the kitchen tickets.
   - **Cash drawer**: tick it if the drawer is plugged into the receipt printer.
4. Take a test order and pay it in cash.

If no printer name is set, the desktop app falls back to the print dialog.

**Which register's printers**: the app uses the printers of the branch's **default** register, whichever
PC it runs on. With several counter PCs in one branch, give the printers the same Windows names on every
PC (for example a shared network `Kitchen` printer, and a `Receipt` printer on each till). Per-PC printer
settings are not built yet. The app reads the register's printers once per branch, so restart it on each
counter PC after changing them.

**Network printers** (port 9100): add them in Windows as a "Standard TCP/IP port" printer with the
Generic / Text Only driver, then use that name. Give the printer a fixed IP address (a DHCP reservation on
the router, or a static IP on the printer itself). Otherwise it can change address after a power cut, and
Windows can no longer reach it.

**Paper width**: Settings → Branding → Printing → *Customer receipt* chooses 80 mm or 58 mm
(48 or 32 characters per line).

**Cash drawer**: plug the drawer's RJ11/RJ12 cable into the printer's *DK* (drawer kick) port, not into
a phone or network socket. The app sends the standard kick on drawer pin 2 after a cash payment's
receipt. Most drawers use pin 2; a drawer wired for pin 5 won't open (that isn't configurable yet).

**Paper cut**: after each receipt or ticket, the app feeds the paper and does a partial cut. Printers
without a cutter ignore it; tear off by hand.

**Limits**: text mode prints Latin characters. Urdu item names come out as "?" on thermal printers. Give
items an English name and add the Urdu name separately, or use the PDF or browser print for Urdu receipts.

### Troubleshooting

| What you see | Check |
|---|---|
| "Printer "…" was not found" | The name in the register doesn't match Windows exactly. Look for " (Copy 1)" or a renamed printer |
| Nothing prints, no error | The job is stuck in the Windows queue (printer → *See what's printing*): the printer is offline, out of paper, or on a changed IP. Clear the queue and power-cycle the printer |
| Blank paper comes out | The thermal paper roll is in the wrong way round: the printing side faces the print head |
| Odd characters or a wall of symbols | The printer is in a non-ESC/POS mode or uses the wrong driver. Use the vendor's ESC/POS driver or Generic / Text Only |
| Drawer doesn't open | The cable is in the DK port, *Cash drawer* is ticked on the default register, and the order was paid in cash. Some drawers need pin 5 |
| The first print after a while is slow | The printer was asleep (USB power saving); see step 2 of the counter PC set-up |
| Kitchen tickets print at the counter | *Kitchen printer* is empty or has the same name as the receipt printer |

### Checklist for a new counter

- [ ] Windows test page prints on each printer
- [ ] PIN sign-in works, and the screen locks after the idle time
- [ ] Cash order: receipt prints, paper is cut, drawer opens
- [ ] Card order: receipt prints, drawer stays shut
- [ ] Send to kitchen: the ticket prints on the kitchen printer (or the kitchen display shows it)
- [ ] Reprint shows "DUPLICATE — REPRINT #1"
- [ ] Unplug the network for a moment: the app says it can't reach the server, then recovers

## Web app

The web app prints through the browser's print dialog, sized for the receipt paper. To print without the
dialog on a dedicated counter PC, start Chrome or Edge with `--kiosk-printing` and set the thermal printer
as the default.

## PDF

**Orders → (order) → Print receipt** logs a print. The API also serves
`/api/orders/{id}/receipt/pdf` (80/58 mm or A4) for sharing, for example on WhatsApp.
