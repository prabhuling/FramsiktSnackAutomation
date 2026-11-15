# Snack Counter Automation — System Design

## 1. Refill Lifecycle ("Empty" to "Reimbursed")

Each office snack category (e.g., Fruit, Nuts, Coffee/Milk, Biscuits) allows **at most one active refill workflow** at any time. A refill moves through six states:

1. **Reported (`Offered`)**: Any employee scans a QR code at the counter or runs `/snack-empty <category>` in chat.
   - **Deduplication**: If an active request already exists for that office and category, the duplicate report is merged silently.
   - **Budget Pre-Check & Reservation**: The system checks if the office has at least the minimum typical purchase amount remaining (`300 NOK` in Oslo, `500 INR` in Bengaluru). It reserves `Min(MaxTypicalPurchase, AvailableBudget)` so parallel refills cannot overdraft the monthly cap.
   - **Smart Nudge**: The system selects the fairest eligible volunteer on-site today and sends a direct chat prompt with an **Offer SLA (2 hours)** and the maximum pre-approved spend limit.
2. **Committed (`Claimed`)**: The nudged volunteer (or any on-site volunteer) clicks "Claim".
   - Acquires a concurrency lock on the refill and starts a **Fulfillment SLA (24 hours)**.
   - Any second volunteer attempting to claim receives an immediate lock rejection ("Already claimed by X").
3. **Auto-Rotation on Timeout (`Offered -> Offered` or `Claimed -> Offered`)**:
   - If the nudged volunteer does not claim within **2 hours**, or claims but fails to upload a receipt within **24 hours**, a background sweep revokes the assignment, adds the volunteer to the refill's skip-list for the day, and nudges the next eligible volunteer.
4. **Receipt Submitted (`PurchasedPendingHr`)**: The buyer uploads the receipt photo and actual spend in local office currency.
   - The temporary budget reservation is replaced by the **actual spend**.
   - The system converts the spend into the buyer's **payroll currency** (if they are visiting from the other office) and submits the claim to the external HR API.
   - The volunteer's `LastRefillAt` timestamp updates immediately.
5. **Settled (`Reimbursed`)**: When HR approves the claim (1–10 days later via webhook or polling), the state transitions to `Reimbursed`, freeing the volunteer to re-enter the rotation pool.

---

## 2. Main Components & Data Ownership

| Component | Responsibility | Data Kept |
|---|---|---|
| **`RefillWorkflowEngine`** | Enforces the state machine, concurrency locks, SLA expirations, and idempotency per office/category. | `RefillId`, `Office`, `Category`, `State`, `AssignedVolunteerId`, `ReservedBudget`, `ActualLocalSpend`, `OfferExpiresAt`, `ClaimExpiresAt`, `SkippedVolunteerIds`. |
| **`VolunteerSelector`** | Ranks eligible buyers fairly while respecting hybrid schedules, inter-office travel, and unpaid claims. | `EmployeeId`, `HomeOffice`, `CurrentOffice`, `PayrollCurrency`, `IsInOfficeToday`, `LastRefillAt`, `HasPendingReimbursement`. |
| **`BudgetAndReportingService`** | Tracks office caps (`6,000 NOK` / `15,000 INR`), active reservations, FX conversions, and snack consumption velocity. | Monthly caps per office, FX rates (`NOK <-> INR`), historical refill timestamps per category, unified monthly spend ledger. |
| **`IChatGateway` / `IHrClient`** | Adapter interfaces decoupling core domain logic from Slack/Teams and the external HR system. | External `HrClaimId`, receipt URL, payroll payout currency and converted amount. |

---

## 3. How Each Core Problem Is Prevented

* **"The box sits empty for days"**: Eliminates the bystander effect by converting a passive group announcement into a direct, 1-on-1 chat prompt to a single on-site volunteer with a 2-hour acceptance timer and 24-hour purchase timer.
* **"Two people buy snacks on the same day"**: Enforces a single active refill per office/category and an atomic state lock on `TryClaim`. Once claimed, the chat bot updates the office status so everyone knows a purchase is in flight.
* **"A few people do most of the refills and wait weeks for reimbursement"**:
  1. **Zero Double-Fronting Rule**: Volunteers with an open `PurchasedPendingHr` claim are excluded from selection until HR reimburses them.
  2. **Least-Recently-Used (LRU) Fairness**: Among on-site volunteers with no pending HR claims, the system picks the person with the oldest `LastRefillAt` (or `null` for first-timers).
* **"Nobody knows monthly spend or which snacks get eaten"**:
  1. Pre-reserves budget on open and records exact local spend on purchase.
  2. Tracks **refill frequency and average days between refills per category**—if "Fruit" is refilled every 3 days while "Protein Bars" takes 25 days, management sees actual consumption velocity without hardware sensors.

---

## 4. Trade-Offs & What We Deliberately Did Not Build

* **Trade-off — Pre-Reserved Budget vs. Strict Single-Refill Serialization**: Earmarking the typical maximum purchase (`1,200 NOK` / `3,000 INR`) while a refill is open means up to 20% of the monthly budget is temporarily locked during a 24h purchase window, but it guarantees volunteers *never* spend personal money only to have reimbursement rejected for budget overrun.
* **Deliberately Not Built**:
  1. **Item-Level Inventory / IoT Scales / Barcode Scanning**: Asking employees to scan every banana or biscuit they eat fails immediately in office culture. Category-level refill frequency ("how often does the Fruit box hit empty?") gives 90% of the consumption insight with zero daily friction.
  2. **OCR Receipt Line-Item Parsing**: HR already reviews the receipt image during their 1–10 day approval process. Having the volunteer type the total amount and select the category takes 5 seconds and avoids brittle OCR failures on handwritten Bengaluru/Oslo store receipts.
  3. **Automated Corporate Cards / Petty Cash Wallets**: Since the external HR reimbursement pipeline already exists and handles payroll payout, fixing rotation and visibility solves the problem in software without changing company finance policies.
