using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Framsikt.SnackAutomation.Tests;

public class SnackCounterTests
{
    private sealed class SpyChatGateway : IChatGateway
    {
        public List<(string EmployeeId, Guid RefillId, Money MaxSpend)> DirectOffers { get; } = new();
        public List<(Office Office, Guid RefillId)> Broadcasts { get; } = new();
        public List<(Office Office, Guid RefillId, string VolunteerName)> ClaimNotifications { get; } = new();

        public Task SendDirectOfferAsync(string employeeId, Guid refillId, Office office, string category, Money maxApprovedSpend)
        {
            DirectOffers.Add((employeeId, refillId, maxApprovedSpend));
            return Task.CompletedTask;
        }

        public Task BroadcastOpenRefillAsync(Office office, Guid refillId, string category, Money maxApprovedSpend)
        {
            Broadcasts.Add((office, refillId));
            return Task.CompletedTask;
        }

        public Task NotifyOfficeClaimedAsync(Office office, Guid refillId, string category, string volunteerName)
        {
            ClaimNotifications.Add((office, refillId, volunteerName));
            return Task.CompletedTask;
        }
    }

    private sealed class SpyHrClient : IHrSystemClient
    {
        public List<HrClaimRequest> SubmittedClaims { get; } = new();

        public Task<string> CreateReimbursementClaimAsync(HrClaimRequest request)
        {
            SubmittedClaims.Add(request);
            return Task.FromResult($"HR-CLAIM-{SubmittedClaims.Count}");
        }
    }

    private sealed class DeterministicFxProvider : IExchangeRateProvider
    {
        public decimal GetExchangeRate(Currency from, Currency to) => (from, to) switch
        {
            (Currency.NOK, Currency.NOK) => 1.0m,
            (Currency.INR, Currency.INR) => 1.0m,
            (Currency.INR, Currency.NOK) => 0.125m, // 8 INR = 1 NOK
            (Currency.NOK, Currency.INR) => 8.0m,
            _ => throw new NotSupportedException()
        };
    }

    private static (SnackCounterAutomationService Service, SpyChatGateway Chat, SpyHrClient Hr) CreateSut()
    {
        var chat = new SpyChatGateway();
        var hr = new SpyHrClient();
        var fx = new DeterministicFxProvider();
        return (new SnackCounterAutomationService(chat, hr, fx), chat, hr);
    }

    [Fact]
    public async Task Rule1_TwoVolunteers_CannotCommitToSameRefill_AndDuplicateReportsAreMerged()
    {
        var (sut, _, _) = CreateSut();
        var now = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

        sut.UpsertVolunteer(new Volunteer("oslo-1", "Ingrid", Office.Oslo, Office.Oslo, Currency.NOK, isInOfficeToday: true));
        sut.UpsertVolunteer(new Volunteer("oslo-2", "Magnus", Office.Oslo, Office.Oslo, Currency.NOK, isInOfficeToday: true));

        // Concurrent empty reports for the same category return the exact same active RefillRequest
        var refill1 = await sut.ReportEmptyBoxAsync(Office.Oslo, "Fruit", now);
        var refill2 = await sut.ReportEmptyBoxAsync(Office.Oslo, "fruit ", now.AddMinutes(2));
        Assert.Equal(refill1.Id, refill2.Id);

        // First volunteer claims successfully; second volunteer is rejected
        var firstClaim = await sut.TryClaimRefillAsync(refill1.Id, "oslo-1", now.AddMinutes(5));
        var secondClaim = await sut.TryClaimRefillAsync(refill1.Id, "oslo-2", now.AddMinutes(6));

        Assert.True(firstClaim);
        Assert.False(secondClaim);
        Assert.Equal("oslo-1", refill1.ClaimedByVolunteerId);
    }

