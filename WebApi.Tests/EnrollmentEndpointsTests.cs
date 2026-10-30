using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentEndpointsTests
{
    private static WebApplicationFactory<Program> _factory = null!;
    private static HttpClient _client = null!;

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        _factory = new ApiApplicationFactory();
        _client = _factory.CreateClient();
    }

    [TestMethod]
    public async Task Schedule_ShouldReturnItemsForAuthenticatedUser()
    {
        var token = await LoginAsync("user001@club.test");

        var request = new HttpRequestMessage(HttpMethod.Get,
            "/schedule?from=2026-09-01T00:00:00Z&to=2026-09-30T00:00:00Z&limit=20");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task CreateEnrollment_WithoutIdempotencyKey_ShouldFailWith400()
    {
        var token = await LoginAsync("user001@club.test");

        var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments")
        {
            Content = new StringContent("{\"classId\":1,\"seats\":1}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task MemberCanBookViewResizeAndCancelEnrollmentThroughApi()
    {
        const string email = "user001@club.test";
        const int capacity = 10;
        const int initialPassVisits = 20;
        var startsAt = DateTime.UtcNow.AddDays(3);
        long classId;

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var user = await dbContext.Users.SingleAsync(x => x.Email == email);
            var pass = await dbContext.Passes.SingleAsync(x => x.UserId == user.Id);
            var room = await dbContext.Rooms.OrderBy(x => x.Id).FirstAsync();

            pass.RemainingVisits = initialPassVisits;
            pass.ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);

            var scheduledClass = new Class
            {
                RoomId = room.Id,
                Title = "Enrollment integration scenario",
                StartsAt = startsAt,
                DurationMinutes = 60,
                Capacity = capacity,
                IsCancelled = false
            };

            dbContext.Classes.Add(scheduledClass);
            await dbContext.SaveChangesAsync();
            classId = scheduledClass.Id;
        }

        var token = await LoginAsync(email);
        using var createResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            token,
            new { classId, seats = 2 },
            "integration-booking-key");
        Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);

        using var createdBody = await JsonDocument.ParseAsync(await createResponse.Content.ReadAsStreamAsync());
        var enrollmentId = createdBody.RootElement.GetProperty("id").GetInt64();
        Assert.AreEqual(classId, createdBody.RootElement.GetProperty("classId").GetInt64());
        Assert.AreEqual(2, createdBody.RootElement.GetProperty("seats").GetInt32());
        Assert.AreEqual("active", createdBody.RootElement.GetProperty("status").GetString());

        using var replayResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            token,
            new { classId, seats = 2 },
            "integration-booking-key");
        Assert.AreEqual(HttpStatusCode.Created, replayResponse.StatusCode);
        using var replayBody = await JsonDocument.ParseAsync(await replayResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual(enrollmentId, replayBody.RootElement.GetProperty("id").GetInt64());

        var from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(4).ToString("O"));
        using var scheduleResponse = await SendAuthorizedAsync(
            HttpMethod.Get,
            $"/schedule?from={from}&to={to}",
            token);
        Assert.AreEqual(HttpStatusCode.OK, scheduleResponse.StatusCode);
        using var scheduleBody = await JsonDocument.ParseAsync(await scheduleResponse.Content.ReadAsStreamAsync());
        var scheduleItem = scheduleBody.RootElement.GetProperty("items")
            .EnumerateArray()
            .Single(item => item.GetProperty("classId").GetInt64() == classId);
        Assert.AreEqual(capacity - 2, scheduleItem.GetProperty("freeSeats").GetInt32());
        Assert.AreEqual(2, scheduleItem.GetProperty("mySeats").GetInt32());

        using var resizeResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/{enrollmentId}/seats",
            token,
            new { seats = 3 });
        Assert.AreEqual(HttpStatusCode.OK, resizeResponse.StatusCode);
        using var resizeBody = await JsonDocument.ParseAsync(await resizeResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual(3, resizeBody.RootElement.GetProperty("seats").GetInt32());
        Assert.AreEqual(initialPassVisits - 3, resizeBody.RootElement.GetProperty("pass").GetProperty("remainingVisits").GetInt32());

        using var detailResponse = await SendAuthorizedAsync(
            HttpMethod.Get,
            $"/enrollments/{enrollmentId}",
            token);
        Assert.AreEqual(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detailBody = await JsonDocument.ParseAsync(await detailResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual("active", detailBody.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("Enrollment integration scenario", detailBody.RootElement.GetProperty("class").GetProperty("title").GetString());
        var cachedClassDetails = await _factory.Services
            .GetRequiredService<Common.Caching.ICacheService>()
            .GetAsync<WebApi.Controllers.EnrollmentController.CachedClassDetails>($"fitnessclub:class-details:{classId}");
        Assert.IsNotNull(cachedClassDetails);

        using var cancelResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/{enrollmentId}/cancel",
            token);
        Assert.AreEqual(HttpStatusCode.OK, cancelResponse.StatusCode);
        using var cancelBody = await JsonDocument.ParseAsync(await cancelResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual("cancelled", cancelBody.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(3, cancelBody.RootElement.GetProperty("refundedVisits").GetInt32());

        using var repeatCancelResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/{enrollmentId}/cancel",
            token);
        Assert.AreEqual(HttpStatusCode.OK, repeatCancelResponse.StatusCode);
        using var repeatCancelBody = await JsonDocument.ParseAsync(await repeatCancelResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual(3, repeatCancelBody.RootElement.GetProperty("refundedVisits").GetInt32());

        using var finalDetailResponse = await SendAuthorizedAsync(
            HttpMethod.Get,
            $"/enrollments/{enrollmentId}",
            token);
        using var finalDetailBody = await JsonDocument.ParseAsync(await finalDetailResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual("cancelled", finalDetailBody.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(initialPassVisits, finalDetailBody.RootElement.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
    }

    [TestMethod]
    public async Task EnrollmentEndpointsOnlyAllowTheOwningMember()
    {
        const string ownerEmail = "user001@club.test";
        const string otherEmail = "user002@club.test";
        var classId = await CreateClassAsync("Enrollment ownership scenario", 1, DateTime.UtcNow.AddDays(2));
        var enrollmentId = await AddActiveEnrollmentAsync(ownerEmail, classId, 1, remainingVisits: 4);
        await SetPassAsync(otherEmail, remainingVisits: 5);
        var ownerToken = await LoginAsync(ownerEmail);
        var otherToken = await LoginAsync(otherEmail);

        using var getResponse = await SendAuthorizedAsync(HttpMethod.Get, $"/enrollments/{enrollmentId}", otherToken);
        Assert.AreEqual(HttpStatusCode.NotFound, getResponse.StatusCode);

        using var resizeResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/{enrollmentId}/seats",
            otherToken,
            new { seats = 2 });
        Assert.AreEqual(HttpStatusCode.NotFound, resizeResponse.StatusCode);

        using var cancelResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/{enrollmentId}/cancel",
            otherToken);
        Assert.AreEqual(HttpStatusCode.NotFound, cancelResponse.StatusCode);

        using var joinResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            otherToken,
            new { classId, seats = 1 },
            "ownership-waitlist-key");
        Assert.AreEqual(HttpStatusCode.Created, joinResponse.StatusCode);
        using var joinBody = await JsonDocument.ParseAsync(await joinResponse.Content.ReadAsStreamAsync());
        var waitlistId = joinBody.RootElement.GetProperty("id").GetInt64();

        using var leaveResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/waitlist/{waitlistId}/cancel",
            ownerToken);
        Assert.AreEqual(HttpStatusCode.NotFound, leaveResponse.StatusCode);

        Assert.AreEqual(4, (await ReadEnrollmentStateAsync(ownerEmail, classId)).RemainingVisits);
        Assert.AreEqual(5, (await ReadEnrollmentStateAsync(otherEmail, classId)).RemainingVisits);
        Assert.AreEqual(1, (await ReadEnrollmentStateAsync(ownerEmail, classId)).UserActiveEnrollmentCount);
        Assert.AreEqual(1, (await ReadEnrollmentStateAsync(otherEmail, classId)).UserWaitingEnrollmentCount);
    }

    [TestMethod]
    public async Task ScheduleFiltersByRoomAndHalfOpenDateRangeAndPaginatesInStableOrder()
    {
        const string email = "user014@club.test";
        var from = DateTime.UtcNow.Date.AddDays(20);
        var to = from.AddDays(1);
        long firstId;
        long secondId;
        long thirdId;
        long startBoundaryId;
        long boundaryId;
        long cancelledId;
        long otherRoomClassId;
        long roomId;
        long otherRoomId;

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var rooms = await dbContext.Rooms.OrderBy(x => x.Id).Take(2).ToListAsync();
            var room = rooms[0];
            var otherRoom = rooms[1];
            roomId = room.Id;
            otherRoomId = otherRoom.Id;
            var classes = new[]
            {
                new Class { RoomId = roomId, Title = "Schedule from boundary", StartsAt = from, DurationMinutes = 60, Capacity = 4 },
                new Class { RoomId = roomId, Title = "Schedule page one", StartsAt = from.AddHours(1), DurationMinutes = 60, Capacity = 4 },
                new Class { RoomId = roomId, Title = "Schedule page two", StartsAt = from.AddHours(1), DurationMinutes = 60, Capacity = 4 },
                new Class { RoomId = roomId, Title = "Schedule page three", StartsAt = from.AddHours(2), DurationMinutes = 60, Capacity = 4 },
                new Class { RoomId = roomId, Title = "Schedule to boundary", StartsAt = to, DurationMinutes = 60, Capacity = 4 },
                new Class { RoomId = roomId, Title = "Schedule cancelled", StartsAt = from.AddHours(3), DurationMinutes = 60, Capacity = 4, IsCancelled = true },
                new Class { RoomId = otherRoomId, Title = "Schedule other room", StartsAt = from.AddHours(1), DurationMinutes = 60, Capacity = 4 }
            };
            dbContext.Classes.AddRange(classes);
            await dbContext.SaveChangesAsync();
            startBoundaryId = classes[0].Id;
            firstId = classes[1].Id;
            secondId = classes[2].Id;
            thirdId = classes[3].Id;
            boundaryId = classes[4].Id;
            cancelledId = classes[5].Id;
            otherRoomClassId = classes[6].Id;
        }

        var token = await LoginAsync(email);
        var fromQuery = Uri.EscapeDataString(from.ToString("O"));
        var toQuery = Uri.EscapeDataString(to.ToString("O"));
        using var firstPageResponse = await SendAuthorizedAsync(
            HttpMethod.Get,
            $"/schedule?from={fromQuery}&to={toQuery}&roomId={roomId}&limit=2",
            token);
        Assert.AreEqual(HttpStatusCode.OK, firstPageResponse.StatusCode);
        using var firstPageBody = await JsonDocument.ParseAsync(await firstPageResponse.Content.ReadAsStreamAsync());
        var firstPage = firstPageBody.RootElement.GetProperty("items").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(
            new[] { startBoundaryId, firstId },
            firstPage.Select(item => item.GetProperty("classId").GetInt64()).ToArray());
        var continuationToken = firstPageBody.RootElement.GetProperty("continuationToken").GetString();
        Assert.IsFalse(string.IsNullOrEmpty(continuationToken));

        using var secondPageResponse = await SendAuthorizedAsync(
            HttpMethod.Get,
            $"/schedule?from={fromQuery}&to={toQuery}&roomId={roomId}&limit=2&continuationToken={Uri.EscapeDataString(continuationToken!)}",
            token);
        Assert.AreEqual(HttpStatusCode.OK, secondPageResponse.StatusCode);
        using var secondPageBody = await JsonDocument.ParseAsync(await secondPageResponse.Content.ReadAsStreamAsync());
        var secondPage = secondPageBody.RootElement.GetProperty("items").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(
            new[] { secondId, thirdId },
            secondPage.Select(item => item.GetProperty("classId").GetInt64()).ToArray());
        Assert.IsTrue(secondPageBody.RootElement.GetProperty("continuationToken").ValueKind == JsonValueKind.Null);

        var returnedIds = firstPage.Concat(secondPage)
            .Select(item => item.GetProperty("classId").GetInt64())
            .ToArray();
        Assert.IsFalse(returnedIds.Contains(boundaryId));
        Assert.IsFalse(returnedIds.Contains(cancelledId));
        Assert.IsFalse(returnedIds.Contains(otherRoomClassId));
    }

    [TestMethod]
    public async Task ResizeDecreaseRefundsVisitsAndSameSeatCountIsNoOp()
    {
        const string email = "user003@club.test";
        var classId = await CreateClassAsync("Resize decrease and no-op", 5, DateTime.UtcNow.AddDays(2));
        var enrollmentId = await AddActiveEnrollmentAsync(email, classId, seats: 3, remainingVisits: 7);
        await SetPassAsync(email, remainingVisits: 7, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));
        var token = await LoginAsync(email);

        using var decreaseResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/{enrollmentId}/seats",
            token,
            new { seats = 1 });
        Assert.AreEqual(HttpStatusCode.OK, decreaseResponse.StatusCode);
        using var decreaseBody = await JsonDocument.ParseAsync(await decreaseResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual(1, decreaseBody.RootElement.GetProperty("seats").GetInt32());
        Assert.AreEqual(9, decreaseBody.RootElement.GetProperty("pass").GetProperty("remainingVisits").GetInt32());

        var stateAfterDecrease = await ReadEnrollmentStateAsync(email, classId);
        using var sameSeatsResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/{enrollmentId}/seats",
            token,
            new { seats = 1 });
        Assert.AreEqual(HttpStatusCode.OK, sameSeatsResponse.StatusCode);
        using var sameSeatsBody = await JsonDocument.ParseAsync(await sameSeatsResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual(1, sameSeatsBody.RootElement.GetProperty("seats").GetInt32());
        Assert.AreEqual(9, sameSeatsBody.RootElement.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual(stateAfterDecrease, await ReadEnrollmentStateAsync(email, classId));
    }

    [TestMethod]
    public async Task ClubCancelledClassRemainsVisibleAndRefundsExistingBooking()
    {
        const string email = "user001@club.test";
        const int seats = 2;
        long classId;
        long enrollmentId;

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var user = await dbContext.Users.SingleAsync(x => x.Email == email);
            var pass = await dbContext.Passes.SingleAsync(x => x.UserId == user.Id);
            var room = await dbContext.Rooms.OrderBy(x => x.Id).FirstAsync();
            var scheduledClass = new Class
            {
                RoomId = room.Id,
                Title = "Cancelled class integration scenario",
                StartsAt = DateTime.UtcNow.AddHours(-1),
                DurationMinutes = 60,
                Capacity = 8,
                IsCancelled = true
            };

            pass.RemainingVisits = 8;
            dbContext.Classes.Add(scheduledClass);
            await dbContext.SaveChangesAsync();
            classId = scheduledClass.Id;

            var enrollment = new Enrollment
            {
                UserId = user.Id,
                ClassId = classId,
                Seats = seats,
                Status = "active",
                CreatedAt = DateTime.UtcNow
            };
            dbContext.Enrollments.Add(enrollment);
            pass.RemainingVisits -= seats;
            await dbContext.SaveChangesAsync();
            enrollmentId = enrollment.Id;
        }

        var token = await LoginAsync(email);

        using var detailResponse = await SendAuthorizedAsync(HttpMethod.Get, $"/enrollments/{enrollmentId}", token);
        Assert.AreEqual(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detailBody = await JsonDocument.ParseAsync(await detailResponse.Content.ReadAsStreamAsync());
        Assert.IsTrue(detailBody.RootElement.GetProperty("class").GetProperty("isCancelled").GetBoolean());

        using var cancelResponse = await SendAuthorizedAsync(HttpMethod.Post, $"/enrollments/{enrollmentId}/cancel", token);
        Assert.AreEqual(HttpStatusCode.OK, cancelResponse.StatusCode);
        using var cancelBody = await JsonDocument.ParseAsync(await cancelResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual(seats, cancelBody.RootElement.GetProperty("refundedVisits").GetInt32());

        using var createResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            token,
            new { classId, seats = 1 },
            "cancelled-class-create-key");
        Assert.AreEqual(HttpStatusCode.Conflict, createResponse.StatusCode);
        using var createBody = await JsonDocument.ParseAsync(await createResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual("class-cancelled", createBody.RootElement.GetProperty("reason").GetString());
    }

    [TestMethod]
    public async Task CreateIdempotencyReplaysAfterPassIsExhaustedAndAcrossHostInstances()
    {
        const string email = "user004@club.test";
        const string key = "cross-host-idempotency-key";
        long classId;

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var user = await dbContext.Users.SingleAsync(x => x.Email == email);
            var pass = await dbContext.Passes.SingleAsync(x => x.UserId == user.Id);
            var room = await dbContext.Rooms.OrderBy(x => x.Id).FirstAsync();
            pass.RemainingVisits = 1;
            pass.ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);

            var scheduledClass = new Class
            {
                RoomId = room.Id,
                Title = "Idempotency persistence scenario",
                StartsAt = DateTime.UtcNow.AddDays(3),
                DurationMinutes = 60,
                Capacity = 4,
                IsCancelled = false
            };
            dbContext.Classes.Add(scheduledClass);
            await dbContext.SaveChangesAsync();
            classId = scheduledClass.Id;
        }

        var token = await LoginAsync(email);
        using var initialResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            token,
            new { classId, seats = 1 },
            key);
        Assert.AreEqual(HttpStatusCode.Created, initialResponse.StatusCode);
        using var initialBody = await JsonDocument.ParseAsync(await initialResponse.Content.ReadAsStreamAsync());
        var enrollmentId = initialBody.RootElement.GetProperty("id").GetInt64();

        using var secondFactory = new ApiApplicationFactory();
        using var secondClient = secondFactory.CreateClient();
        var secondToken = await LoginAsync(secondClient, email);
        using var retryResponse = await SendAuthorizedAsync(
            secondClient,
            HttpMethod.Post,
            "/enrollments",
            secondToken,
            new { classId, seats = 1 },
            key);
        Assert.AreEqual(HttpStatusCode.Created, retryResponse.StatusCode);
        using var retryBody = await JsonDocument.ParseAsync(await retryResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual(enrollmentId, retryBody.RootElement.GetProperty("id").GetInt64());
        Assert.AreEqual(initialBody.RootElement.GetRawText(), retryBody.RootElement.GetRawText());

        using var changedBodyResponse = await SendAuthorizedAsync(
            secondClient,
            HttpMethod.Post,
            "/enrollments",
            secondToken,
            new { classId, seats = 2 },
            key);
        Assert.AreEqual(HttpStatusCode.Conflict, changedBodyResponse.StatusCode);
        using var changedBody = await JsonDocument.ParseAsync(await changedBodyResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual("idempotency-key-reused", changedBody.RootElement.GetProperty("reason").GetString());
        var state = await ReadEnrollmentStateAsync(email, classId);
        Assert.AreEqual(0, state.RemainingVisits);
        Assert.AreEqual(1, state.UserActiveEnrollmentCount);
    }

    [TestMethod]
    public async Task RejectedCreateDoesNotChangeActiveSeatsOrRemainingVisits()
    {
        const string ownerEmail = "user015@club.test";
        const string rejectedEmail = "user016@club.test";
        const int initialVisits = 5;
        var classId = await CreateClassAsync("Rejected create no side effects", capacity: 1, DateTime.UtcNow.AddDays(2));
        await AddActiveEnrollmentAsync(ownerEmail, classId, seats: 1, remainingVisits: 4);
        await SetPassAsync(rejectedEmail, remainingVisits: initialVisits);
        var before = await ReadEnrollmentStateAsync(rejectedEmail, classId);
        var token = await LoginAsync(rejectedEmail);

        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            token,
            new { classId, seats = 1 },
            "rejected-create-no-side-effects");

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.AreEqual("capacity-exceeded", body.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual(before, await ReadEnrollmentStateAsync(rejectedEmail, classId));
        var exhaustedClassId = await CreateClassAsync("Exhausted pass no side effects", capacity: 4, DateTime.UtcNow.AddDays(2));
        await SetPassAsync(rejectedEmail, remainingVisits: 0);
        var exhaustedBefore = await ReadEnrollmentStateAsync(rejectedEmail, exhaustedClassId);
        using var exhaustedResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            token,
            new { classId = exhaustedClassId, seats = 1 },
            "exhausted-pass-no-side-effects");
        Assert.AreEqual(HttpStatusCode.Conflict, exhaustedResponse.StatusCode);
        using var exhaustedBody = await JsonDocument.ParseAsync(await exhaustedResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual("pass-exhausted", exhaustedBody.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual(exhaustedBefore, await ReadEnrollmentStateAsync(rejectedEmail, exhaustedClassId));
    }
    [TestMethod]
    public async Task RejectedResizeDoesNotChangeBookingSeatsOrRemainingVisits()
    {
        const string ownerEmail = "user017@club.test";
        const string otherEmail = "user018@club.test";
        var classId = await CreateClassAsync("Rejected resize no side effects", capacity: 2, DateTime.UtcNow.AddDays(2));
        var enrollmentId = await AddActiveEnrollmentAsync(ownerEmail, classId, seats: 1, remainingVisits: 4);
        await AddActiveEnrollmentAsync(otherEmail, classId, seats: 1, remainingVisits: 4);
        var before = await ReadEnrollmentStateAsync(ownerEmail, classId);
        var token = await LoginAsync(ownerEmail);

        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/{enrollmentId}/seats",
            token,
            new { seats = 2 });

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.AreEqual("capacity-exceeded", body.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual(before, await ReadEnrollmentStateAsync(ownerEmail, classId));
    }

    [TestMethod]
    public async Task ResizeConflictReasonsLeaveEnrollmentAndPassStateUnchanged()
    {
        var exhaustedClassId = await CreateClassAsync("Resize pass exhausted", 4, DateTime.UtcNow.AddDays(2));
        var exhaustedEnrollmentId = await AddActiveEnrollmentAsync("user026@club.test", exhaustedClassId, 1, 0);
        var expiredClassId = await CreateClassAsync("Resize pass expired", 4, DateTime.UtcNow.AddDays(2));
        var expiredEnrollmentId = await AddActiveEnrollmentAsync("user027@club.test", expiredClassId, 1, 4);
        await SetPassAsync("user027@club.test", 4, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));
        var cancelledEnrollmentClassId = await CreateClassAsync("Resize cancelled enrollment", 4, DateTime.UtcNow.AddDays(2));
        var cancelledEnrollmentId = await AddActiveEnrollmentAsync("user028@club.test", cancelledEnrollmentClassId, 1, 4);
        var clubCancelledClassId = await CreateClassAsync("Resize club cancelled", 4, DateTime.UtcNow.AddDays(2));
        var clubCancelledEnrollmentId = await AddActiveEnrollmentAsync("user029@club.test", clubCancelledClassId, 1, 4);
        var startedClassId = await CreateClassAsync("Resize class started", 4, DateTime.UtcNow.AddSeconds(-1));
        var startedEnrollmentId = await AddActiveEnrollmentAsync("user030@club.test", startedClassId, 1, 4);

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var clubCancelledClass = await dbContext.Classes.IgnoreQueryFilters().SingleAsync(x => x.Id == clubCancelledClassId);
            clubCancelledClass.IsCancelled = true;
            var cancelledEnrollment = await dbContext.Enrollments.SingleAsync(x => x.Id == cancelledEnrollmentId);
            cancelledEnrollment.Status = "cancelled";
            await dbContext.SaveChangesAsync();
        }

        var cases = new[]
        {
            ("user026@club.test", exhaustedClassId, exhaustedEnrollmentId, "pass-exhausted"),
            ("user027@club.test", expiredClassId, expiredEnrollmentId, "pass-expired"),
            ("user028@club.test", cancelledEnrollmentClassId, cancelledEnrollmentId, "enrollment-cancelled"),
            ("user029@club.test", clubCancelledClassId, clubCancelledEnrollmentId, "class-cancelled"),
            ("user030@club.test", startedClassId, startedEnrollmentId, "class-started")
        };

        foreach (var (email, classId, enrollmentId, expectedReason) in cases)
        {
            var before = await ReadEnrollmentStateAsync(email, classId);
            var token = await LoginAsync(email);
            using var response = await SendAuthorizedAsync(
                HttpMethod.Post,
                $"/enrollments/{enrollmentId}/seats",
                token,
                new { seats = 2 });

            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, $"Unexpected status for {expectedReason}.");
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            Assert.AreEqual(expectedReason, body.RootElement.GetProperty("reason").GetString());
            Assert.AreEqual(before, await ReadEnrollmentStateAsync(email, classId), $"State changed for {expectedReason}.");
        }
    }

    [TestMethod]
    public async Task StartedClassRejectionDoesNotChangeEnrollmentOrPassState()
    {
        const string email = "user019@club.test";
        const int initialVisits = 6;
        var classId = await CreateClassAsync("Already started class", capacity: 5, DateTime.UtcNow.AddSeconds(-1));
        await SetPassAsync(email, remainingVisits: initialVisits);
        var before = await ReadEnrollmentStateAsync(email, classId);
        var token = await LoginAsync(email);

        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            token,
            new { classId, seats = 2 },
            "started-class-no-side-effects");

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.AreEqual("class-started", body.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual(before, await ReadEnrollmentStateAsync(email, classId));
    }

    [TestMethod]
    public async Task ExpiredPassRejectionDoesNotChangeActiveSeatsOrRemainingVisits()
    {
        const string email = "user024@club.test";
        const int initialVisits = 4;
        var classId = await CreateClassAsync("Expired pass no side effects", capacity: 4, DateTime.UtcNow.AddDays(2));
        await SetPassAsync(email, initialVisits, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));
        var before = await ReadEnrollmentStateAsync(email, classId);
        var token = await LoginAsync(email);

        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            token,
            new { classId, seats = 1 },
            "expired-pass-no-side-effects");

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.AreEqual("pass-expired", body.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual(before, await ReadEnrollmentStateAsync(email, classId));
    }

    [TestMethod]
    public async Task CancellationAfterClassStartDoesNotChangeBookingOrPass()
    {
        const string email = "user025@club.test";
        var classId = await CreateClassAsync("Started cancellation no side effects", capacity: 4, DateTime.UtcNow.AddSeconds(-1));
        var enrollmentId = await AddActiveEnrollmentAsync(email, classId, seats: 2, remainingVisits: 4);
        var before = await ReadEnrollmentStateAsync(email, classId);
        var token = await LoginAsync(email);

        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"/enrollments/{enrollmentId}/cancel", token);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.AreEqual("class-started", body.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual(before, await ReadEnrollmentStateAsync(email, classId));
    }

    [TestMethod]
    public async Task PassValidThroughTodayAllowsBookingForClassNextMonth()
    {
        const string email = "user020@club.test";
        const int initialVisits = 2;
        var classId = await CreateClassAsync("Future class with valid pass", capacity: 5, DateTime.UtcNow.AddDays(35));
        await SetPassAsync(email, initialVisits, DateOnly.FromDateTime(DateTime.UtcNow));
        var token = await LoginAsync(email);

        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            token,
            new { classId, seats = 1 },
            "valid-through-today-key");

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual(initialVisits - 1, (await ReadEnrollmentStateAsync(email, classId)).RemainingVisits);
    }

    [TestMethod]
    public async Task IdempotencyKeyRejectsEmptyWhitespaceAndAcceptsLongValues()
    {
        const string email = "user021@club.test";
        var token = await LoginAsync(email);
        var classId = await CreateClassAsync("Long idempotency key", capacity: 4, DateTime.UtcNow.AddDays(2));

        foreach (var invalidKey in new[] { string.Empty, "   " })
        {
            using var invalidResponse = await SendAuthorizedAsync(
                HttpMethod.Post,
                "/enrollments",
                token,
                new { classId, seats = 1 },
                invalidKey);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        }

        var longKey = new string('k', 8192);
        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            token,
            new { classId, seats = 1 },
            longKey);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
    }

    [TestMethod]
    public async Task InvalidAndUnknownEnrollmentIdentifiersUseSpecifiedStatuses()
    {
        var token = await LoginAsync("user022@club.test");
        using var nonnumericPath = await SendAuthorizedAsync(HttpMethod.Get, "/enrollments/not-a-number", token);
        Assert.AreEqual(HttpStatusCode.BadRequest, nonnumericPath.StatusCode);

        foreach (var invalidId in new long[] { 0, -1 })
        {
            using var invalidGetId = await SendAuthorizedAsync(HttpMethod.Get, $"/enrollments/{invalidId}", token);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidGetId.StatusCode);

            using var invalidSeatId = await SendAuthorizedAsync(
                HttpMethod.Post,
                $"/enrollments/{invalidId}/seats",
                token,
                new { seats = 1 });
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidSeatId.StatusCode);

            using var invalidCancelId = await SendAuthorizedAsync(HttpMethod.Post, $"/enrollments/{invalidId}/cancel", token);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidCancelId.StatusCode);

            using var invalidWaitlistId = await SendAuthorizedAsync(
                HttpMethod.Post,
                $"/enrollments/waitlist/{invalidId}/cancel",
                token);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidWaitlistId.StatusCode);

            using var invalidClassBodyId = await SendAuthorizedAsync(
                HttpMethod.Post,
                "/enrollments",
                token,
                new { classId = invalidId, seats = 1 },
                $"invalid-class-id-{invalidId}");
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidClassBodyId.StatusCode);

            using var invalidWaitlistBodyId = await SendAuthorizedAsync(
                HttpMethod.Post,
                "/enrollments/waitlist",
                token,
                new { classId = invalidId, seats = 1 },
                $"invalid-waitlist-class-id-{invalidId}");
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidWaitlistBodyId.StatusCode);
        }

        using var nonnumericSeatPath = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/not-a-number/seats",
            token,
            new { seats = 1 });
        Assert.AreEqual(HttpStatusCode.BadRequest, nonnumericSeatPath.StatusCode);

        using var nonnumericCancelPath = await SendAuthorizedAsync(HttpMethod.Post, "/enrollments/not-a-number/cancel", token);
        Assert.AreEqual(HttpStatusCode.BadRequest, nonnumericCancelPath.StatusCode);

        using var nonnumericWaitlistPath = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist/not-a-number/cancel",
            token);
        Assert.AreEqual(HttpStatusCode.BadRequest, nonnumericWaitlistPath.StatusCode);

        using var nonnumericBody = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            token,
            new { classId = "not-a-number", seats = 1 },
            "nonnumeric-class-id");
        Assert.AreEqual(HttpStatusCode.BadRequest, nonnumericBody.StatusCode);

        using var unknownBodyId = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            token,
            new { classId = long.MaxValue, seats = 1 },
            "unknown-class-id");
        Assert.AreEqual(HttpStatusCode.NotFound, unknownBodyId.StatusCode);

        using var unknownPathId = await SendAuthorizedAsync(HttpMethod.Get, $"/enrollments/{long.MaxValue}", token);
        Assert.AreEqual(HttpStatusCode.NotFound, unknownPathId.StatusCode);

        var from = Uri.EscapeDataString(DateTime.UtcNow.ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("O"));
        foreach (var invalidRoomId in new[] { 0, -1 })
        {
            using var invalidScheduleRoom = await SendAuthorizedAsync(
                HttpMethod.Get,
                $"/schedule?from={from}&to={to}&roomId={invalidRoomId}",
                token);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidScheduleRoom.StatusCode);
        }

        var adminToken = await LoginAsync("admin003@club.test");
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        foreach (var invalidRoomId in new[] { 0, -1 })
        {
            using var invalidAnalyticsRoom = await SendAuthorizedAsync(
                HttpMethod.Get,
                $"/analytics/load?from={today}&to={today}&roomId={invalidRoomId}",
                adminToken);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidAnalyticsRoom.StatusCode);
        }
    }

    [TestMethod]
    // Сценарий: свободных мест хватает; постановка в очередь отклоняется без изменения баланса и записей.
    public async Task WaitlistRefusalWhenSeatsAreAvailableLeavesStateUnchanged()
    {
        const string email = "user023@club.test";
        const int initialVisits = 5;
        var classId = await CreateClassAsync("Available seats waitlist refusal", capacity: 4, DateTime.UtcNow.AddDays(2));
        await SetPassAsync(email, initialVisits);
        var before = await ReadEnrollmentStateAsync(email, classId);
        var token = await LoginAsync(email);

        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            token,
            new { classId, seats = 1 },
            "available-seats-waitlist-refusal");

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.AreEqual("seats-available", body.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual(before, await ReadEnrollmentStateAsync(email, classId));
    }

    [TestMethod]
    public async Task WaitlistIdempotencyReplaysSameEntryAndRejectsChangedRequest()
    {
        const string ownerEmail = "user049@club.test";
        const string waiterEmail = "user050@club.test";
        const string key = "waitlist-cross-host-key";
        var classId = await CreateClassAsync("Waitlist idempotency scenario", 1, DateTime.UtcNow.AddDays(2));
        await AddActiveEnrollmentAsync(ownerEmail, classId, seats: 1, remainingVisits: 4);
        await SetPassAsync(waiterEmail, remainingVisits: 5);
        var waiterToken = await LoginAsync(waiterEmail);

        using var firstResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            waiterToken,
            new { classId, seats = 1 },
            key);
        Assert.AreEqual(HttpStatusCode.Created, firstResponse.StatusCode);
        using var firstBody = await JsonDocument.ParseAsync(await firstResponse.Content.ReadAsStreamAsync());
        var waitlistId = firstBody.RootElement.GetProperty("id").GetInt64();
        var initialState = await ReadEnrollmentStateAsync(waiterEmail, classId);

        using var secondFactory = new ApiApplicationFactory();
        using var secondClient = secondFactory.CreateClient();
        var secondToken = await LoginAsync(secondClient, waiterEmail);
        using var replayResponse = await SendAuthorizedAsync(
            secondClient,
            HttpMethod.Post,
            "/enrollments/waitlist",
            secondToken,
            new { classId, seats = 1 },
            key);
        Assert.AreEqual(HttpStatusCode.Created, replayResponse.StatusCode);
        using var replayBody = await JsonDocument.ParseAsync(await replayResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual(waitlistId, replayBody.RootElement.GetProperty("id").GetInt64());
        Assert.AreEqual(firstBody.RootElement.GetRawText(), replayBody.RootElement.GetRawText());

        using var changedRequestResponse = await SendAuthorizedAsync(
            secondClient,
            HttpMethod.Post,
            "/enrollments/waitlist",
            secondToken,
            new { classId, seats = 2 },
            key);
        Assert.AreEqual(HttpStatusCode.Conflict, changedRequestResponse.StatusCode);
        using var changedRequestBody = await JsonDocument.ParseAsync(await changedRequestResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual("idempotency-key-reused", changedRequestBody.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual(initialState, await ReadEnrollmentStateAsync(waiterEmail, classId));
    }

    [TestMethod]
    // Сценарий: дубликат ожидания, начавшееся или отменённое занятие; ожидаем соответствующий 409 без изменений состояния.
    public async Task WaitlistConflictReasonsLeavePassAndQueueStateUnchanged()
    {
        const string ownerEmail = "user031@club.test";
        const string alreadyWaitingEmail = "user032@club.test";
        const string startedClassEmail = "user033@club.test";
        const string cancelledClassEmail = "user034@club.test";
        var fullClassId = await CreateClassAsync("Already waiting conflict", 1, DateTime.UtcNow.AddDays(2));
        await AddActiveEnrollmentAsync(ownerEmail, fullClassId, 1, 4);
        await SetPassAsync(alreadyWaitingEmail, 5);
        var alreadyWaitingToken = await LoginAsync(alreadyWaitingEmail);

        using var firstJoin = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            alreadyWaitingToken,
            new { classId = fullClassId, seats = 1 },
            "already-waiting-first-key");
        Assert.AreEqual(HttpStatusCode.Created, firstJoin.StatusCode);
        var beforeAlreadyWaiting = await ReadEnrollmentStateAsync(alreadyWaitingEmail, fullClassId);

        using var duplicateJoin = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            alreadyWaitingToken,
            new { classId = fullClassId, seats = 1 },
            "already-waiting-second-key");
        Assert.AreEqual(HttpStatusCode.Conflict, duplicateJoin.StatusCode);
        using var duplicateBody = await JsonDocument.ParseAsync(await duplicateJoin.Content.ReadAsStreamAsync());
        Assert.AreEqual("already-waiting", duplicateBody.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual(beforeAlreadyWaiting, await ReadEnrollmentStateAsync(alreadyWaitingEmail, fullClassId));

        var startedClassId = await CreateClassAsync("Waitlist started conflict", 1, DateTime.UtcNow.AddSeconds(-1));
        await SetPassAsync(startedClassEmail, 5);
        var beforeStarted = await ReadEnrollmentStateAsync(startedClassEmail, startedClassId);
        var startedToken = await LoginAsync(startedClassEmail);
        using var startedJoin = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            startedToken,
            new { classId = startedClassId, seats = 1 },
            "waitlist-started-class-key");
        Assert.AreEqual(HttpStatusCode.Conflict, startedJoin.StatusCode);
        using var startedBody = await JsonDocument.ParseAsync(await startedJoin.Content.ReadAsStreamAsync());
        Assert.AreEqual("class-started", startedBody.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual(beforeStarted, await ReadEnrollmentStateAsync(startedClassEmail, startedClassId));

        var cancelledClassId = await CreateClassAsync("Waitlist cancelled conflict", 1, DateTime.UtcNow.AddDays(2));
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var scheduledClass = await dbContext.Classes.IgnoreQueryFilters().SingleAsync(x => x.Id == cancelledClassId);
            scheduledClass.IsCancelled = true;
            await dbContext.SaveChangesAsync();
        }

        await SetPassAsync(cancelledClassEmail, 5);
        var beforeCancelled = await ReadEnrollmentStateAsync(cancelledClassEmail, cancelledClassId);
        var cancelledToken = await LoginAsync(cancelledClassEmail);
        using var cancelledJoin = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            cancelledToken,
            new { classId = cancelledClassId, seats = 1 },
            "waitlist-cancelled-class-key");
        Assert.AreEqual(HttpStatusCode.Conflict, cancelledJoin.StatusCode);
        using var cancelledBody = await JsonDocument.ParseAsync(await cancelledJoin.Content.ReadAsStreamAsync());
        Assert.AreEqual("class-cancelled", cancelledBody.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual(beforeCancelled, await ReadEnrollmentStateAsync(cancelledClassEmail, cancelledClassId));
    }

    [TestMethod]
    // Сценарий: ожидающие с исчерпанным/истёкшим абонементом остаются в очереди; следующий платёжеспособный получает место.
    public async Task CancellationSkipsIneligibleWaiterAndPromotesNextEligibleEntry()
    {
        const string ownerEmail = "user035@club.test";
        const string ineligibleEmail = "user036@club.test";
        const string expiredEmail = "user044@club.test";
        const string eligibleEmail = "user037@club.test";
        var classId = await CreateClassAsync("Skip ineligible waitlist entry", 2, DateTime.UtcNow.AddDays(2));
        await SetPassAsync(ownerEmail, 5);
        await AddActiveEnrollmentAsync(ineligibleEmail, classId, 1, 0);
        await SetPassAsync(expiredEmail, 4, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));
        await SetPassAsync(eligibleEmail, 5);

        var ownerToken = await LoginAsync(ownerEmail);
        using var ownerBooking = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            ownerToken,
            new { classId, seats = 1 },
            "ineligible-promotion-owner-key");
        Assert.AreEqual(HttpStatusCode.Created, ownerBooking.StatusCode);
        using var ownerBody = await JsonDocument.ParseAsync(await ownerBooking.Content.ReadAsStreamAsync());
        var ownerEnrollmentId = ownerBody.RootElement.GetProperty("id").GetInt64();

        var ineligibleToken = await LoginAsync(ineligibleEmail);
        using var ineligibleJoin = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            ineligibleToken,
            new { classId, seats = 1 },
            "ineligible-promotion-first-key");
        Assert.AreEqual(HttpStatusCode.Created, ineligibleJoin.StatusCode);
        using var ineligibleBody = await JsonDocument.ParseAsync(await ineligibleJoin.Content.ReadAsStreamAsync());
        var ineligibleEnrollmentId = ineligibleBody.RootElement.GetProperty("id").GetInt64();
        var ineligibleStateBeforePromotion = await ReadEnrollmentStateAsync(ineligibleEmail, classId);
        Assert.AreEqual(1, ineligibleStateBeforePromotion.UserActiveEnrollmentCount);
        Assert.AreEqual(1, ineligibleStateBeforePromotion.UserWaitingEnrollmentCount);

        var expiredToken = await LoginAsync(expiredEmail);
        using var expiredJoin = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            expiredToken,
            new { classId, seats = 1 },
            "expired-promotion-second-key");
        Assert.AreEqual(HttpStatusCode.Created, expiredJoin.StatusCode);
        using var expiredBody = await JsonDocument.ParseAsync(await expiredJoin.Content.ReadAsStreamAsync());
        var expiredEnrollmentId = expiredBody.RootElement.GetProperty("id").GetInt64();
        Assert.AreEqual(2, expiredBody.RootElement.GetProperty("position").GetInt32());

        var eligibleToken = await LoginAsync(eligibleEmail);
        using var eligibleJoin = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            eligibleToken,
            new { classId, seats = 1 },
            "ineligible-promotion-next-key");
        Assert.AreEqual(HttpStatusCode.Created, eligibleJoin.StatusCode);
        using var eligibleBody = await JsonDocument.ParseAsync(await eligibleJoin.Content.ReadAsStreamAsync());
        var eligibleEnrollmentId = eligibleBody.RootElement.GetProperty("id").GetInt64();

        using var cancelOwner = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/{ownerEnrollmentId}/cancel",
            ownerToken);
        Assert.AreEqual(HttpStatusCode.OK, cancelOwner.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var ineligibleEntry = await dbContext.Enrollments.SingleAsync(x => x.Id == ineligibleEnrollmentId);
        var expiredEntry = await dbContext.Enrollments.SingleAsync(x => x.Id == expiredEnrollmentId);
        var eligibleEntry = await dbContext.Enrollments.SingleAsync(x => x.Id == eligibleEnrollmentId);
        Assert.AreEqual("waiting", ineligibleEntry.Status);
        Assert.AreEqual("waiting", expiredEntry.Status);
        Assert.AreEqual("active", eligibleEntry.Status);
        Assert.AreEqual(0, (await ReadEnrollmentStateAsync(ineligibleEmail, classId)).RemainingVisits);
        Assert.AreEqual(1, (await ReadEnrollmentStateAsync(ineligibleEmail, classId)).UserActiveEnrollmentCount);
        Assert.AreEqual(1, (await ReadEnrollmentStateAsync(ineligibleEmail, classId)).UserWaitingEnrollmentCount);
        Assert.AreEqual(4, (await ReadEnrollmentStateAsync(expiredEmail, classId)).RemainingVisits);
        Assert.AreEqual(1, (await ReadEnrollmentStateAsync(expiredEmail, classId)).UserWaitingEnrollmentCount);
        Assert.AreEqual(4, (await ReadEnrollmentStateAsync(eligibleEmail, classId)).RemainingVisits);
        Assert.AreEqual(1, (await ReadEnrollmentStateAsync(eligibleEmail, classId)).UserSeats);
    }

    [TestMethod]
    // Сценарий: занятие клуба отменено, хотя в очереди есть клиент; отмена брони возвращает посещения, но очередь не активируется.
    public async Task CancellationOfClubCancelledClassDoesNotPromoteWaiters()
    {
        const string ownerEmail = "user045@club.test";
        const string waitingEmail = "user046@club.test";
        var classId = await CreateClassAsync("Cancelled class with waiter", 2, DateTime.UtcNow.AddHours(-1));
        var ownerEnrollmentId = await AddActiveEnrollmentAsync(ownerEmail, classId, 1, 4);
        await SetPassAsync(waitingEmail, 5);
        long waitlistId;

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var scheduledClass = await dbContext.Classes.IgnoreQueryFilters().SingleAsync(x => x.Id == classId);
            scheduledClass.IsCancelled = true;
            var waiter = await dbContext.Users.SingleAsync(x => x.Email == waitingEmail);
            var waitlistEntry = new Enrollment
            {
                UserId = waiter.Id,
                ClassId = classId,
                Seats = 1,
                Status = "waiting",
                IsWaitlist = true,
                CreatedAt = DateTime.UtcNow
            };
            dbContext.Enrollments.Add(waitlistEntry);
            await dbContext.SaveChangesAsync();
            waitlistId = waitlistEntry.Id;
        }

        var ownerToken = await LoginAsync(ownerEmail);
        using var cancelResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/{ownerEnrollmentId}/cancel",
            ownerToken);
        Assert.AreEqual(HttpStatusCode.OK, cancelResponse.StatusCode);
        using var cancelBody = await JsonDocument.ParseAsync(await cancelResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual(1, cancelBody.RootElement.GetProperty("refundedVisits").GetInt32());

        using var scopeAfter = _factory.Services.CreateScope();
        var resultContext = scopeAfter.ServiceProvider.GetRequiredService<ClubDbContext>();
        var waitingEntry = await resultContext.Enrollments.SingleAsync(x => x.Id == waitlistId);
        Assert.AreEqual("waiting", waitingEntry.Status);
        Assert.AreEqual(5, (await ReadEnrollmentStateAsync(waitingEmail, classId)).RemainingVisits);
        Assert.AreEqual(0, (await ReadEnrollmentStateAsync(waitingEmail, classId)).UserActiveEnrollmentCount);
    }

    [TestMethod]
    // Сценарий: клиент уходит из очереди до освобождения места; последующая отмена не должна зачислить его.
    public async Task WaiterWhoLeavesBeforeCancellationIsNotPromotedLater()
    {
        const string ownerEmail = "user047@club.test";
        const string waitingEmail = "user048@club.test";
        var classId = await CreateClassAsync("Waiter leaves before seat release", 1, DateTime.UtcNow.AddDays(2));
        var ownerEnrollmentId = await AddActiveEnrollmentAsync(ownerEmail, classId, 1, 4);
        await SetPassAsync(waitingEmail, 5);

        var waitingToken = await LoginAsync(waitingEmail);
        using var joinResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            waitingToken,
            new { classId, seats = 1 },
            "leave-before-release-key");
        Assert.AreEqual(HttpStatusCode.Created, joinResponse.StatusCode);
        using var joinBody = await JsonDocument.ParseAsync(await joinResponse.Content.ReadAsStreamAsync());
        var waitlistId = joinBody.RootElement.GetProperty("id").GetInt64();

        using var leaveResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/waitlist/{waitlistId}/cancel",
            waitingToken);
        Assert.AreEqual(HttpStatusCode.OK, leaveResponse.StatusCode);

        var ownerToken = await LoginAsync(ownerEmail);
        using var cancelOwner = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/{ownerEnrollmentId}/cancel",
            ownerToken);
        Assert.AreEqual(HttpStatusCode.OK, cancelOwner.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var entry = await dbContext.Enrollments.SingleAsync(x => x.Id == waitlistId);
        Assert.AreEqual("cancelled", entry.Status);
        var finalState = await ReadEnrollmentStateAsync(waitingEmail, classId);
        Assert.AreEqual(5, finalState.RemainingVisits);
        Assert.AreEqual(0, finalState.UserActiveEnrollmentCount);
        Assert.AreEqual(0, finalState.UserWaitingEnrollmentCount);
    }

    [TestMethod]
    // Сценарий: после отмены освобождается одно место, а первый ожидающий просит два; строгий FIFO удерживает следующего.
    public async Task WaitlistKeepsStrictFifoWhenHeadNeedsMoreThanFreedCapacity()
    {
        const string ownerEmail = "user038@club.test";
        const string blockerEmail = "user039@club.test";
        const string firstWaitingEmail = "user040@club.test";
        const string secondWaitingEmail = "user041@club.test";
        var classId = await CreateClassAsync("Strict FIFO waitlist", 2, DateTime.UtcNow.AddDays(2));
        var ownerEnrollmentId = await AddActiveEnrollmentAsync(ownerEmail, classId, 1, 4);
        await AddActiveEnrollmentAsync(blockerEmail, classId, 1, 4);
        await SetPassAsync(firstWaitingEmail, 5);
        await SetPassAsync(secondWaitingEmail, 5);

        var firstToken = await LoginAsync(firstWaitingEmail);
        using var firstJoin = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            firstToken,
            new { classId, seats = 2 },
            "strict-fifo-head-key");
        Assert.AreEqual(HttpStatusCode.Created, firstJoin.StatusCode);
        using var firstBody = await JsonDocument.ParseAsync(await firstJoin.Content.ReadAsStreamAsync());
        var firstId = firstBody.RootElement.GetProperty("id").GetInt64();
        Assert.AreEqual(1, firstBody.RootElement.GetProperty("position").GetInt32());

        var secondToken = await LoginAsync(secondWaitingEmail);
        using var secondJoin = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            secondToken,
            new { classId, seats = 1 },
            "strict-fifo-next-key");
        Assert.AreEqual(HttpStatusCode.Created, secondJoin.StatusCode);
        using var secondBody = await JsonDocument.ParseAsync(await secondJoin.Content.ReadAsStreamAsync());
        var secondId = secondBody.RootElement.GetProperty("id").GetInt64();
        Assert.AreEqual(2, secondBody.RootElement.GetProperty("position").GetInt32());

        var ownerToken = await LoginAsync(ownerEmail);
        using var cancelOwner = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/{ownerEnrollmentId}/cancel",
            ownerToken);
        Assert.AreEqual(HttpStatusCode.OK, cancelOwner.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var firstEntry = await dbContext.Enrollments.SingleAsync(x => x.Id == firstId);
        var secondEntry = await dbContext.Enrollments.SingleAsync(x => x.Id == secondId);
        Assert.AreEqual("waiting", firstEntry.Status);
        Assert.AreEqual("waiting", secondEntry.Status);
        Assert.AreEqual(1, (await ReadEnrollmentStateAsync(blockerEmail, classId)).ActiveSeats);
        Assert.AreEqual(5, (await ReadEnrollmentStateAsync(firstWaitingEmail, classId)).RemainingVisits);
        Assert.AreEqual(5, (await ReadEnrollmentStateAsync(secondWaitingEmail, classId)).RemainingVisits);
    }

    [TestMethod]
    // Сценарий: отмена освобождает два места для трёх заявок; первые две активируются в FIFO-порядке, третья остаётся ждать.
    public async Task CancellationPromotesFifoEntriesOnlyUntilFreedCapacityIsConsumed()
    {
        const string ownerEmail = "user049@club.test";
        const string blockerEmail = "user053@club.test";
        const string firstEmail = "user050@club.test";
        const string secondEmail = "user051@club.test";
        const string thirdEmail = "user052@club.test";
        var classId = await CreateClassAsync("Multiple waitlist promotions", 3, DateTime.UtcNow.AddDays(2));
        await SetPassAsync(ownerEmail, 5);
        await AddActiveEnrollmentAsync(blockerEmail, classId, 1, 4);
        await SetPassAsync(firstEmail, 5);
        await SetPassAsync(secondEmail, 5);
        await SetPassAsync(thirdEmail, 5);

        var ownerToken = await LoginAsync(ownerEmail);
        using var ownerBooking = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            ownerToken,
            new { classId, seats = 2 },
            "multiple-promotion-owner-key");
        Assert.AreEqual(HttpStatusCode.Created, ownerBooking.StatusCode);
        using var ownerBody = await JsonDocument.ParseAsync(await ownerBooking.Content.ReadAsStreamAsync());
        var ownerEnrollmentId = ownerBody.RootElement.GetProperty("id").GetInt64();

        var firstToken = await LoginAsync(firstEmail);
        using var firstJoin = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            firstToken,
            new { classId, seats = 1 },
            "multiple-promotion-first-key");
        Assert.AreEqual(HttpStatusCode.Created, firstJoin.StatusCode);
        using var firstBody = await JsonDocument.ParseAsync(await firstJoin.Content.ReadAsStreamAsync());
        var firstId = firstBody.RootElement.GetProperty("id").GetInt64();
        Assert.AreEqual(1, firstBody.RootElement.GetProperty("position").GetInt32());

        var secondToken = await LoginAsync(secondEmail);
        using var secondJoin = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            secondToken,
            new { classId, seats = 1 },
            "multiple-promotion-second-key");
        Assert.AreEqual(HttpStatusCode.Created, secondJoin.StatusCode);
        using var secondBody = await JsonDocument.ParseAsync(await secondJoin.Content.ReadAsStreamAsync());
        var secondId = secondBody.RootElement.GetProperty("id").GetInt64();
        Assert.AreEqual(2, secondBody.RootElement.GetProperty("position").GetInt32());

        var thirdToken = await LoginAsync(thirdEmail);
        using var thirdJoin = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            thirdToken,
            new { classId, seats = 1 },
            "multiple-promotion-third-key");
        Assert.AreEqual(HttpStatusCode.Created, thirdJoin.StatusCode);
        using var thirdBody = await JsonDocument.ParseAsync(await thirdJoin.Content.ReadAsStreamAsync());
        var thirdId = thirdBody.RootElement.GetProperty("id").GetInt64();
        Assert.AreEqual(3, thirdBody.RootElement.GetProperty("position").GetInt32());

        using var cancelOwner = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/{ownerEnrollmentId}/cancel",
            ownerToken);
        Assert.AreEqual(HttpStatusCode.OK, cancelOwner.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual("active", (await dbContext.Enrollments.SingleAsync(x => x.Id == firstId)).Status);
        Assert.AreEqual("active", (await dbContext.Enrollments.SingleAsync(x => x.Id == secondId)).Status);
        Assert.AreEqual("waiting", (await dbContext.Enrollments.SingleAsync(x => x.Id == thirdId)).Status);
        Assert.AreEqual(3, (await ReadEnrollmentStateAsync(firstEmail, classId)).ActiveSeats);
        Assert.AreEqual(4, (await ReadEnrollmentStateAsync(firstEmail, classId)).RemainingVisits);
        Assert.AreEqual(4, (await ReadEnrollmentStateAsync(secondEmail, classId)).RemainingVisits);
        Assert.AreEqual(5, (await ReadEnrollmentStateAsync(thirdEmail, classId)).RemainingVisits);
        Assert.AreEqual(1, (await ReadEnrollmentStateAsync(thirdEmail, classId)).UserWaitingEnrollmentCount);
    }

    [TestMethod]
    public async Task ConcurrentBookingsCannotExceedClassCapacity()
    {
        const string firstEmail = "user005@club.test";
        const string secondEmail = "user006@club.test";
        long classId;

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var emails = new[] { firstEmail, secondEmail };
            var users = await dbContext.Users.Where(x => emails.Contains(x.Email)).ToListAsync();
            var userIds = users.Select(x => x.Id).ToArray();
            var passes = await dbContext.Passes.Where(x => userIds.Contains(x.UserId)).ToListAsync();
            foreach (var pass in passes)
            {
                pass.RemainingVisits = 5;
                pass.ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
            }

            var room = await dbContext.Rooms.OrderBy(x => x.Id).FirstAsync();
            var scheduledClass = new Class
            {
                RoomId = room.Id,
                Title = "Concurrent capacity scenario",
                StartsAt = DateTime.UtcNow.AddDays(3),
                DurationMinutes = 60,
                Capacity = 1,
                IsCancelled = false
            };
            dbContext.Classes.Add(scheduledClass);
            await dbContext.SaveChangesAsync();
            classId = scheduledClass.Id;
        }

        var firstToken = await LoginAsync(firstEmail);
        var secondToken = await LoginAsync(secondEmail);
        var firstTask = SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            firstToken,
            new { classId, seats = 1 },
            "concurrent-booking-one");
        var secondTask = SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            secondToken,
            new { classId, seats = 1 },
            "concurrent-booking-two");
        var responses = await Task.WhenAll(firstTask, secondTask);

        Assert.AreEqual(1, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
        Assert.AreEqual(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        foreach (var response in responses)
        {
            response.Dispose();
        }

        var firstState = await ReadEnrollmentStateAsync(firstEmail, classId);
        var secondState = await ReadEnrollmentStateAsync(secondEmail, classId);
        Assert.AreEqual(1, firstState.ActiveSeats);
        Assert.AreEqual(1, firstState.UserActiveEnrollmentCount + secondState.UserActiveEnrollmentCount);
        Assert.AreEqual(1, firstState.UserSeats + secondState.UserSeats);
        Assert.AreEqual(5 - firstState.UserSeats, firstState.RemainingVisits);
        Assert.AreEqual(5 - secondState.UserSeats, secondState.RemainingVisits);
    }

    [TestMethod]
    // Сценарий: три ожидающих, первое место активируется; выход второго не меняет баланс, позиция следующего сдвигается.
    public async Task WaitlistPromotesFirstMemberAndAllowsRepeatedLeave()
    {
        const string ownerEmail = "user007@club.test";
        const string firstWaitingEmail = "user008@club.test";
        const string secondWaitingEmail = "user009@club.test";
        const string thirdWaitingEmail = "user042@club.test";
        long classId;

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var emails = new[] { ownerEmail, firstWaitingEmail, secondWaitingEmail, thirdWaitingEmail };
            var users = await dbContext.Users.Where(x => emails.Contains(x.Email)).ToListAsync();
            var userIds = users.Select(x => x.Id).ToArray();
            var passes = await dbContext.Passes.Where(x => userIds.Contains(x.UserId)).ToListAsync();
            foreach (var pass in passes)
            {
                pass.RemainingVisits = 5;
                pass.ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
            }

            var room = await dbContext.Rooms.OrderBy(x => x.Id).FirstAsync();
            var scheduledClass = new Class
            {
                RoomId = room.Id,
                Title = "Waitlist lifecycle scenario",
                StartsAt = DateTime.UtcNow.AddDays(3),
                DurationMinutes = 60,
                Capacity = 1,
                IsCancelled = false
            };
            dbContext.Classes.Add(scheduledClass);
            await dbContext.SaveChangesAsync();
            classId = scheduledClass.Id;
        }

        var ownerToken = await LoginAsync(ownerEmail);
        using var ownerBooking = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments",
            ownerToken,
            new { classId, seats = 1 },
            "waitlist-owner-booking");
        Assert.AreEqual(HttpStatusCode.Created, ownerBooking.StatusCode);
        using var ownerBody = await JsonDocument.ParseAsync(await ownerBooking.Content.ReadAsStreamAsync());
        var ownerEnrollmentId = ownerBody.RootElement.GetProperty("id").GetInt64();

        var firstWaitingToken = await LoginAsync(firstWaitingEmail);
        using var firstWaitResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            firstWaitingToken,
            new { classId, seats = 1 },
            "first-waitlist-key");
        Assert.AreEqual(HttpStatusCode.Created, firstWaitResponse.StatusCode);
        using var firstWaitBody = await JsonDocument.ParseAsync(await firstWaitResponse.Content.ReadAsStreamAsync());
        var firstWaitId = firstWaitBody.RootElement.GetProperty("id").GetInt64();
        Assert.AreEqual(1, firstWaitBody.RootElement.GetProperty("position").GetInt32());

        using var firstWaitRetry = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            firstWaitingToken,
            new { classId, seats = 1 },
            "first-waitlist-key");
        Assert.AreEqual(HttpStatusCode.Created, firstWaitRetry.StatusCode);
        using var firstRetryBody = await JsonDocument.ParseAsync(await firstWaitRetry.Content.ReadAsStreamAsync());
        Assert.AreEqual(firstWaitId, firstRetryBody.RootElement.GetProperty("id").GetInt64());
        Assert.AreEqual(firstWaitBody.RootElement.GetRawText(), firstRetryBody.RootElement.GetRawText());

        var stateBeforeChangedRetry = await ReadEnrollmentStateAsync(firstWaitingEmail, classId);
        using var changedWaitRetry = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            firstWaitingToken,
            new { classId, seats = 2 },
            "first-waitlist-key");
        Assert.AreEqual(HttpStatusCode.Conflict, changedWaitRetry.StatusCode);
        using var changedWaitBody = await JsonDocument.ParseAsync(await changedWaitRetry.Content.ReadAsStreamAsync());
        Assert.AreEqual("idempotency-key-reused", changedWaitBody.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual(stateBeforeChangedRetry, await ReadEnrollmentStateAsync(firstWaitingEmail, classId));

        var secondWaitingToken = await LoginAsync(secondWaitingEmail);
        using var secondWaitResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            secondWaitingToken,
            new { classId, seats = 1 },
            "second-waitlist-key");
        Assert.AreEqual(HttpStatusCode.Created, secondWaitResponse.StatusCode);
        using var secondWaitBody = await JsonDocument.ParseAsync(await secondWaitResponse.Content.ReadAsStreamAsync());
        var secondWaitId = secondWaitBody.RootElement.GetProperty("id").GetInt64();
        Assert.AreEqual(2, secondWaitBody.RootElement.GetProperty("position").GetInt32());

        var thirdWaitingToken = await LoginAsync(thirdWaitingEmail);
        using var thirdWaitResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            thirdWaitingToken,
            new { classId, seats = 1 },
            "third-waitlist-key");
        Assert.AreEqual(HttpStatusCode.Created, thirdWaitResponse.StatusCode);
        using var thirdWaitBody = await JsonDocument.ParseAsync(await thirdWaitResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual(3, thirdWaitBody.RootElement.GetProperty("position").GetInt32());

        using var cancelOwner = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/{ownerEnrollmentId}/cancel",
            ownerToken);
        Assert.AreEqual(HttpStatusCode.OK, cancelOwner.StatusCode);

        using var promotedDetail = await SendAuthorizedAsync(
            HttpMethod.Get,
            $"/enrollments/{firstWaitId}",
            firstWaitingToken);
        Assert.AreEqual(HttpStatusCode.OK, promotedDetail.StatusCode);
        using var promotedBody = await JsonDocument.ParseAsync(await promotedDetail.Content.ReadAsStreamAsync());
        Assert.AreEqual("active", promotedBody.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(4, promotedBody.RootElement.GetProperty("pass").GetProperty("remainingVisits").GetInt32());

        var secondStateBeforeLeave = await ReadEnrollmentStateAsync(secondWaitingEmail, classId);
        using var leaveResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/waitlist/{secondWaitId}/cancel",
            secondWaitingToken);
        Assert.AreEqual(HttpStatusCode.OK, leaveResponse.StatusCode);
        using var repeatLeaveResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/waitlist/{secondWaitId}/cancel",
            secondWaitingToken);
        Assert.AreEqual(HttpStatusCode.OK, repeatLeaveResponse.StatusCode);
        var secondStateAfterLeave = await ReadEnrollmentStateAsync(secondWaitingEmail, classId);
        Assert.AreEqual(secondStateBeforeLeave.ActiveSeats, secondStateAfterLeave.ActiveSeats);
        Assert.AreEqual(secondStateBeforeLeave.RemainingVisits, secondStateAfterLeave.RemainingVisits);
        Assert.AreEqual(0, secondStateAfterLeave.UserActiveEnrollmentCount);
        Assert.AreEqual(0, secondStateAfterLeave.UserWaitingEnrollmentCount);

        using var afterLeaveJoin = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            secondWaitingToken,
            new { classId, seats = 1 },
            "after-leave-position-key");
        Assert.AreEqual(HttpStatusCode.Created, afterLeaveJoin.StatusCode);
        using var afterLeaveBody = await JsonDocument.ParseAsync(await afterLeaveJoin.Content.ReadAsStreamAsync());
        Assert.AreEqual(2, afterLeaveBody.RootElement.GetProperty("position").GetInt32());

        using var leavePromoted = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/enrollments/waitlist/{firstWaitId}/cancel",
            firstWaitingToken);
        Assert.AreEqual(HttpStatusCode.NotFound, leavePromoted.StatusCode);
    }

    [TestMethod]
    public async Task NonnumericEnrollmentIdReturnsBadRequest()
    {
        var token = await LoginAsync("user010@club.test");
        using var response = await SendAuthorizedAsync(HttpMethod.Get, "/enrollments/not-a-number", token);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task AnalyticsLoadIsForbiddenForRegularUser()
    {
        var token = await LoginAsync("user012@club.test");
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        using var response = await SendAuthorizedAsync(
            HttpMethod.Get,
            $"/analytics/load?from={today}&to={today}",
            token);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task AnalyticsLoadAggregatesCapacityAndBookingsByUtcDateAndRoom()
    {
        const string email = "user011@club.test";
        const int firstCapacity = 12;
        const int secondCapacity = 8;
        const int firstBookedSeats = 3;
        const int secondBookedSeats = 2;
        var startsAt = DateTime.UtcNow.Date.AddDays(800).AddHours(9);
        long roomId;
        DateOnly classDate;

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var user = await dbContext.Users.SingleAsync(x => x.Email == email);
            var otherUser = await dbContext.Users.SingleAsync(x => x.Email == "user010@club.test");
            var room = await dbContext.Rooms.OrderBy(x => x.Id).FirstAsync();
            var otherRoom = await dbContext.Rooms.OrderBy(x => x.Id).Skip(1).FirstAsync();
            roomId = room.Id;
            classDate = DateOnly.FromDateTime(startsAt);

            var firstClass = new Class
            {
                RoomId = roomId,
                Title = "Analytics aggregation first class",
                StartsAt = startsAt,
                DurationMinutes = 60,
                Capacity = firstCapacity,
                IsCancelled = false
            };
            var secondClass = new Class
            {
                RoomId = roomId,
                Title = "Analytics aggregation second class",
                StartsAt = startsAt.AddHours(2),
                DurationMinutes = 60,
                Capacity = secondCapacity,
                IsCancelled = false
            };
            var cancelledClass = new Class
            {
                RoomId = roomId,
                Title = "Analytics aggregation cancelled class",
                StartsAt = startsAt.AddHours(4),
                DurationMinutes = 60,
                Capacity = 100,
                IsCancelled = true
            };
            var otherRoomClass = new Class
            {
                RoomId = otherRoom.Id,
                Title = "Analytics aggregation other room",
                StartsAt = startsAt.AddHours(6),
                DurationMinutes = 60,
                Capacity = 200,
                IsCancelled = false
            };
            dbContext.Classes.AddRange(firstClass, secondClass, cancelledClass, otherRoomClass);
            await dbContext.SaveChangesAsync();
            dbContext.Enrollments.AddRange(
                new Enrollment
                {
                    UserId = user.Id,
                    ClassId = firstClass.Id,
                    Seats = firstBookedSeats,
                    Status = "active",
                    CreatedAt = DateTime.UtcNow
                },
                new Enrollment
                {
                    UserId = otherUser.Id,
                    ClassId = secondClass.Id,
                    Seats = secondBookedSeats,
                    Status = "active",
                    CreatedAt = DateTime.UtcNow
                },
                new Enrollment
                {
                    UserId = otherUser.Id,
                    ClassId = secondClass.Id,
                    Seats = 4,
                    Status = "cancelled",
                    CreatedAt = DateTime.UtcNow
                },
                new Enrollment
                {
                    UserId = otherUser.Id,
                    ClassId = secondClass.Id,
                    Seats = 5,
                    Status = "waiting",
                    IsWaitlist = true,
                    CreatedAt = DateTime.UtcNow
                },
                new Enrollment
                {
                    UserId = user.Id,
                    ClassId = cancelledClass.Id,
                    Seats = 7,
                    Status = "active",
                    CreatedAt = DateTime.UtcNow
                },
                new Enrollment
                {
                    UserId = user.Id,
                    ClassId = otherRoomClass.Id,
                    Seats = 9,
                    Status = "active",
                    CreatedAt = DateTime.UtcNow
                });
            await dbContext.SaveChangesAsync();
        }

        var token = await LoginAsync("admin001@club.test");
        using var response = await SendAuthorizedAsync(
            HttpMethod.Get,
            $"/analytics/load?from={classDate:yyyy-MM-dd}&to={classDate:yyyy-MM-dd}&roomId={roomId}",
            token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var item = body.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.AreEqual(classDate.ToString("yyyy-MM-dd"), item.GetProperty("date").GetString());
        Assert.AreEqual(firstCapacity + secondCapacity, item.GetProperty("totalCapacity").GetInt32());
        Assert.AreEqual(firstBookedSeats + secondBookedSeats, item.GetProperty("bookedSeats").GetInt32());
        Assert.AreEqual(2, item.GetProperty("classesCount").GetInt32());

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var scheduledClass = await dbContext.Classes.SingleAsync(x => x.Title == "Analytics aggregation first class");
            scheduledClass.Capacity = firstCapacity + 10;
            await dbContext.SaveChangesAsync();
        }

        using var cachedResponse = await SendAuthorizedAsync(
            HttpMethod.Get,
            $"/analytics/load?from={classDate:yyyy-MM-dd}&to={classDate:yyyy-MM-dd}&roomId={roomId}",
            token);
        Assert.AreEqual(HttpStatusCode.OK, cachedResponse.StatusCode);
        using var cachedBody = await JsonDocument.ParseAsync(await cachedResponse.Content.ReadAsStreamAsync());
        var cachedItem = cachedBody.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.AreEqual(firstCapacity + secondCapacity, cachedItem.GetProperty("totalCapacity").GetInt32());
    }

    [TestMethod]
    public async Task ScheduleAcceptsEmptyAndRejectsWhitespaceOrNegativeContinuationTokens()
    {
        var token = await LoginAsync("user013@club.test");
        var from = Uri.EscapeDataString(DateTime.UtcNow.ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("O"));
        var negativeToken = Convert.ToBase64String(Encoding.UTF8.GetBytes("-1"));

        using var emptyTokenResponse = await SendAuthorizedAsync(
            HttpMethod.Get,
            $"/schedule?from={from}&to={to}&continuationToken=",
            token);
        Assert.AreEqual(HttpStatusCode.OK, emptyTokenResponse.StatusCode);

        using var whitespaceTokenResponse = await SendAuthorizedAsync(
            HttpMethod.Get,
            $"/schedule?from={from}&to={to}&continuationToken=%20%20",
            token);
        Assert.AreEqual(HttpStatusCode.BadRequest, whitespaceTokenResponse.StatusCode);

        using var negativeTokenResponse = await SendAuthorizedAsync(
            HttpMethod.Get,
            $"/schedule?from={from}&to={to}&continuationToken={Uri.EscapeDataString(negativeToken)}",
            token);
        Assert.AreEqual(HttpStatusCode.BadRequest, negativeTokenResponse.StatusCode);
    }

    [TestMethod]
    public async Task RateLimitIsSharedAcrossEnrollmentEndpoints()
    {
        var token = await LoginAsync("admin002@club.test");
        using var secondFactory = new ApiApplicationFactory();
        using var secondClient = secondFactory.CreateClient();
        var from = Uri.EscapeDataString(DateTime.UtcNow.ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("O"));
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        for (var requestNumber = 0; requestNumber < 60; requestNumber++)
        {
            var client = requestNumber < 30 ? _client : secondClient;
            var response = (requestNumber % 8) switch
            {
                0 => await SendAuthorizedAsync(client, HttpMethod.Post, "/enrollments", token, new { classId = 1, seats = 1 }),
                1 => await SendAuthorizedAsync(client, HttpMethod.Post, $"/enrollments/{long.MaxValue}/seats", token, new { seats = 1 }),
                2 => await SendAuthorizedAsync(client, HttpMethod.Post, $"/enrollments/{long.MaxValue}/cancel", token),
                3 => await SendAuthorizedAsync(client, HttpMethod.Get, $"/enrollments/{long.MaxValue}", token),
                4 => await SendAuthorizedAsync(client, HttpMethod.Get, $"/schedule?from={from}&to={to}", token),
                5 => await SendAuthorizedAsync(client, HttpMethod.Get, $"/analytics/load?from={today}&to={today}", token),
                6 => await SendAuthorizedAsync(client, HttpMethod.Post, "/enrollments/waitlist", token, new { classId = 1, seats = 1 }),
                _ => await SendAuthorizedAsync(client, HttpMethod.Post, $"/enrollments/waitlist/{long.MaxValue}/cancel", token)
            };

            Assert.AreNotEqual(HttpStatusCode.TooManyRequests, response.StatusCode, $"Request {requestNumber + 1} was rate limited early.");
            response.Dispose();
        }

        using var sixtyFirstResponse = await SendAuthorizedAsync(_client, HttpMethod.Get, $"/schedule?from={from}&to={to}", token);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, sixtyFirstResponse.StatusCode);
    }

    private static async Task<long> CreateClassAsync(string title, int capacity, DateTime startsAt)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var room = await dbContext.Rooms.OrderBy(x => x.Id).FirstAsync();
        var scheduledClass = new Class
        {
            RoomId = room.Id,
            Title = title,
            StartsAt = startsAt,
            DurationMinutes = 60,
            Capacity = capacity,
            IsCancelled = false
        };
        dbContext.Classes.Add(scheduledClass);
        await dbContext.SaveChangesAsync();
        return scheduledClass.Id;
    }

    private static async Task SetPassAsync(string email, int remainingVisits, DateOnly? validUntil = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var user = await dbContext.Users.SingleAsync(x => x.Email == email);
        var pass = await dbContext.Passes.SingleAsync(x => x.UserId == user.Id);
        pass.RemainingVisits = remainingVisits;
        pass.ValidUntil = validUntil ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
        await dbContext.SaveChangesAsync();
    }

    private static async Task<long> AddActiveEnrollmentAsync(string email, long classId, int seats, int remainingVisits)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var user = await dbContext.Users.SingleAsync(x => x.Email == email);
        var pass = await dbContext.Passes.SingleAsync(x => x.UserId == user.Id);
        pass.RemainingVisits = remainingVisits;
        pass.ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
        var enrollment = new Enrollment
        {
            UserId = user.Id,
            ClassId = classId,
            Seats = seats,
            Status = "active",
            CreatedAt = DateTime.UtcNow
        };
        dbContext.Enrollments.Add(enrollment);
        await dbContext.SaveChangesAsync();
        return enrollment.Id;
    }

    private static async Task<EnrollmentState> ReadEnrollmentStateAsync(string email, long classId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var user = await dbContext.Users.SingleAsync(x => x.Email == email);
        var pass = await dbContext.Passes.SingleAsync(x => x.UserId == user.Id);
        var activeEnrollments = await dbContext.Enrollments
            .Where(x => x.ClassId == classId && x.Status == "active")
            .Select(x => new { x.UserId, x.Seats })
            .ToListAsync();

        return new EnrollmentState(
            pass.RemainingVisits,
            activeEnrollments.Sum(x => x.Seats),
            activeEnrollments.Where(x => x.UserId == user.Id).Sum(x => x.Seats),
            activeEnrollments.Count(x => x.UserId == user.Id),
            await dbContext.Enrollments.CountAsync(x => x.ClassId == classId && x.UserId == user.Id && x.Status == "waiting"));
    }

    private sealed record EnrollmentState(
        int RemainingVisits,
        int ActiveSeats,
        int UserSeats,
        int UserActiveEnrollmentCount,
        int UserWaitingEnrollmentCount);

    private static async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method,
        string path,
        string token,
        object? body = null,
        string? idempotencyKey = null)
    {
        return await SendAuthorizedAsync(_client, method, path, token, body, idempotencyKey);
    }

    private static async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string token,
        object? body = null,
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (idempotencyKey != null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> LoginAsync(string email)
    {
        return await LoginAsync(_client, email);
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        var payload = JsonContent.Create(new { email, password = "Password123!" });
        var response = await client.PostAsync("/auth/login", payload);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }
}
