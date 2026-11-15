using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Framsikt.SnackAutomation;

// ============================================================================
// 1. DOMAIN VALUE OBJECTS & ENUMS
// ============================================================================

public enum Office { Oslo, Bengaluru }
public enum Currency { NOK, INR }

public enum RefillState
{
    Offered,            // Open and offered to a specific on-site volunteer (2h SLA to claim)
    OpenBroadcast,      // No single volunteer available; open to anyone in the office channel
    Claimed,            // Committed by one volunteer (24h SLA to purchase & submit receipt)
    PurchasedPendingHr, // Receipt submitted; claim sent to external HR (1-10 days to reimburse)
    Reimbursed,         // HR payout completed
    Cancelled           // Closed without spend
}

public readonly record struct Money(decimal Amount, Currency Currency)
{
    public static Money Zero(Currency currency) => new(0m, currency);
    public override string ToString() => $"{Amount:F2} {Currency}";
}

public sealed record OfficeBudgetPolicy(
    Office Office,
    Currency LocalCurrency,
    decimal MonthlyCap,
    decimal MinTypicalPurchase,
    decimal MaxTypicalPurchase);

public sealed class Volunteer
{
    public string EmployeeId { get; }
    public string Name { get; }
    public Office HomeOffice { get; }
    public Office CurrentOffice { get; set; }
    public Currency PayrollCurrency { get; }
    public bool IsInOfficeToday { get; set; }
    public DateTimeOffset? LastRefillAt { get; set; }

    public Volunteer(
        string employeeId,
        string name,
        Office homeOffice,
        Office currentOffice,
        Currency payrollCurrency,
        bool isInOfficeToday,
        DateTimeOffset? lastRefillAt = null)
    {
        EmployeeId = employeeId;
        Name = name;
        HomeOffice = homeOffice;
        CurrentOffice = currentOffice;
        PayrollCurrency = payrollCurrency;
        IsInOfficeToday = isInOfficeToday;
        LastRefillAt = lastRefillAt;
    }
}

// ============================================================================
// 2. REFILL AGGREGATE ROOT (STATE MACHINE & CONCURRENCY LOCK)
// ============================================================================

public sealed class RefillRequest
{
    private readonly object _syncLock = new();
    private readonly HashSet<string> _skippedVolunteerIds = new();

    public Guid Id { get; } = Guid.NewGuid();
    public Office Office { get; }
    public string SnackCategory { get; }
    public DateTimeOffset CreatedAt { get; }
    public RefillState State { get; private set; }

    public string? OfferedToVolunteerId { get; private set; }
    public DateTimeOffset? OfferExpiresAt { get; private set; }

    public string? ClaimedByVolunteerId { get; private set; }
    public DateTimeOffset? ClaimExpiresAt { get; private set; }

    public Money ReservedBudget { get; private set; }
    public Money? ActualLocalSpend { get; private set; }
    public Money? ReimbursedPayrollAmount { get; private set; }
    public string? ReceiptImageUrl { get; private set; }
    public string? HrClaimId { get; private set; }
    public DateTimeOffset? PurchasedAt { get; private set; }
    public DateTimeOffset? ReimbursedAt { get; private set; }

    public IReadOnlySet<string> SkippedVolunteerIds => _skippedVolunteerIds;

    public bool IsActive => State is RefillState.Offered or RefillState.OpenBroadcast or RefillState.Claimed;

    public RefillRequest(Office office, string snackCategory, Money reservedBudget, DateTimeOffset now)
    {
        Office = office;
        SnackCategory = snackCategory.Trim();
        ReservedBudget = reservedBudget;
        CreatedAt = now;
        State = RefillState.OpenBroadcast;
    }

    public void AssignOffer(string volunteerId, DateTimeOffset now, TimeSpan offerTtl)
    {
        lock (_syncLock)
        {
            State = RefillState.Offered;
            OfferedToVolunteerId = volunteerId;
            OfferExpiresAt = now.Add(offerTtl);
            ClaimedByVolunteerId = null;
            ClaimExpiresAt = null;
        }
    }

