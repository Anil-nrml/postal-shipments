using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PostalService.BuildingBlocks.Application;
using PostalService.BuildingBlocks.Infrastructure.Messaging;
using PostalService.Shipments.Application;
using PostalService.Shipments.Domain;
using PostalService.Shipments.Infrastructure.Persistence;

namespace PostalService.Shipments.IntegrationTests;

public sealed class ShipmentsApiTests : IClassFixture<ShipmentsApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ShipmentsApiFactory _factory;

    public ShipmentsApiTests(ShipmentsApiFactory factory) => _factory = factory;

    private async Task<ShipmentDto> CreateAsync(HttpClient client, string origin = "500001", string destination = "560001",
        decimal kg = 2m, ShipmentType type = ShipmentType.Package)
    {
        var response = await client.PostAsJsonAsync("/api/shipments", new CreateShipmentRequest(type, kg, origin, destination), Json);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ShipmentDto>(Json))!;
    }

    [Fact]
    public async Task Create_then_get_round_trips_through_sqlite()
    {
        var client = _factory.Operator();
        var created = await CreateAsync(client, kg: 0.75m, type: ShipmentType.Letter);

        var fetched = await client.GetFromJsonAsync<ShipmentDto>($"/api/shipments/{created.Id}", Json);

        fetched!.ShipmentNumber.Should().Be(created.ShipmentNumber);
        fetched.WeightKg.Should().Be(0.75m);
        fetched.WeightCategory.Should().Be(WeightCategory.LessThan1Kg);
        fetched.Status.Should().Be(ShipmentStatus.ReceivedAtOrigin);
        fetched.CreatedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Invalid_input_returns_400_problem_details()
    {
        var response = await _factory.Operator().PostAsJsonAsync("/api/shipments",
            new CreateShipmentRequest(ShipmentType.Package, -1, "x", "560001"), Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem!.Errors.Keys.Should().Contain(new[] { "weightKg", "originZip" });
    }

    [Fact]
    public async Task Inactive_post_office_returns_422()
    {
        var response = await _factory.Operator().PostAsJsonAsync("/api/shipments",
            new CreateShipmentRequest(ShipmentType.Package, 1, "500001", "110001"), Json);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("postoffice.inactive");
    }

    [Fact]
    public async Task Post_offices_outage_returns_503_and_saves_nothing()
    {
        var client = _factory.Operator();
        var before = (await client.GetFromJsonAsync<PagedResult<ShipmentDto>>("/api/shipments?pageSize=1", Json))!.TotalRecords;
        _factory.PostOffices.Unavailable = true;
        try
        {
            var response = await client.PostAsJsonAsync("/api/shipments",
                new CreateShipmentRequest(ShipmentType.Package, 1, "500001", "560001"), Json);
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter.Should().NotBeNull();
        }
        finally
        {
            _factory.PostOffices.Unavailable = false;
        }

        var after = (await client.GetFromJsonAsync<PagedResult<ShipmentDto>>("/api/shipments?pageSize=1", Json))!.TotalRecords;
        after.Should().Be(before);
    }

    [Fact]
    public async Task Idempotency_key_prevents_duplicate_shipments()
    {
        var client = _factory.Operator();
        var key = Guid.NewGuid().ToString();
        HttpRequestMessage Request() => new(HttpMethod.Post, "/api/shipments")
        {
            Content = JsonContent.Create(new CreateShipmentRequest(ShipmentType.Package, 3, "500001", "560001"), options: Json),
            Headers = { { "Idempotency-Key", key } },
        };

        var first = await client.SendAsync(Request());
        var second = await client.SendAsync(Request());

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<ShipmentDto>(Json))!.Id
            .Should().Be((await first.Content.ReadFromJsonAsync<ShipmentDto>(Json))!.Id);
    }

    [Fact]
    public async Task Status_flow_records_history_and_rejects_stale_versions()
    {
        var client = _factory.Operator();
        var s = await CreateAsync(client);

        var ok = await client.PatchAsJsonAsync($"/api/shipments/{s.Id}/status",
            new ChangeStatusRequest(ShipmentStatus.ProcessedAtOrigin, "500001", "sorted", s.Version), Json);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await ok.Content.ReadFromJsonAsync<ShipmentDto>(Json))!;
        updated.Version.Should().Be(s.Version + 1);

        // A second clerk still holding the old version loses: 409, no history row added.
        var stale = await client.PatchAsJsonAsync($"/api/shipments/{s.Id}/status",
            new ChangeStatusRequest(ShipmentStatus.InTransit, "500001", null, s.Version), Json);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var invalid = await client.PatchAsJsonAsync($"/api/shipments/{s.Id}/status",
            new ChangeStatusRequest(ShipmentStatus.Delivered, "560001", null, updated.Version), Json);
        invalid.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var history = await client.GetFromJsonAsync<List<ShipmentHistoryDto>>($"/api/shipments/{s.Id}/history", Json);
        history!.Select(h => h.Status).Should().Equal(ShipmentStatus.ReceivedAtOrigin, ShipmentStatus.ProcessedAtOrigin);
        history[1].PerformedBy.Should().Be("test-user");

        var tracking = await client.GetFromJsonAsync<TrackingDto>($"/api/tracking/{s.ShipmentNumber}", Json);
        tracking!.History.Should().HaveCount(2);
    }

    [Fact]
    public async Task Status_change_writes_an_outbox_message_in_the_same_transaction()
    {
        var client = _factory.Operator();
        var s = await CreateAsync(client);
        await client.PatchAsJsonAsync($"/api/shipments/{s.Id}/status",
            new ChangeStatusRequest(ShipmentStatus.ProcessedAtOrigin, "500001", null, s.Version), Json);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShipmentsDbContext>();
        var types = await db.Set<OutboxMessage>().Where(m => m.AggregateId == s.Id.ToString())
            .Select(m => m.EventType).ToListAsync();
        types.Should().BeEquivalentTo("shipment.created.v1", "shipment.status-changed.v1");
    }

    [Fact]
    public async Task List_supports_combined_filters_and_paging_metadata()
    {
        var client = _factory.Operator();
        var heavy = await CreateAsync(client, kg: 12m);
        await CreateAsync(client, kg: 0.2m);

        var page = await client.GetFromJsonAsync<PagedResult<ShipmentDto>>(
            "/api/shipments?weightCategory=Above5Kg&status=ReceivedAtOrigin&originPostOffice=500001&pageSize=1&pageNumber=1&sortBy=weight&sortDirection=desc",
            Json);

        page!.Items.Should().ContainSingle();
        page.Items.Should().OnlyContain(i => i.WeightCategory == WeightCategory.Above5Kg);
        page.PageSize.Should().Be(1);
        page.TotalRecords.Should().BeGreaterThanOrEqualTo(1);

        var byNumber = await client.GetFromJsonAsync<PagedResult<ShipmentDto>>(
            $"/api/shipments?shipmentNumber={heavy.ShipmentNumber[..^2].ToLowerInvariant()}", Json);
        byNumber!.Items.Should().Contain(i => i.Id == heavy.Id, "prefix search is case-insensitive");
    }

    [Fact]
    public async Task List_rejects_bad_paging_input()
    {
        var response = await _factory.Operator().GetAsync("/api/shipments?pageSize=1000");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_is_allowed_only_before_processing()
    {
        var client = _factory.Operator();
        var fresh = await CreateAsync(client);
        (await client.DeleteAsync($"/api/shipments/{fresh.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/shipments/{fresh.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var processed = await CreateAsync(client);
        await client.PatchAsJsonAsync($"/api/shipments/{processed.Id}/status",
            new ChangeStatusRequest(ShipmentStatus.ProcessedAtOrigin, "500001", null, processed.Version), Json);
        (await client.DeleteAsync($"/api/shipments/{processed.Id}")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Anonymous_requests_get_401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/shipments");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Viewer_cannot_create_or_correct()
    {
        var viewer = _factory.ClientWith(Permissions.ShipmentsRead);
        (await viewer.GetAsync("/api/shipments")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsJsonAsync("/api/shipments", new CreateShipmentRequest(ShipmentType.Letter, 0.1m, "500001", "560001"), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var s = await CreateAsync(_factory.Operator());
        (await _factory.Operator().PostAsJsonAsync($"/api/shipments/{s.Id}/status/corrections",
                new CorrectStatusRequest(ShipmentStatus.Lost, "500001", "Recorded on the wrong parcel", s.Version), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.Admin().PostAsJsonAsync($"/api/shipments/{s.Id}/status/corrections",
                new CorrectStatusRequest(ShipmentStatus.Lost, "500001", "Recorded on the wrong parcel", s.Version), Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cookie_authenticated_writes_require_the_csrf_header()
    {
        var client = _factory.Operator();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/shipments")
        {
            Content = JsonContent.Create(new CreateShipmentRequest(ShipmentType.Letter, 0.1m, "500001", "560001"), options: Json),
        };
        request.Headers.Add("Cookie", "access_token=abc; csrf_token=expected");

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Statistics_count_by_status()
    {
        var client = _factory.Operator();
        await CreateAsync(client);
        var stats = await client.GetFromJsonAsync<ShipmentStatistics>("/api/shipments/statistics", Json);
        stats!.Total.Should().BeGreaterThan(0);
        stats.ByStatus[nameof(ShipmentStatus.ReceivedAtOrigin)].Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Internal_endpoints_require_the_api_key()
    {
        var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync("/internal/postoffices/500001/dependencies")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        anonymous.DefaultRequestHeaders.Add("X-Internal-Api-Key", "test-internal-key");
        var deps = await anonymous.GetFromJsonAsync<PostOfficeDependencies>("/internal/postoffices/500001/dependencies", Json);
        deps!.ZipCode.Should().Be("500001");
    }

    [Fact]
    public async Task Post_office_event_is_applied_once()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", "test-internal-key");
        var payload = JsonSerializer.SerializeToElement(new PostOfficeChangedV1(Guid.NewGuid(), "Created", "400001", "Mumbai GPO",
            "Mumbai", true, 1), OutboxJson.Options);
        var envelope = new IntegrationEventEnvelope(Guid.NewGuid(), PostOfficeChangedV1.EventType, DateTime.UtcNow, "postoffices", payload);

        (await client.PostAsJsonAsync("/internal/events", envelope, OutboxJson.Options)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await client.PostAsJsonAsync("/internal/events", envelope, OutboxJson.Options)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShipmentsDbContext>();
        (await db.PostOfficeReferences.CountAsync(r => r.ZipCode == "400001")).Should().Be(1);
        (await db.Set<InboxMessage>().CountAsync(m => m.EventId == envelope.EventId)).Should().Be(1);
    }
}
