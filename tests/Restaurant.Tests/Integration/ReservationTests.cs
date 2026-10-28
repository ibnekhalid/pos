using System.Net;
using FluentAssertions;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Tables;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

/// <summary>Bookings hold tables; tables need cleaning after guests leave.</summary>
public class ReservationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_booking_holds_its_table_and_is_seated_or_cancelled()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;
        var at = DateTimeOffset.UtcNow.AddHours(1);

        var booking = await owner.PostAsync<ReservationDto>("/api/reservations", new SaveReservationRequest("Ayesha Khan", "03001112233", 4, at, 90, pos.Table1, "Birthday"));
        booking.Status.Should().Be(ReservationStatus.Booked);
        booking.TableNumber.Should().Be("1");

        // Overlapping on the same table: refused. After it ends, or on another table: fine.
        (await owner.RawPostAsync("/api/reservations", new SaveReservationRequest("Bilal", null, 2, at.AddMinutes(60), 60, pos.Table1, null))).ShouldBe(HttpStatusCode.Conflict);
        var later = await owner.PostAsync<ReservationDto>("/api/reservations", new SaveReservationRequest("Bilal", null, 2, at.AddMinutes(90), 60, pos.Table1, null));
        var elsewhere = await owner.PostAsync<ReservationDto>("/api/reservations", new SaveReservationRequest("Sana", null, 2, at, 60, pos.Table2, null));

        // The table map shows the next booking.
        var table1 = (await owner.GetAsync<List<TableDto>>("/api/tables")).Single(t => t.Id == pos.Table1);
        table1.ReservedFor.Should().Be("Ayesha Khan");
        table1.ReservationId.Should().Be(booking.Id);

        var today = TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi")).Date;
        (await owner.GetAsync<List<ReservationDto>>($"/api/reservations?date={today:yyyy-MM-dd}")).Should().Contain(r => r.Id == booking.Id);

        var seated = await owner.PostAsync<ReservationDto>($"/api/reservations/{booking.Id}/seat", new SeatReservationRequest(null));
        seated.Status.Should().Be(ReservationStatus.Seated);
        seated.SeatedAt.Should().NotBeNull();
        (await owner.RawPostAsync($"/api/reservations/{booking.Id}/cancel", new { })).ShouldBe(HttpStatusCode.Conflict);
        (await owner.PostAsync<ReservationDto>($"/api/reservations/{later.Id}/no-show", new { })).Status.Should().Be(ReservationStatus.NoShow);
        (await owner.PostAsync<ReservationDto>($"/api/reservations/{elsewhere.Id}/cancel", new { })).Status.Should().Be(ReservationStatus.Cancelled);
        (await owner.GetAsync<List<TableDto>>("/api/tables")).Single(t => t.Id == pos.Table2).ReservedFor.Should().BeNull();

        // Kitchen staff can't take bookings; another restaurant can't touch them.
        var (kitchen, _, _) = await factory.AddStaffAsync(owner, ["Kitchen staff"]);
        kitchen.UseBranch(owner.BranchId);
        (await kitchen.RawGetAsync($"/api/reservations?date={today:yyyy-MM-dd}")).ShouldBe(HttpStatusCode.Forbidden);
        var other = await factory.SetUpRestaurantAsync();
        (await other.Owner.RawPostAsync($"/api/reservations/{later.Id}/cancel", new { })).ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_table_needs_cleaning_after_its_guests_leave()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;

        var order = await owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder(table: pos.Table2));
        await owner.PayCashAsync(order.Id, order.Total, order.Total, pos);
        (await owner.GetAsync<List<TableDto>>("/api/tables")).Single(t => t.Id == pos.Table2).NeedsCleaning.Should().BeFalse(); // still occupied
        (await owner.PostAsync<OrderDto>($"/api/orders/{order.Id}/served", new { })).Status.Should().Be(OrderStatus.Completed);

        var table = (await owner.GetAsync<List<TableDto>>("/api/tables")).Single(t => t.Id == pos.Table2);
        table.OpenOrderId.Should().BeNull();
        table.NeedsCleaning.Should().BeTrue();

        (await owner.PostAsync<TableDto>($"/api/tables/{pos.Table2}/clean", new { })).NeedsCleaning.Should().BeFalse();
        (await owner.GetAsync<List<TableDto>>("/api/tables")).Single(t => t.Id == pos.Table2).NeedsCleaning.Should().BeFalse();
    }
}
