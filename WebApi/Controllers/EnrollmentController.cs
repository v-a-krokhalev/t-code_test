using System.Text.Json;
using Common.Caching;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebApi.Services;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
public class EnrollmentController : BaseApiController
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICacheService _cacheService;
    private readonly IRateLimitService _rateLimitService;
    private readonly IIdempotencyService _idempotencyService;

    public EnrollmentController(
        IUnitOfWork unitOfWork,
        ICacheService cacheService,
        IRateLimitService rateLimitService,
        IIdempotencyService idempotencyService)
    {
        _unitOfWork = unitOfWork;
        _cacheService = cacheService;
        _rateLimitService = rateLimitService;
        _idempotencyService = idempotencyService;
    }

    [HttpPost("enrollments")]
    public async Task<IActionResult> CreateEnrollment([FromBody] CreateEnrollmentRequest request, CancellationToken ct)
    {
        if (!await EnsureRateLimitAsync(_rateLimitService))
        {
            return TooManyRequests();
        }

        if (!TryGetIdempotencyKey(out var key, out var badKeyProblem))
        {
            return BadRequest(badKeyProblem);
        }

        if (request.ClassId <= 0 || request.Seats <= 0)
        {
            return BadRequest(CreateBadRequest("Invalid request"));
        }

        var bodyHash = _idempotencyService.ComputeBodyHash(JsonSerializer.Serialize(request));
        var keyHash = _idempotencyService.ComputeScopedKeyHash("create", key);
        await using var transaction = await _unitOfWork.DbContext.Database.BeginTransactionAsync(ct);
        await PostgresTransactionLocks.AcquireAsync(
            _unitOfWork.DbContext,
            $"idempotency:{CurrentUserId}:{keyHash}",
            ct);

        var previous = await _unitOfWork.Enrollment.Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.UserId == CurrentUserId && x.IdempotencyKeyHash == keyHash, ct);
        if (previous != null)
        {
            if (previous.IdempotencyBodyHash != bodyHash || previous.IdempotencyResponseJson == null)
            {
                // решение: повторное использование ключа с другим телом отклоняем, чтобы ключ не менял смысл операции.
                return ConflictResult("idempotency-key-reused");
            }

            await transaction.CommitAsync(ct);
            return ReplayCreatedResponse(previous.IdempotencyResponseJson);
        }

        await PostgresTransactionLocks.AcquireAsync(
            _unitOfWork.DbContext,
            $"class:{request.ClassId}",
            ct);

        var scheduledClass = await _unitOfWork
            .Class
            .Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == request.ClassId, ct);

        if (scheduledClass == null)
        {
            return NotFound();
        }

        await PostgresTransactionLocks.AcquireAsync(
            _unitOfWork.DbContext,
            $"pass:{CurrentUserId}",
            ct);

        var pass = await _unitOfWork.Pass.Query().FirstOrDefaultAsync(x => x.UserId == CurrentUserId, ct);
        var activeSeats = await _unitOfWork.Enrollment.Query()
            .Where(x => x.ClassId == request.ClassId && x.Status == "active")
            .SumAsync(x => (int?)x.Seats ?? 0, ct);
        var createConflict = EnrollmentRules.GetCreateConflictReason(
            scheduledClass,
            pass,
            request.Seats,
            activeSeats,
            DateTime.UtcNow);
        if (createConflict != null)
        {
            return ConflictResult(createConflict);
        }

        if (pass == null)
        {
            return ConflictResult("pass-expired");
        }

        var enrollment = new Enrollment
        {
            UserId = CurrentUserId,
            ClassId = request.ClassId,
            Seats = request.Seats,
            Status = "active",
            CreatedAt = DateTime.UtcNow,
            Class = scheduledClass,
            IdempotencyKeyHash = keyHash,
            IdempotencyBodyHash = bodyHash
        };

        pass.RemainingVisits -= request.Seats;
        await _unitOfWork.Enrollment.CreateAsync(enrollment, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        var response = new
        {
            id = enrollment.Id,
            classId = enrollment.ClassId,
            seats = enrollment.Seats,
            status = enrollment.Status,
            createdAt = enrollment.CreatedAt
        };

        var payload = JsonSerializer.Serialize(response);
        enrollment.IdempotencyResponseJson = payload;
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPost("enrollments/{id}/seats")]
    public async Task<IActionResult> UpdateEnrollmentSeats([FromRoute] long id, [FromBody] UpdateEnrollmentSeatsRequest request, CancellationToken ct)
    {
        if (!await EnsureRateLimitAsync(_rateLimitService))
        {
            return TooManyRequests();
        }

        if (id <= 0)
        {
            return BadRequest(CreateBadRequest("Invalid enrollment ID"));
        }

        if (request.Seats <= 0)
        {
            return BadRequest(CreateBadRequest("Invalid request"));
        }

        var enrollmentIdentity = await _unitOfWork.Enrollment.Query()
            .AsNoTracking()
            .Where(x => x.Id == id && x.UserId == CurrentUserId)
            .Select(x => new { x.ClassId })
            .FirstOrDefaultAsync(ct);
        if (enrollmentIdentity == null)
        {
            return NotFound();
        }

        await using var transaction = await _unitOfWork.DbContext.Database.BeginTransactionAsync(ct);
        await PostgresTransactionLocks.AcquireAsync(_unitOfWork.DbContext, $"class:{enrollmentIdentity.ClassId}", ct);
        await PostgresTransactionLocks.AcquireAsync(_unitOfWork.DbContext, $"pass:{CurrentUserId}", ct);

        var enrollment = await _unitOfWork.Enrollment.Query()
            .IgnoreQueryFilters()
            .Include(x => x.Class)
            .ThenInclude(x => x.Room)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == CurrentUserId, ct);
        if (enrollment == null || enrollment.Status == "waiting")
        {
            return NotFound();
        }

        var classEntity = enrollment.Class;

        var pass = await _unitOfWork.Pass.Query().FirstOrDefaultAsync(x => x.UserId == CurrentUserId, ct);
        var otherActiveSeats = await _unitOfWork.Enrollment.Query()
            .Where(x => x.ClassId == classEntity.Id && x.Status == "active" && x.Id != enrollment.Id)
            .SumAsync(x => (int?)x.Seats ?? 0, ct);
        var seatChangeConflict = EnrollmentRules.GetSeatChangeConflictReason(
            enrollment,
            classEntity,
            pass,
            request.Seats,
            otherActiveSeats,
            DateTime.UtcNow);
        if (seatChangeConflict != null)
        {
            return ConflictResult(seatChangeConflict);
        }

        if (pass == null)
        {
            return ConflictResult("pass-expired");
        }

        if (request.Seats == enrollment.Seats)
        {
            await transaction.CommitAsync(ct);
            return Ok(await BuildEnrollmentDetailAsync(enrollment, pass, classEntity));
        }

        if (request.Seats > enrollment.Seats)
        {
            var delta = request.Seats - enrollment.Seats;
            pass.RemainingVisits -= delta;
        }
        else
        {
            var delta = enrollment.Seats - request.Seats;
            pass.RemainingVisits += delta;
        }

        enrollment.Seats = request.Seats;
        _unitOfWork.Enrollment.Update(enrollment);
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Ok(await BuildEnrollmentDetailAsync(enrollment, pass, classEntity));
    }

    [HttpPost("enrollments/{id}/cancel")]
    public async Task<IActionResult> CancelEnrollment([FromRoute] long id, CancellationToken ct)
    {
        if (!await EnsureRateLimitAsync(_rateLimitService))
        {
            return TooManyRequests();
        }

        if (id <= 0)
        {
            return BadRequest(CreateBadRequest("Invalid enrollment ID"));
        }

        var enrollmentIdentity = await _unitOfWork.Enrollment.Query()
            .AsNoTracking()
            .Where(x => x.Id == id && x.UserId == CurrentUserId)
            .Select(x => new { x.ClassId })
            .FirstOrDefaultAsync(ct);
        if (enrollmentIdentity == null)
        {
            return NotFound();
        }

        await using var transaction = await _unitOfWork.DbContext.Database.BeginTransactionAsync(ct);
        await PostgresTransactionLocks.AcquireAsync(_unitOfWork.DbContext, $"class:{enrollmentIdentity.ClassId}", ct);

        var scheduledClass = await _unitOfWork.Class.Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == enrollmentIdentity.ClassId, ct);
        if (scheduledClass == null)
        {
            return NotFound();
        }

        var enrollment = await _unitOfWork.Enrollment.Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == CurrentUserId, ct);
        if (enrollment == null)
        {
            return NotFound();
        }

        if (enrollment.Status == "cancelled")
        {
            await transaction.CommitAsync(ct);
            return Ok(new { id = enrollment.Id, status = "cancelled", refundedVisits = enrollment.RefundedVisits });
        }

        if (enrollment.Status == "waiting")
        {
            return NotFound();
        }

        if (!scheduledClass.IsCancelled && scheduledClass.StartsAt <= DateTime.UtcNow)
        {
            return ConflictResult("class-started");
        }

        var waitingUserIds = await _unitOfWork.Enrollment.Query()
            .Where(x => x.ClassId == scheduledClass.Id && x.Status == "waiting")
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(ct);
        foreach (var userId in waitingUserIds.Append(CurrentUserId).Distinct().OrderBy(x => x))
        {
            await PostgresTransactionLocks.AcquireAsync(_unitOfWork.DbContext, $"pass:{userId}", ct);
        }

        var pass = await _unitOfWork.Pass.Query().FirstOrDefaultAsync(x => x.UserId == CurrentUserId, ct);
        var refundedVisits = 0;
        if (pass != null)
        {
            refundedVisits = EnrollmentRules.GetRefundedVisits(
                enrollment.Seats,
                scheduledClass.StartsAt,
                scheduledClass.IsCancelled,
                DateTime.UtcNow);
            pass.RemainingVisits += refundedVisits;
        }

        enrollment.Status = "cancelled";
        enrollment.CancelledAt = DateTime.UtcNow;
        enrollment.RefundedVisits = refundedVisits;
        _unitOfWork.Enrollment.Update(enrollment);
        await _unitOfWork.SaveChangesAsync(ct);
        await PromoteWaitingListAsync(scheduledClass.Id, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Ok(new { id = enrollment.Id, status = "cancelled", refundedVisits });
    }

    [HttpGet("enrollments/{id}")]
    public async Task<IActionResult> GetEnrollment([FromRoute] long id, CancellationToken ct)
    {
        if (!await EnsureRateLimitAsync(_rateLimitService))
        {
            return TooManyRequests();
        }

        if (id <= 0)
        {
            return BadRequest(CreateBadRequest("Invalid enrollment ID"));
        }

        var enrollment = await _unitOfWork
            .Enrollment
            .Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == CurrentUserId, ct);

        if (enrollment == null)
        {
            return NotFound();
        }

        if (enrollment.Status == "waiting")
        {
            return NotFound();
        }

        var classDetails = await GetCachedClassDetailsAsync(enrollment.ClassId, ct);
        if (classDetails == null)
        {
            return NotFound();
        }

        var pass = await _unitOfWork.Pass.Query().FirstOrDefaultAsync(x => x.UserId == CurrentUserId, ct);
        return Ok(new
        {
            id = enrollment.Id,
            status = enrollment.Status,
            seats = enrollment.Seats,
            createdAt = enrollment.CreatedAt,
            @class = new
            {
                id = classDetails.Id,
                title = classDetails.Title,
                startsAt = classDetails.StartsAt,
                durationMinutes = classDetails.DurationMinutes,
                isCancelled = classDetails.IsCancelled,
                room = new
                {
                    id = classDetails.RoomId,
                    name = classDetails.RoomName
                }
            },
            pass = new
            {
                remainingVisits = pass?.RemainingVisits ?? 0,
                validUntil = pass?.ValidUntil ?? DateOnly.FromDateTime(DateTime.UtcNow)
            }
        });
    }

    [HttpGet("schedule")]
    public async Task<IActionResult> GetSchedule(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        [FromQuery] long? roomId,
        [FromQuery] int limit = 20,
        [FromQuery] string? continuationToken = null,
        CancellationToken ct = default)
    {
        if (!await EnsureRateLimitAsync(_rateLimitService))
        {
            return TooManyRequests();
        }

        if (from == default || to == default || to <= from)
        {
            return BadRequest(CreateBadRequest("Invalid period"));
        }

        if (limit is < 1 or > 100)
        {
            return BadRequest(CreateBadRequest("Invalid limit"));
        }

        if (roomId is <= 0)
        {
            return BadRequest(CreateBadRequest("Invalid room ID"));
        }

        var skip = 0;
        var suppliedContinuationToken = Request.Query.TryGetValue("continuationToken", out var queryToken)
            ? queryToken.ToString()
            : continuationToken;
        if (!string.IsNullOrEmpty(suppliedContinuationToken) &&
            (!TryParseContinuationToken(suppliedContinuationToken, out skip) || skip < 0))
        {
            return BadRequest(CreateBadRequest("Invalid continuation token"));
        }

        var query = _unitOfWork
            .Class
            .Query()
            .Include(x => x.Room)
            .Where(x => !x.IsCancelled && x.StartsAt >= from && x.StartsAt < to)
            .AsQueryable();

        if (roomId.HasValue)
        {
            query = query.Where(x => x.RoomId == roomId.Value);
        }

        var classes = await query
            .OrderBy(x => x.StartsAt)
            .ThenBy(x => x.Id)
            .Skip(skip)
            .Take(limit + 1)
            .ToListAsync(ct);

        var page = classes.Take(limit).ToList();
        var nextToken = classes.Count > limit ? EncodeContinuationToken(skip + limit) : null;
        var items = new List<object>();
        var classIds = page.Select(x => x.Id).ToArray();
        var bookedSeatsByClass = await _unitOfWork.Enrollment.Query()
            .Where(x => classIds.Contains(x.ClassId) && x.Status == "active")
            .GroupBy(x => x.ClassId)
            .Select(x => new { ClassId = x.Key, Seats = x.Sum(y => y.Seats) })
            .ToDictionaryAsync(x => x.ClassId, x => x.Seats, ct);
        var mySeatsByClass = await _unitOfWork.Enrollment.Query()
            .Where(x => classIds.Contains(x.ClassId) && x.UserId == CurrentUserId && x.Status == "active")
            .GroupBy(x => x.ClassId)
            .Select(x => new { ClassId = x.Key, Seats = x.Sum(y => y.Seats) })
            .ToDictionaryAsync(x => x.ClassId, x => x.Seats, ct);

        foreach (var scheduledClass in page)
        {
            bookedSeatsByClass.TryGetValue(scheduledClass.Id, out var bookedSeats);
            mySeatsByClass.TryGetValue(scheduledClass.Id, out var mySeats);

            items.Add(new
            {
                classId = scheduledClass.Id,
                title = scheduledClass.Title,
                startsAt = scheduledClass.StartsAt,
                durationMinutes = scheduledClass.DurationMinutes,
                room = new { id = scheduledClass.Room.Id, name = scheduledClass.Room.Name },
                capacity = scheduledClass.Capacity,
                freeSeats = Math.Max(0, scheduledClass.Capacity - bookedSeats),
                mySeats
            });
        }

        return Ok(new { items, continuationToken = nextToken });
    }

    [HttpGet("analytics/load")]
    [Authorize(Roles = AdminRole)]
    public async Task<IActionResult> GetLoad(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        [FromQuery] long? roomId,
        CancellationToken ct)
    {
        if (!await EnsureRateLimitAsync(_rateLimitService))
        {
            return TooManyRequests();
        }

        if (from == default || to == default || to < from)
        {
            return BadRequest(CreateBadRequest("Invalid period"));
        }

        if (roomId is <= 0)
        {
            return BadRequest(CreateBadRequest("Invalid room ID"));
        }

        var cacheKey = $"fitnessclub:analytics-load:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}:{roomId?.ToString() ?? "all"}";
        var cachedSummary = await _cacheService.GetAsync<List<AnalyticsLoadCacheItem>>(cacheKey, ct);
        if (cachedSummary != null)
        {
            return Ok(new { items = cachedSummary });
        }

        var query = _unitOfWork
            .Class
            .Query()
            .Where(x => !x.IsCancelled && DateOnly.FromDateTime(x.StartsAt) >= from && DateOnly.FromDateTime(x.StartsAt) <= to)
            .AsQueryable();

        if (roomId.HasValue)
        {
            query = query.Where(x => x.RoomId == roomId.Value);
        }

        var groupedLoad = await query
            .Select(scheduledClass => new
            {
                Date = scheduledClass.StartsAt.Date,
                scheduledClass.RoomId,
                RoomName = scheduledClass.Room.Name,
                scheduledClass.Capacity,
                BookedSeats = scheduledClass.Enrollments
                    .Where(x => x.Status == "active")
                    .Sum(x => (int?)x.Seats) ?? 0
            })
            .GroupBy(x => new { x.Date, x.RoomId, x.RoomName })
            .Select(dayRoom => new
            {
                dayRoom.Key.Date,
                dayRoom.Key.RoomId,
                dayRoom.Key.RoomName,
                ClassesCount = dayRoom.Count(),
                TotalCapacity = dayRoom.Sum(x => x.Capacity),
                BookedSeats = dayRoom.Sum(x => x.BookedSeats)
            })
            .OrderBy(x => x.Date)
            .ThenBy(x => x.RoomId)
            .ToListAsync(ct);
        var summary = groupedLoad.Select(x => new AnalyticsLoadCacheItem(
            DateOnly.FromDateTime(x.Date),
            new AnalyticsRoomCacheItem(x.RoomId, x.RoomName),
            x.ClassesCount,
            x.TotalCapacity,
            x.BookedSeats)).ToList();

        // решение: аналитика кэшируется на 30 секунд, чтобы снять повторные агрегаты с базы и ограничить устаревание.
        await _cacheService.SetAsync(cacheKey, summary, TimeSpan.FromSeconds(30), ct);
        return Ok(new { items = summary });
    }

    [HttpPost("enrollments/waitlist")]
    public async Task<IActionResult> JoinWaitlist([FromBody] CreateEnrollmentRequest request, CancellationToken ct)
    {
        if (!await EnsureRateLimitAsync(_rateLimitService))
        {
            return TooManyRequests();
        }

        if (!TryGetIdempotencyKey(out var key, out var badKeyProblem))
        {
            return BadRequest(badKeyProblem);
        }

        if (request.ClassId <= 0 || request.Seats <= 0)
        {
            return BadRequest(CreateBadRequest("Invalid request"));
        }

        var bodyHash = _idempotencyService.ComputeBodyHash(JsonSerializer.Serialize(request));
        var keyHash = _idempotencyService.ComputeScopedKeyHash("waitlist", key);
        await using var transaction = await _unitOfWork.DbContext.Database.BeginTransactionAsync(ct);
        await PostgresTransactionLocks.AcquireAsync(
            _unitOfWork.DbContext,
            $"idempotency:{CurrentUserId}:{keyHash}",
            ct);

        var previous = await _unitOfWork.Enrollment.Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.UserId == CurrentUserId && x.IdempotencyKeyHash == keyHash, ct);
        if (previous != null)
        {
            if (previous.IdempotencyBodyHash != bodyHash || previous.IdempotencyResponseJson == null)
            {
                // решение: повторное использование ключа с другим телом отклоняем, чтобы ключ не менял смысл операции.
                return ConflictResult("idempotency-key-reused");
            }

            await transaction.CommitAsync(ct);
            return ReplayCreatedResponse(previous.IdempotencyResponseJson);
        }

        await PostgresTransactionLocks.AcquireAsync(
            _unitOfWork.DbContext,
            $"class:{request.ClassId}",
            ct);

        var scheduledClass = await _unitOfWork.Class.Query().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == request.ClassId, ct);
        if (scheduledClass == null)
        {
            return NotFound();
        }

        if (scheduledClass.IsCancelled)
        {
            return ConflictResult("class-cancelled");
        }

        if (scheduledClass.StartsAt <= DateTime.UtcNow)
        {
            return ConflictResult("class-started");
        }

        var bookedSeats = await _unitOfWork.Enrollment.Query()
            .Where(x => x.ClassId == request.ClassId && x.Status == "active")
            .SumAsync(x => (int?)x.Seats ?? 0, ct);
        if (bookedSeats + request.Seats <= scheduledClass.Capacity)
        {
            return ConflictResult("seats-available");
        }

        var existing = await _unitOfWork
            .Enrollment
            .Query()
            .FirstOrDefaultAsync(x => x.UserId == CurrentUserId && x.ClassId == request.ClassId && x.Status == "waiting", ct);

        if (existing != null)
        {
            return ConflictResult("already-waiting");
        }

        var waitlistEntry = new Enrollment
        {
            UserId = CurrentUserId,
            ClassId = request.ClassId,
            Seats = request.Seats,
            Status = "waiting",
            CreatedAt = DateTime.UtcNow,
            Class = scheduledClass,
            IsWaitlist = true,
            IdempotencyKeyHash = keyHash,
            IdempotencyBodyHash = bodyHash
        };

        await _unitOfWork.Enrollment.CreateAsync(waitlistEntry, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        var position = await ComputeWaitingPositionAsync(request.ClassId, waitlistEntry.Id, waitlistEntry.CreatedAt, ct);
        var response = new
        {
            id = waitlistEntry.Id,
            classId = waitlistEntry.ClassId,
            seats = waitlistEntry.Seats,
            position,
            createdAt = waitlistEntry.CreatedAt
        };

        waitlistEntry.IdempotencyResponseJson = JsonSerializer.Serialize(response);
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPost("enrollments/waitlist/{id}/cancel")]
    public async Task<IActionResult> LeaveWaitlist([FromRoute] long id, CancellationToken ct)
    {
        if (!await EnsureRateLimitAsync(_rateLimitService))
        {
            return TooManyRequests();
        }

        if (id <= 0)
        {
            return BadRequest(CreateBadRequest("Invalid waitlist ID"));
        }

        var classId = await _unitOfWork.Enrollment.Query()
            .Where(x => x.Id == id && x.UserId == CurrentUserId && x.IsWaitlist)
            .Select(x => (long?)x.ClassId)
            .FirstOrDefaultAsync(ct);
        if (classId == null)
        {
            return NotFound();
        }

        await using var transaction = await _unitOfWork.DbContext.Database.BeginTransactionAsync(ct);
        await PostgresTransactionLocks.AcquireAsync(_unitOfWork.DbContext, $"class:{classId.Value}", ct);
        var entry = await _unitOfWork.Enrollment.Query()
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == CurrentUserId && x.IsWaitlist, ct);
        if (entry == null || (entry.Status != "waiting" && !(entry.Status == "cancelled" && entry.WaitlistLeft)))
        {
            return NotFound();
        }

        if (entry.Status == "cancelled")
        {
            await transaction.CommitAsync(ct);
            return Ok(new { id = entry.Id, status = "cancelled" });
        }

        entry.Status = "cancelled";
        entry.CancelledAt = DateTime.UtcNow;
        entry.WaitlistLeft = true;
        _unitOfWork.Enrollment.Update(entry);
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Ok(new { id = entry.Id, status = "cancelled" });
    }

    private async Task PromoteWaitingListAsync(long classId, CancellationToken ct)
    {
        var waitingEntries = await _unitOfWork
            .Enrollment
            .Query()
            .Where(x => x.ClassId == classId && x.Status == "waiting")
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);

        if (waitingEntries.Count == 0)
        {
            return;
        }

        var currentClass = await _unitOfWork.Class.Query().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == classId, ct);
        if (currentClass == null)
        {
            return;
        }

        if (currentClass.IsCancelled)
        {
            // решение: после отмены клубом оставляем очередь без изменений и не выдаём ожидающим места.
            return;
        }

        var activeSeats = await _unitOfWork.Enrollment.Query()
            .Where(x => x.ClassId == classId && x.Status == "active")
            .SumAsync(x => (int?)x.Seats ?? 0, ct);
        var remainingCapacity = currentClass.Capacity - activeSeats;
        var passesByUser = await _unitOfWork.Pass.Query()
            .Where(x => waitingEntries.Select(e => e.UserId).Distinct().Contains(x.UserId))
            .ToDictionaryAsync(x => x.UserId, ct);

        foreach (var entry in waitingEntries)
        {
            if (remainingCapacity <= 0)
            {
                break;
            }

            if (entry.Seats > remainingCapacity)
            {
                // решение: сохраняем строгий FIFO, поэтому большая первая заявка удерживает место до подходящей вместимости.
                break;
            }

            if (!passesByUser.TryGetValue(entry.UserId, out var pass) ||
                pass.ValidUntil < DateOnly.FromDateTime(DateTime.UtcNow) ||
                pass.RemainingVisits < entry.Seats)
            {
                // решение: пропускаем ожидающего, который сейчас не может оплатить места, чтобы следующий допустимый мог пройти.
                continue;
            }

            entry.Status = "active";
            entry.CancelledAt = null;
            pass.RemainingVisits -= entry.Seats;
            remainingCapacity -= entry.Seats;
            _unitOfWork.Enrollment.Update(entry);
        }

        await _unitOfWork.SaveChangesAsync(ct);
    }

    private async Task<int> ComputeWaitingPositionAsync(long classId, long enrollmentId, DateTime createdAt, CancellationToken ct)
    {
        return await _unitOfWork
            .Enrollment
            .Query()
            .CountAsync(x => x.ClassId == classId && x.Status == "waiting" &&
                (x.CreatedAt < createdAt || (x.CreatedAt == createdAt && x.Id <= enrollmentId)), ct);
    }

    private IActionResult ReplayCreatedResponse(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        return StatusCode(StatusCodes.Status201Created, document.RootElement.Clone());
    }

    private async Task<CachedClassDetails?> GetCachedClassDetailsAsync(long classId, CancellationToken ct)
    {
        var cacheKey = $"fitnessclub:class-details:{classId}";
        var cached = await _cacheService.GetAsync<CachedClassDetails>(cacheKey, ct);
        if (cached != null)
        {
            return cached;
        }

        var details = await _unitOfWork.Class.Query()
            .IgnoreQueryFilters()
            .Where(x => x.Id == classId)
            .Select(x => new
            {
                x.Id,
                x.Title,
                x.StartsAt,
                x.DurationMinutes,
                x.IsCancelled,
                RoomId = x.Room.Id,
                RoomName = x.Room.Name
            })
            .FirstOrDefaultAsync(ct);

        if (details == null)
        {
            return null;
        }

        var result = new CachedClassDetails(
            details.Id,
            details.Title,
            details.StartsAt,
            details.DurationMinutes,
            details.IsCancelled,
            details.RoomId,
            details.RoomName);
        await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromMinutes(10), ct);
        return result;
    }

    public sealed record CachedClassDetails(
        long Id,
        string Title,
        DateTime StartsAt,
        int DurationMinutes,
        bool IsCancelled,
        long RoomId,
        string RoomName);

    public sealed record AnalyticsLoadCacheItem(
        DateOnly Date,
        AnalyticsRoomCacheItem Room,
        int ClassesCount,
        int TotalCapacity,
        int BookedSeats);

    public sealed record AnalyticsRoomCacheItem(long Id, string Name);

    private async Task<object> BuildEnrollmentDetailAsync(Enrollment enrollment, Pass pass, Class classEntity)
    {
        return new
        {
            id = enrollment.Id,
            status = enrollment.Status,
            seats = enrollment.Seats,
            createdAt = enrollment.CreatedAt,
            @class = new
            {
                id = classEntity.Id,
                title = classEntity.Title,
                startsAt = classEntity.StartsAt,
                durationMinutes = classEntity.DurationMinutes,
                isCancelled = classEntity.IsCancelled,
                room = new
                {
                    id = classEntity.Room.Id,
                    name = classEntity.Room.Name
                }
            },
            pass = new
            {
                remainingVisits = pass.RemainingVisits,
                validUntil = pass.ValidUntil
            }
        };
    }

    private bool TryGetIdempotencyKey(out string key, out ProblemDetails problem)
    {
        key = string.Empty;
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var values))
        {
            problem = CreateBadRequest("Missing Idempotency-Key");
            return false;
        }

        key = values.ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            problem = CreateBadRequest("Missing Idempotency-Key");
            return false;
        }

        problem = new ProblemDetails();
        return true;
    }

    private IActionResult ConflictResult(string reason)
    {
        var problem = CreateConflict(reason);
        problem.Extensions["reason"] = reason;
        return Conflict(problem);
    }

    private static bool TryParseContinuationToken(string token, out int skip)
    {
        skip = 0;
        try
        {
            var bytes = Convert.FromBase64String(token);
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            return int.TryParse(text, out skip);
        }
        catch
        {
            return false;
        }
    }

    private static string EncodeContinuationToken(int nextOffset)
    {
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(nextOffset.ToString()));
    }

    public sealed record CreateEnrollmentRequest
    {
        public long ClassId { get; init; }

        public int Seats { get; init; }
    }

    public sealed record UpdateEnrollmentSeatsRequest
    {
        public int Seats { get; init; }
    }
}