    [Fact]
    public async Task Rule2_RefillNobodyCompletes_AutomaticallyHandsOffOnBothOfferAndClaimTimeouts()
    {
        var (sut, chat, _) = CreateSut();
        var t0 = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

        // Three volunteers in Bengaluru; remote volunteer is WFH and must be skipped
        sut.UpsertVolunteer(new Volunteer("blr-wfh", "Rohan", Office.Bengaluru, Office.Bengaluru, Currency.INR, isInOfficeToday: false, lastRefillAt: null));
        sut.UpsertVolunteer(new Volunteer("blr-1", "Asha", Office.Bengaluru, Office.Bengaluru, Currency.INR, isInOfficeToday: true, lastRefillAt: t0.AddDays(-30)));
        sut.UpsertVolunteer(new Volunteer("blr-2", "Vikram", Office.Bengaluru, Office.Bengaluru, Currency.INR, isInOfficeToday: true, lastRefillAt: t0.AddDays(-15)));
        sut.UpsertVolunteer(new Volunteer("blr-3", "Deepa", Office.Bengaluru, Office.Bengaluru, Currency.INR, isInOfficeToday: true, lastRefillAt: t0.AddDays(-5)));

        // 1. Box reported empty -> Offered to Asha (longest since last refill among on-site staff)
        var refill = await sut.ReportEmptyBoxAsync(Office.Bengaluru, "Nuts", t0);
        Assert.Equal("blr-1", chat.DirectOffers.Last().EmployeeId);

        // 2. Asha ignores the chat offer for > 2 hours -> auto-rotates to Vikram
        await sut.ProcessTimeoutsAsync(t0.AddHours(2).AddMinutes(1));
        Assert.Equal("blr-2", chat.DirectOffers.Last().EmployeeId);

        // 3. Vikram claims the refill, but forgets to buy snacks for > 24 hours -> auto-rotates to Deepa
        var claimedAt = t0.AddHours(2).AddMinutes(10);
        Assert.True(await sut.TryClaimRefillAsync(refill.Id, "blr-2", claimedAt));
        await sut.ProcessTimeoutsAsync(claimedAt.AddHours(24).AddMinutes(1));

        Assert.Equal(RefillState.Offered, refill.State);
        Assert.Equal("blr-3", chat.DirectOffers.Last().EmployeeId);
    }

    [Fact]
    public async Task Rule3_VolunteerWaitingForHrReimbursement_IsProtectedFromFrontingMoreMoney()
    {
        var (sut, chat, _) = CreateSut();
        var now = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

        sut.UpsertVolunteer(new Volunteer("oslo-1", "Ingrid", Office.Oslo, Office.Oslo, Currency.NOK, isInOfficeToday: true, lastRefillAt: now.AddDays(-40)));
        sut.UpsertVolunteer(new Volunteer("oslo-2", "Magnus", Office.Oslo, Office.Oslo, Currency.NOK, isInOfficeToday: true, lastRefillAt: now.AddDays(-2)));

        // Ingrid claims and buys Coffee (she is now PurchasedPendingHr waiting 1-10 days)
        var coffeeRefill = await sut.ReportEmptyBoxAsync(Office.Oslo, "Coffee", now);
        await sut.TryClaimRefillAsync(coffeeRefill.Id, "oslo-1", now);
        await sut.SubmitPurchaseReceiptAsync(coffeeRefill.Id, "oslo-1", new Money(600m, Currency.NOK), "https://receipts/coffee.jpg", now);

        // Even if Ingrid had an older timestamp, she has an unpaid HR claim, so the next refill goes to Magnus
        var fruitRefill = await sut.ReportEmptyBoxAsync(Office.Oslo, "Fruit", now.AddHours(1));
        Assert.Equal("oslo-2", chat.DirectOffers.Last().EmployeeId);
        Assert.NotEqual(coffeeRefill.Id, fruitRefill.Id);
    }