    public void FallbackToBroadcast()
    {
        lock (_syncLock)
        {
            State = RefillState.OpenBroadcast;
            OfferedToVolunteerId = null;
            OfferExpiresAt = null;
            ClaimedByVolunteerId = null;
            ClaimExpiresAt = null;
        }
    }

    /// <summary>
    /// Atomic claim transition. Prevents two volunteers from committing to the same refill.
    /// </summary>
    public bool TryClaim(string volunteerId, DateTimeOffset now, TimeSpan claimTtl)
    {
        lock (_syncLock)
        {
            if (State is not (RefillState.Offered or RefillState.OpenBroadcast))
                return false;

            State = RefillState.Claimed;
            ClaimedByVolunteerId = volunteerId;
            ClaimExpiresAt = now.Add(claimTtl);
            OfferedToVolunteerId = null;
            OfferExpiresAt = null;
            return true;
        }
    }

    /// <summary>
    /// Checks if either the 2h Offer SLA or 24h Claim SLA has expired, recording the timed-out
    /// volunteer in the skip list so rotation moves to someone else.
    /// </summary>
    public bool TryExpireAndRelease(DateTimeOffset now, out string? timedOutVolunteerId)
    {
        lock (_syncLock)
        {
            if (State == RefillState.Offered && OfferExpiresAt <= now)
            {
                timedOutVolunteerId = OfferedToVolunteerId;
                if (timedOutVolunteerId is not null)
                    _skippedVolunteerIds.Add(timedOutVolunteerId);

                FallbackToBroadcast();
                return true;
            }

            if (State == RefillState.Claimed && ClaimExpiresAt <= now)
            {
                timedOutVolunteerId = ClaimedByVolunteerId;
                if (timedOutVolunteerId is not null)
                    _skippedVolunteerIds.Add(timedOutVolunteerId);

                FallbackToBroadcast();
                return true;
            }

            timedOutVolunteerId = null;
            return false;
        }
    }

    public void CompletePurchase(
        string volunteerId,
        Money actualLocalSpend,
        Money payrollReimbursement,
        string receiptImageUrl,
        string hrClaimId,
        DateTimeOffset now)
    {
        lock (_syncLock)
        {
            if (State != RefillState.Claimed || ClaimedByVolunteerId != volunteerId)
                throw new InvalidOperationException("Only the volunteer who claimed this refill can submit a receipt.");

            ActualLocalSpend = actualLocalSpend;
            ReimbursedPayrollAmount = payrollReimbursement;
            ReceiptImageUrl = receiptImageUrl;
            HrClaimId = hrClaimId;
            PurchasedAt = now;
            ReservedBudget = Money.Zero(ReservedBudget.Currency); // Release reservation; actual spend now applies
            State = RefillState.PurchasedPendingHr;
            ClaimExpiresAt = null;
        }
    }

    public void MarkReimbursed(DateTimeOffset now)
    {
        lock (_syncLock)
        {
            if (State != RefillState.PurchasedPendingHr)
                throw new InvalidOperationException("Refill must be in PurchasedPendingHr state to mark reimbursed.");

            ReimbursedAt = now;
            State = RefillState.Reimbursed;
        }
    }
}

// ============================================================================
// 3. EXTERNAL SYSTEM PORTS (CHAT, HR, EXCHANGE RATES)
// ============================================================================

public sealed record HrClaimRequest(
    string EmployeeId,
    decimal Amount,
    Currency Currency,
    string Description,
    string ReceiptImageUrl);

public interface IChatGateway
{
    Task SendDirectOfferAsync(string employeeId, Guid refillId, Office office, string category, Money maxApprovedSpend);
    Task BroadcastOpenRefillAsync(Office office, Guid refillId, string category, Money maxApprovedSpend);
    Task NotifyOfficeClaimedAsync(Office office, Guid refillId, string category, string volunteerName);
}

