using FluentAssertions;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Sessions;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

/// <summary>T4: a day started Monday morning and closed after midnight stays Monday's.</summary>
public class BusinessDayClockTests(ClockApiFactory factory) : IClassFixture<ClockApiFactory>
{
    [Fact]
    public async Task A_session_running_past_midnight_keeps_its_business_date()
    {
        var pos = await factory.SetUpRestaurantAsync(startDay: false);
        factory.Clock.Now = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(5)); // Mon 10:00
        var session = await pos.Owner.PostAsync<BusinessSessionDto>("/api/sessions/start", new StartSessionRequest(0, null));
        session.BusinessDate.Should().Be(new DateOnly(2026, 11, 2));

        factory.Clock.Now = new DateTimeOffset(2026, 11, 3, 2, 0, 0, TimeSpan.FromHours(5)); // Tue 02:00
        var order = await pos.Owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder(send: false));
        order.BusinessSessionId.Should().Be(session.Id);
        await pos.Owner.PayCashAsync(order.Id, order.Total, order.Total, pos);
        await pos.Owner.PostAsync<OrderDto>($"/api/orders/{order.Id}/send", new { });
        await pos.Owner.PostAsync<OrderDto>($"/api/orders/{order.Id}/served", new { });

        factory.Clock.Now = new DateTimeOffset(2026, 11, 3, 2, 30, 0, TimeSpan.FromHours(5));
        var preview = await pos.Owner.GetAsync<ClosePreviewDto>("/api/sessions/current/close-preview");
        var closed = await pos.Owner.PostAsync<ClosedSessionDto>("/api/sessions/current/close",
            new CloseSessionRequest([new(preview.Summary.Cash[0].RegisterSessionId, preview.Summary.Cash[0].ExpectedCash, null)], null));
        closed.Session.BusinessDate.Should().Be(new DateOnly(2026, 11, 2));

        // The receipt prints the business date (Monday) and the real time (Tuesday 2 AM).
        var receipt = await pos.Owner.GetAsync<Restaurant.Contracts.Receipts.ReceiptDocumentDto>($"/api/orders/{order.Id}/receipt");
        receipt.BusinessDate.Should().Be("02 Nov 2026");
        receipt.PrintedAt.Should().StartWith("03 Nov 2026");

        // The next day started Tuesday morning is Tuesday's.
        factory.Clock.Now = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(5));
        (await pos.Owner.PostAsync<BusinessSessionDto>("/api/sessions/start", new StartSessionRequest(0, null))).BusinessDate.Should().Be(new DateOnly(2026, 11, 3));
        (await pos.Owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder(send: false))).OrderNumber.Should().Be("001");
    }
}