    [Fact]
    public async Task Rule4_VisitingEmployee_DeductsLocalOfficeBudget_ButGetsReimbursedInPayrollCurrency()
    {
        var (sut, _, hr) = CreateSut();
        var now = new DateTimeOffset(2026, 11, 15, 11, 0, 0, TimeSpan.Zero);

        // Erik is an Oslo employee (NOK payroll) visiting the Bengaluru office for 2 weeks
        var visitingErik = new Volunteer(
            employeeId: "oslo-visitor",
            name: "Erik",
            homeOffice: Office.Oslo,
            currentOffice: Office.Bengaluru,
            payrollCurrency: Currency.NOK,
            isInOfficeToday: true);
        sut.UpsertVolunteer(visitingErik);

        var refill = await sut.ReportEmptyBoxAsync(Office.Bengaluru, "Samosas", now);
        Assert.True(await sut.TryClaimRefillAsync(refill.Id, "oslo-visitor", now));

        // Erik spends 1,600 INR in Bengaluru
        await sut.SubmitPurchaseReceiptAsync(
            refill.Id,
            "oslo-visitor",
            new Money(1_600m, Currency.INR),
            "https://receipts/blr-snacks.jpg",
            now);

        // 1. Bengaluru local budget is deducted by 1,600 INR (15,000 - 1,600 = 13,400 INR)
        var report = sut.GenerateMonthlyReport(2026, 11, Currency.NOK);
        Assert.Equal(new Money(1_600m, Currency.INR), report.BengaluruTotalSpend);
        Assert.Equal(new Money(13_400m, Currency.INR), report.BengaluruRemainingBudget);

        // 2. HR reimbursement claim was submitted in Erik's payroll currency: 1,600 INR * 0.125 = 200 NOK
        var hrClaim = Assert.Single(hr.SubmittedClaims);
        Assert.Equal("oslo-visitor", hrClaim.EmployeeId);
        Assert.Equal(Currency.NOK, hrClaim.Currency);
        Assert.Equal(200m, hrClaim.Amount);
    }

    [Fact]
    public async Task Rule5_BudgetsPreReserveFunds_AndUnifiedReportTracksMultiCurrencyAndConsumptionVelocity()
    {
        var (sut, _, _) = CreateSut();
        var day1 = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

        sut.UpsertVolunteer(new Volunteer("oslo-1", "Ingrid", Office.Oslo, Office.Oslo, Currency.NOK, isInOfficeToday: true));
        sut.UpsertVolunteer(new Volunteer("blr-1", "Asha", Office.Bengaluru, Office.Bengaluru, Currency.INR, isInOfficeToday: true));

        // Oslo Refill 1: Fruit on Nov 2 (1,200 NOK) -> Reimbursed so Ingrid can refill again
        var r1 = await sut.ReportEmptyBoxAsync(Office.Oslo, "Fruit", day1);
        await sut.TryClaimRefillAsync(r1.Id, "oslo-1", day1);
        await sut.SubmitPurchaseReceiptAsync(r1.Id, "oslo-1", new Money(1_200m, Currency.NOK), "https://r/1.jpg", day1);
        sut.MarkHrClaimReimbursed(r1.Id, day1.AddDays(2));

        // Oslo Refill 2: Fruit on Nov 6 (4 days later, 800 NOK) -> Total Oslo = 2,000 NOK
        var day5 = day1.AddDays(4);
        var r2 = await sut.ReportEmptyBoxAsync(Office.Oslo, "Fruit", day5);
        await sut.TryClaimRefillAsync(r2.Id, "oslo-1", day5);
        await sut.SubmitPurchaseReceiptAsync(r2.Id, "oslo-1", new Money(800m, Currency.NOK), "https://r/2.jpg", day5);

        // Bengaluru Refill: Coffee on Nov 5 (2,400 INR = 300 NOK)
        var r3 = await sut.ReportEmptyBoxAsync(Office.Bengaluru, "Coffee", day5);
        await sut.TryClaimRefillAsync(r3.Id, "blr-1", day5);
        await sut.SubmitPurchaseReceiptAsync(r3.Id, "blr-1", new Money(2_400m, Currency.INR), "https://r/3.jpg", day5);

        var report = sut.GenerateMonthlyReport(2026, 11, Currency.NOK);

        // Unified total: 2,000 NOK + (2,400 INR * 0.125) = 2,300 NOK
        Assert.Equal(new Money(2_300m, Currency.NOK), report.UnifiedTotalSpend);
        Assert.Equal(new Money(4_000m, Currency.NOK), report.OsloRemainingBudget);
        Assert.Equal(new Money(12_600m, Currency.INR), report.BengaluruRemainingBudget);

        // Consumption velocity: Oslo Fruit was refilled twice, 4.0 days apart
        var fruitMetric = report.CategoryBreakdown.Single(c => c.Office == Office.Oslo && c.SnackCategory == "Fruit");
        Assert.Equal(2, fruitMetric.RefillCount);
        Assert.Equal(4.0, fruitMetric.AverageDaysBetweenRefills);
    }
}
