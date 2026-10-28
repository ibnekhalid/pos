using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Branding;
using Restaurant.Contracts.Tenancy;
using Restaurant.Domain.Constants;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

public class BrandingAndPlatformTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // Smallest valid PNG (1x1 transparent pixel).
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    [Fact]
    public async Task Branding_starts_with_the_restaurant_details_and_can_be_changed()
    {
        var owner = await factory.RegisterOwnerAsync("Lahori Chargha");
        var branding = await owner.GetAsync<BrandingDto>("/api/tenant/branding");
        branding.DisplayName.Should().Be("Lahori Chargha");
        branding.DefaultReceiptTemplateId.Should().Be(SystemReceiptTemplates.Thermal80mmId);
        branding.DefaultKitchenTemplateId.Should().Be(SystemReceiptTemplates.Kitchen80mmId);
        branding.AvailableTemplates.Select(t => t.Code).Should().Contain(["thermal_80mm", "thermal_58mm", "a4_invoice", "kitchen_80mm"]);

        var request = new UpdateBrandingRequest("#0F766E", "#374151", "#F59E0B", "#111827", "#FFFFFF", "Lahori Chargha", "Since 1985",
            "Liberty Market", "042-111-222", null, "Thank you!", "Sales tax invoice", "Visit again", "ur", null,
            SystemReceiptTemplates.Thermal58mmId, SystemReceiptTemplates.Kitchen80mmId);
        (await owner.RawPutAsync("/api/tenant/branding", request with { PrimaryColor = "teal" })).ShouldBe(HttpStatusCode.BadRequest);
        (await owner.RawPutAsync("/api/tenant/branding", request with { DefaultReceiptTemplateId = Guid.NewGuid() })).ShouldBe(HttpStatusCode.NotFound);

        var saved = await owner.PutAsync<BrandingDto>("/api/tenant/branding", request);
        saved.PrimaryColor.Should().Be("#0F766E");
        saved.ReceiptLanguage.Should().Be("ur");
        saved.DefaultReceiptTemplateId.Should().Be(SystemReceiptTemplates.Thermal58mmId);

        var (cashier, _, _) = await factory.AddStaffAsync(owner, ["Cashier"]);
        (await cashier.GetAsync<BrandingDto>("/api/tenant/branding")).PrimaryColor.Should().Be("#0F766E");
        (await cashier.RawPutAsync("/api/tenant/branding", request)).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Logo_upload_accepts_images_only_and_is_served()
    {
        var owner = await factory.RegisterOwnerAsync();

        using var text = new MultipartFormDataContent { { new ByteArrayContent("hello"u8.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") } }, "logo", "logo.txt" } };
        (await owner.Http.PostAsync("/api/tenant/branding/logo", text)).ShouldBe(HttpStatusCode.BadRequest);

        using var image = new MultipartFormDataContent { { new ByteArrayContent(Png) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }, "logo", "logo.png" } };
        var branding = await TestApi.ReadAsync<BrandingDto>(owner.Http.PostAsync("/api/tenant/branding/logo", image));
        branding.LogoUrl.Should().StartWith("/uploads/branding/");

        var served = await owner.Http.GetAsync(branding.LogoUrl);
        served.ShouldBe(HttpStatusCode.OK);
        (await served.Content.ReadAsByteArrayAsync()).Should().Equal(Png);
    }

    [Fact]
    public async Task Platform_admin_can_suspend_a_restaurant_but_owners_cannot_reach_platform_endpoints()
    {
        var owner = await factory.RegisterOwnerAsync();
        (await owner.RawGetAsync("/api/platform/tenants")).ShouldBe(HttpStatusCode.Forbidden);

        var adminAuth = await factory.LoginAsync(ApiFactory.PlatformAdminEmail, ApiFactory.PlatformAdminPassword);
        adminAuth.TenantId.Should().BeNull();
        var admin = factory.CallerFor(adminAuth);
        (await admin.RawGetAsync("/api/tenant")).ShouldBe(HttpStatusCode.Forbidden);
        (await admin.GetAsync<List<PlatformTenantDto>>("/api/platform/tenants")).Should().Contain(t => t.Id == owner.TenantId);

        (await admin.RawPostAsync($"/api/platform/tenants/{owner.TenantId}/suspend", new { })).ShouldBe(HttpStatusCode.NoContent);
        (await owner.RawGetAsync("/api/tenant")).ShouldBe(HttpStatusCode.Forbidden);

        (await admin.RawPostAsync($"/api/platform/tenants/{owner.TenantId}/activate", new { })).ShouldBe(HttpStatusCode.NoContent);
        (await owner.RawGetAsync("/api/tenant")).ShouldBe(HttpStatusCode.OK);
    }
}