public interface IHrSystemClient
{
    Task<string> CreateReimbursementClaimAsync(HrClaimRequest request);
}

public interface IExchangeRateProvider
{
    decimal GetExchangeRate(Currency from, Currency to);
}

// ============================================================================
// 4. REPORTING MODELS
// ============================================================================

public sealed record CategoryConsumptionMetric(
    Office Office,
    string SnackCategory,
    int RefillCount,
    Money TotalLocalSpend,
    double? AverageDaysBetweenRefills);

public sealed record MonthlyManagementReport(
    int Year,
    int Month,
    Money OsloTotalSpend,
    Money OsloRemainingBudget,
    Money BengaluruTotalSpend,
    Money BengaluruRemainingBudget,
    Money UnifiedTotalSpend,
    IReadOnlyList<CategoryConsumptionMetric> CategoryBreakdown);

// ============================================================================
// 5. CORE ORCHESTRATION SERVICE
// ============================================================================

public sealed class SnackCounterAutomationService
{
    private readonly Dictionary<Office, OfficeBudgetPolicy> _policies = new()
    {
        [Office.Oslo] = new OfficeBudgetPolicy(Office.Oslo, Currency.NOK, MonthlyCap: 6_000m, MinTypicalPurchase: 300m, MaxTypicalPurchase: 1_200m),
        [Office.Bengaluru] = new OfficeBudgetPolicy(Office.Bengaluru, Currency.INR, MonthlyCap: 15_000m, MinTypicalPurchase: 500m, MaxTypicalPurchase: 3_000m)
    };

    private readonly Dictionary<string, Volunteer> _volunteers = new();
    private readonly List<RefillRequest> _refills = new();

    private readonly IChatGateway _chat;
    private readonly IHrSystemClient _hr;
    private readonly IExchangeRateProvider _fx;

    public TimeSpan OfferSla { get; init; } = TimeSpan.FromHours(2);
    public TimeSpan ClaimSla { get; init; } = TimeSpan.FromHours(24);

    public SnackCounterAutomationService(IChatGateway chat, IHrSystemClient hr, IExchangeRateProvider fx)
    {
        _chat = chat;
        _hr = hr;
        _fx = fx;
    }

    public void UpsertVolunteer(Volunteer volunteer) => _volunteers[volunteer.EmployeeId] = volunteer;

    public IReadOnlyList<RefillRequest> GetAllRefills() => _refills.AsReadOnly();

    /// <summary>
    /// Reports a snack category empty. Deduplicates active requests, pre-reserves budget,
    /// and selects the fairest on-site volunteer who has no pending unpaid HR claims.
    /// </summary>
    public async Task<RefillRequest> ReportEmptyBoxAsync(Office office, string snackCategory, DateTimeOffset now)
    {
        var normalizedCategory = snackCategory.Trim();

        // 1. Idempotency check: prevent duplicate concurrent refills for the same box
        var active = _refills.FirstOrDefault(r =>
            r.Office == office &&
            string.Equals(r.SnackCategory, normalizedCategory, StringComparison.OrdinalIgnoreCase) &&
            r.IsActive);

        if (active is not null)
            return active;

        // 2. Pre-check available budget (accounting for both actual spend AND active reservations)
        var policy = _policies[office];
        var unallocated = GetUnallocatedBudget(office, now.Year, now.Month);

        if (unallocated.Amount < policy.MinTypicalPurchase)
        {
            throw new InvalidOperationException(
                $"Cannot open refill for {office}: unallocated monthly budget ({unallocated}) is below minimum purchase threshold ({policy.MinTypicalPurchase} {policy.LocalCurrency}).");
        }

        // Earmark up to MaxTypicalPurchase (or remaining unallocated budget if smaller)
        var reservedAmount = Math.Min(policy.MaxTypicalPurchase, unallocated.Amount);
        var refill = new RefillRequest(office, normalizedCategory, new Money(reservedAmount, policy.LocalCurrency), now);
        _refills.Add(refill);

        await AssignNextVolunteerOrBroadcastAsync(refill, now);
        return refill;
    }

