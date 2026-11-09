# Phase 16 audit: shops, deliveries, packages and sales

**Evidence:** static review of 0.57 mod source and native PCBS2 paths. No new in-game commerce checks. See [phase 15](phase-15.md) for the common command and save boundaries and the [Career register](career-economy-and-persistence.md) for the broader source inventory.

| Factors | Native or mod evidence | Proposed command and state boundary |
| --- | --- | --- |
| CP-010, 017, 043–050 | `Checkout.OnBuy` calls `CareerStatus.SpendCash`, `TransferBasketToTransit`, `Calendar.ScheduleDelivery` and, on immediate paths, `AddItemToInventory`. The native commit has multiple writes and clears purchased lines. [Checkout trace](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Checkout.txt#L7630) | Both players edit each shop's shared cart. Keep search, filters and scroll personal. Carry the expected cart revision, quoted total and delivery choice in a checkout intent. The host rechecks available stock, price, discount, unlock, job tag, delivery date/fee and funds. Settle one ledger debit, order, cart update and either transit manifest or granted inventory as one operation. |
| CP-009, 011, 049–051 | `Calendar.ScheduleDelivery`, `CareerStatus.AddDelivery`, `CollectDeliveries`, `Package`, `PackageCareerStatus`, and `DeliveryBox` represent scheduled, collectable and physical states. Package pickup, drop and slot assignment have separate native entry points. [Collect deliveries](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CareerStatus.txt#L30780) · [Bench placement](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/BenchSlot.txt#L2645) | Bind order and package identity through scheduled, arrived, carried, slotted, opened and settled states where reachable. Move the same package and its job/part links, including when a carrier leaves. Opening consumes a package once and grants the exact owned items. Associate a refund with the source transaction and returned goods wherever the native action exists. No independent client delivery roll or item grant. |
| CP-015–017, 052–054, 068 | `PCBayApp.OnBuyAction(Offer)` calls `SpendCash` and `Calendar.ScheduleDelivery`. `CareerStatus.GenerateTodaysOffers`, `RetailApp`, `ShopSalesManager` and `CareerStatus.SpawnShopComputer` are leads for the daily offer and retail settlement paths. [PC Bay trace](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/PCBayApp.txt#L2551) · [Shop computer spawn](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CareerStatus.txt#L36031) | Assign offer, listing and sale identities. Validate offer availability/expiry and PC ownership/location/use. Order purchase and sale outcomes with money, custody and review/ledger updates. Resolve resale valuation, market rolls and other nondeterministic results once on the host. |
| CP-007–009, 012, 029, 046 | The current plugin has cable purchase mirroring, tool-bench inventory reconciliation and a package/note refusal. The shared desktop replay can reach native app callbacks. [Cable purchase](../../src/PCBS2Coop.Plugin/NativeCustomization.cs#L475) · [Unsupported objects](../../src/PCBS2Coop.Plugin/NativeWorkstations.cs#L307) | Reuse the owned-item identity and PC claims from phase 15. Route the existing cable purchase path through the Career transaction once. Suppress the same logical checkout from replayed input. Replace the package/note refusal only for objects that have supported identity, contents, location and restore paths. Link shared physical notes in phase 17. |

Treat `SpendCash`, inventory add/remove and native mail/calendar notifications as projection or evidence points. Do the economic validation at the complete transaction boundary, including operations that reach a different sink for immediate and scheduled delivery. On guest disconnect or restore without a carrier, return the same physical package to reachable shared staging or storage. Resolve the exact native placement target from the scene and preserve the job link.

Before coding each adapter, finish tracing the exact callbacks for buy confirmations, retail listings and settlement, purchase exemptions, refunds, package open/claim and sale-driven reviews. The existence of a Retail or PC Bay API alone does not establish that a UI path reaches it. The [phase 16 checklist](../career-testing.md#16-shop-delivery-and-sale) covers double checkout, stale carts and prices, low funds, exact item custody, cancellation, backup and late join.

## Owner decisions for phase 16 (November 8, 2026)

The owner accepted the recommended options, which are also in the [plan](../../plan.md#career-decisions-confirmed-november-8-2026):

- **Checkout:** the host's game places every order. The guest's Buy sends the lines and total it showed.
- **Packages:** fully shared in 16c, which removes the hosting refusal.
- **Shop sales:** either player lists a PC and sets its price.
- **Delivery:** one version per step, stopping after each for review.

## Callback trace (16a follow-up)

| Path | What the game does (decompile) | Consequence for co-op |
| --- | --- | --- |
| Buy button | `CheckoutFinalRow.OnBuy` calls `CableApp.OnBuy` or `Checkout.OnBuy`, then `Checkout.ClearTrolley` when the purchase succeeded. | The guest's `Checkout.OnBuy` returns false, so its cart stays until the host's order empties it. |
| Cart | `Shop.m_trolley` is set from `CareerStatus.GetShopTrolley(ShopID)` in `Shop.Start`. `Shop.AddItem` adds one entry per unit. The four carts are saved in `CareerStatus.State` (`m_shopTrolley`, `m_wcShopTrolley`, `m_perifShopTrolley`, `m_cableShopTrolley`). Job baskets are `JobTag.InBasket`/`InBasketWater` in `State.m_jobTags`. | Carts are shared as whole lists and written into the saved list in place. |
| Total | `Checkout.UpdateCost`: the sum of `ShopEntry.m_cost`, plus `DeliveryOption.m_cost` when any line isn't `PartDesc.ImmediateDelivery()`. | The host recomputes the total and refuses a different one. |
| Checkout | `Checkout.OnBuy` does the following in order. (1) `SpendCash(total, exempt, GOODS)`. (2) `TransferBasketToTransit(ids, shop)`. (3) Picks the delivery days as `Random.Range(min, max + 1)`. (4) Sends each line to one place: the inventory if it's available now (`AddItemToInventory`), the delivery box if it arrives the same day (`AddDelivery`), a workshop case spawn (`SpawnCase`, falling back to tomorrow), or the calendar for later days (`MarkEverInInventory` and `ScheduleDelivery`). (5) Runs onboarding exemptions, the order e-mail, tutorials and the confirmation rows. | The host repeats steps 1 to 4 for the guest's order. A same-day case goes to the delivery box instead of the floor. |
| Cable app | `CableApp.OnBuy` pays, makes cable sets, and either adds them to the delivery box or inventory or schedules them. It clears its own cart. The app is on a shared PC desktop, so the other game mirrors clicks. | The host's game presses Buy on the same PC's Cable app. |
| Sell | `ItemDisplay.SellButtonClicked` shows a confirmation, then `SellItem(doIt, ...)` runs `AddCash(GetResaleValue, PART_SALE)` and `RemoveItemFromInventory`. `Inventory.SellAllBroken` sells every main inventory item whose condition is `BROKEN`. | The guest's sale is sent by item ID. |
| Deliveries | Calendar delivery events add to `m_deliveries` in the morning (`CalendarEvent.OnMorning`). `CollectDeliveries` moves them into the inventory. | 16c |
| PC Bay | `PCBayApp.OnBuyAction` pays, then schedules the PC delivery or adds it to storage. | 16d |
| Shop-front sales | `ShopSalesManager.CalculateDayPurchases` runs at day end. It sells PCs on sale slots and removes them. | 16d. The settlement is already host-only. |

## 0.60 implementation: shared carts, checkout and part sales (16b)

**Status:** coded with model tests on November 8, 2026. Release built with zero warnings or errors and all 787 automated checks passed. No game was launched. Nothing here is verified in the running game.

| Area | What 0.60 does |
| --- | --- |
| Model | Command 19 `CommerceWorld`, with the optional world state `commerce`. It holds each shop's cart (serialized lines and a revision) and the job baskets (with a revision). The host binds them from its game at every generation start, and a joining guest's carts become the host's. Free Build leaves it unbound and refuses every command. |
| Cart edits | Both games read their four carts and job baskets four times a second. A local change is sent as the whole cart, naming the revision it was made on. An edit made on an old revision is refused, and that game takes the shared cart back with a notice (silently for the Cable app, whose clicks the desktop already mirrors). The other game writes the cart into its list in place and refreshes any open shop screen. |
| Checkout | The host's own Buy runs the game's checkout. A same-day case goes to the delivery box. The guest's Buy sends an order with the cart revision, line count, the total it showed and the delivery. The host refuses: a changed cart, a different total, too little cash, an impossible delivery or a repeated order. On acceptance it pays, moves job baskets, and sends each line to the inventory, the delivery box or the calendar as the game does. The cart empties on both games, and the guest sees "Order placed". Cash, items and ledger reach the guest as a career update. |
| Cable app | The guest's Buy sends an order naming the shared PC. The host checks the total and cash, then presses Buy on that PC's Cable app. |
| Selling | The guest's Sell and Sell All Broken send item IDs. The host refuses an item that is gone, being installed or can't be sold, then sells the exact item for its resale value. |
| Delivery box | The guest's delivery collection is refused until 16c. The host collects. |
| 0.59 fix | The host's career updates no longer wait 10 s after each one. The host's own commands are answered inside `Submit`, and the update was being marked as waiting only afterwards. |

### Not covered by 0.60

- The guest's order shows a notice instead of the game's confirmation page. The order e-mail, purchase tutorials and onboarding purchase exemptions are not repeated for a guest's order. With an exemption, the host may refuse the guest's total.
- Deliveries and the delivery box aren't shared yet (16c). Same-day items from either player's order wait in the host's delivery box. Scheduled deliveries show on the guest's calendar after the day-end reload.
- Prices are checked as the total of the shared cart, not against the catalog again. Both games have the same prices within a day.
- PC Bay, shares, jobs and End Day stay host-only (16d and 17).

### Runtime checks for the gameplay sweep

1. A shop screen shows the other player's cart edit after `UpdateTrolley`, and an open checkout rebuilds. `Shop.m_trolley` is the saved list.
2. A cart line made from its bytes shows and buys like the original. Normal lines use `ShopEntry(PartDesc)`; cable lines use the five-argument constructor.
3. A guest order placed by the host makes one GOODS ledger entry. Its items reach the inventory, the host's delivery box or the host's calendar, and job-tagged items move to transit.
4. The guest's `m_checkoutTotal` equals the host's total. If orders are refused with "The total is …", the formula differs.
5. A same-day case bought by the host goes to the delivery box, and after collection it spawns through co-op.
6. A guest Cable app Buy completes on the host's mirrored app. Cable cart clicks converge without notices.
7. Guest Sell and Sell All Broken sell exactly those items on the host, and the guest's inventory and cash follow.
8. The guest's delivery box shows the refusal notice.
9. Job basket keys (`JobId|offer`) match on both games, and the guest's basket edits reach the host.
10. Host career updates go out about every 0.5 s with no "wasn't confirmed" warnings.
11. Startup logs no `[hooks]` error for the five new hooks.