    /// <summary>
    /// Allows a volunteer to claim an open/offered refill.
    /// </summary>
    public async Task<bool> TryClaimRefillAsync(Guid refillId, string volunteerId, DateTimeOffset now)
    {
        var refill = GetRefillById(refillId);
        if (!_volunteers.TryGetValue(volunteerId, out var volunteer))
            throw new KeyNotFoundException($"Volunteer '{volunteerId}' is not registered.");

        if (volunteer.CurrentOffice != refill.Office || !volunteer.IsInOfficeToday)
            return false; // Must be physically present in that office today

        if (!refill.TryClaim(volunteerId, now, ClaimSla))
            return false;

        await _chat.NotifyOfficeClaimedAsync(refill.Office, refill.Id, refill.SnackCategory, volunteer.Name);
        return true;
    }

    /// <summary>
    /// Sweeps expired 2h offers and 24h claims, handing the refill to the next eligible volunteer.
    /// </summary>
    public async Task ProcessTimeoutsAsync(DateTimeOffset now)
    {
        foreach (var refill in _refills.Where(r => r.IsActive))
        {
            if (refill.TryExpireAndRelease(now, out _))
            {
                await AssignNextVolunteerOrBroadcastAsync(refill, now);
            }
        }
    }

    /// <summary>
    /// Records the purchase, validates budget against the pre-reserved cap, converts currency
    /// if the buyer is a visiting employee from another payroll, and creates the HR claim.
    /// </summary>
    public async Task SubmitPurchaseReceiptAsync(
        Guid refillId,
        string volunteerId,
        Money localSpend,
        string receiptImageUrl,
        DateTimeOffset now)
    {
        var refill = GetRefillById(refillId);
        var volunteer = _volunteers[volunteerId];
        var policy = _policies[refill.Office];

        if (localSpend.Currency != policy.LocalCurrency)
            throw new InvalidOperationException($"Purchase in {refill.Office} must be recorded in local currency ({policy.LocalCurrency}).");

        if (localSpend.Amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(localSpend), "Spend amount must be positive.");

        // Available budget for this purchase includes the refill's own reservation
        var unallocated = GetUnallocatedBudget(refill.Office, refill.CreatedAt.Year, refill.CreatedAt.Month);
        var maxAllowedSpend = unallocated.Amount + refill.ReservedBudget.Amount;

        if (localSpend.Amount > maxAllowedSpend)
        {
            throw new InvalidOperationException(
                $"Purchase of {localSpend} exceeds available monthly budget ({maxAllowedSpend:F2} {policy.LocalCurrency}).");
        }

        // Convert to the volunteer's payroll currency (handles cross-office visitors)
        var fxRate = _fx.GetExchangeRate(localSpend.Currency, volunteer.PayrollCurrency);
        var payrollAmount = Math.Round(localSpend.Amount * fxRate, 2);
        var payrollReimbursement = new Money(payrollAmount, volunteer.PayrollCurrency);

        var description = $"{refill.Office} Snack Refill ({refill.SnackCategory}) - Local Spend: {localSpend}";
        var hrClaimId = await _hr.CreateReimbursementClaimAsync(new HrClaimRequest(
            volunteer.EmployeeId,
            payrollReimbursement.Amount,
            payrollReimbursement.Currency,
            description,
            receiptImageUrl));

        refill.CompletePurchase(volunteerId, localSpend, payrollReimbursement, receiptImageUrl, hrClaimId, now);
        volunteer.LastRefillAt = now;
    }

    public void MarkHrClaimReimbursed(Guid refillId, DateTimeOffset now)
    {
        var refill = GetRefillById(refillId);
        refill.MarkReimbursed(now);
    }

    /// <summary>
    /// Selects the fairest candidate:
    /// 1. Currently in the target office & working on-site today
    /// 2. Has NOT already skipped/timed out on this refill
    /// 3. Does NOT have an unpaid HR claim waiting (prevents double out-of-pocket burden)
    /// 4. Does NOT already hold another active claim
    /// 5. Ordered by oldest LastRefillAt (nulls first)
    /// </summary>
    private async Task AssignNextVolunteerOrBroadcastAsync(RefillRequest refill, DateTimeOffset now)
    {
        var busyOrUnpaidVolunteerIds = _refills
            .Where(r => r.State is RefillState.Offered or RefillState.Claimed or RefillState.PurchasedPendingHr)
            .SelectMany(r => new[] { r.OfferedToVolunteerId, r.ClaimedByVolunteerId })
            .Where(id => id is not null)
            .ToHashSet()!;

        var nextCandidate = _volunteers.Values
            .Where(v => v.CurrentOffice == refill.Office && v.IsInOfficeToday)
            .Where(v => !refill.SkippedVolunteerIds.Contains(v.EmployeeId))
            .Where(v => !busyOrUnpaidVolunteerIds.Contains(v.EmployeeId))
            .OrderBy(v => v.LastRefillAt ?? DateTimeOffset.MinValue)
            .ThenBy(v => v.EmployeeId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (nextCandidate is not null)
        {
            refill.AssignOffer(nextCandidate.EmployeeId, now, OfferSla);
            await _chat.SendDirectOfferAsync(
                nextCandidate.EmployeeId,
                refill.Id,
                refill.Office,
                refill.SnackCategory,
                refill.ReservedBudget);
        }
        else
        {
            refill.FallbackToBroadcast();
            await _chat.BroadcastOpenRefillAsync(
                refill.Office,
                refill.Id,
                refill.SnackCategory,
                refill.ReservedBudget);
        }
    }

    public Money GetActualMonthlySpend(Office office, int year, int month)
    {
        var policy = _policies[office];
        var spent = _refills
            .Where(r => r.Office == office
                        && r.CreatedAt.Year == year
                        && r.CreatedAt.Month == month
                        && r.ActualLocalSpend is not null
                        && r.State is RefillState.PurchasedPendingHr or RefillState.Reimbursed)
            .Sum(r => r.ActualLocalSpend!.Value.Amount);

        return new Money(spent, policy.LocalCurrency);
    }

    public Money GetUnallocatedBudget(Office office, int year, int month)
    {
        var policy = _policies[office];
        var actualSpent = GetActualMonthlySpend(office, year, month).Amount;
        var activeReserved = _refills
            .Where(r => r.Office == office
                        && r.CreatedAt.Year == year
                        && r.CreatedAt.Month == month
                        && r.IsActive)
            .Sum(r => r.ReservedBudget.Amount);

        return new Money(Math.Max(0m, policy.MonthlyCap - actualSpent - activeReserved), policy.LocalCurrency);
    }

    /// <summary>
    /// Generates the unified monthly report across Oslo & Bengaluru, including category
    /// consumption velocity ("which snacks actually get eaten").
    /// </summary>
    public MonthlyManagementReport GenerateMonthlyReport(int year, int month, Currency reportingCurrency)
    {
        var osloPolicy = _policies[Office.Oslo];
        var blrPolicy = _policies[Office.Bengaluru];

        var osloSpend = GetActualMonthlySpend(Office.Oslo, year, month);
        var blrSpend = GetActualMonthlySpend(Office.Bengaluru, year, month);

        var osloRemaining = new Money(osloPolicy.MonthlyCap - osloSpend.Amount, osloPolicy.LocalCurrency);
        var blrRemaining = new Money(blrPolicy.MonthlyCap - blrSpend.Amount, blrPolicy.LocalCurrency);

        var unifiedAmount =
            (osloSpend.Amount * _fx.GetExchangeRate(osloSpend.Currency, reportingCurrency)) +
            (blrSpend.Amount * _fx.GetExchangeRate(blrSpend.Currency, reportingCu
