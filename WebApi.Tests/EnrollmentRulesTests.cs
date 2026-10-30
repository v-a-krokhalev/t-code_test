using DataAccessLayer.Entities;
using WebApi.Services;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void CreateConflictReason_RejectsInvalidEnrollmentConditions()
    {
        var scheduledClass = NewClass(capacity: 3);
        var pass = NewPass(remainingVisits: 2);

        Assert.AreEqual("capacity-exceeded", EnrollmentRules.GetCreateConflictReason(scheduledClass, pass, 2, 2, Now));
        Assert.AreEqual("pass-exhausted", EnrollmentRules.GetCreateConflictReason(scheduledClass, pass, 3, 0, Now));
        Assert.AreEqual("pass-expired", EnrollmentRules.GetCreateConflictReason(scheduledClass, NewPass(validUntil: DateOnly.FromDateTime(Now).AddDays(-1)), 1, 0, Now));
        Assert.AreEqual("class-cancelled", EnrollmentRules.GetCreateConflictReason(NewClass(isCancelled: true), pass, 1, 0, Now));
        Assert.AreEqual("class-started", EnrollmentRules.GetCreateConflictReason(NewClass(startsAt: Now), pass, 1, 0, Now));
    }

    [TestMethod]
    public void CreateConflictReason_AllowsValidEnrollment()
    {
        var reason = EnrollmentRules.GetCreateConflictReason(
            NewClass(capacity: 4),
            NewPass(remainingVisits: 2),
            requestedSeats: 2,
            activeSeats: 2,
            Now);

        Assert.IsNull(reason);
    }

    [TestMethod]
    public void SeatChangeConflictReason_RequiresValidPassOnlyForIncrease()
    {
        var enrollment = new Enrollment { Seats = 2, Status = "active" };
        var scheduledClass = NewClass(capacity: 5);
        var expiredPass = NewPass(remainingVisits: 5, validUntil: DateOnly.FromDateTime(Now).AddDays(-1));

        Assert.IsNull(EnrollmentRules.GetSeatChangeConflictReason(enrollment, scheduledClass, expiredPass, 1, 0, Now));
        Assert.IsNull(EnrollmentRules.GetSeatChangeConflictReason(enrollment, scheduledClass, expiredPass, 2, 0, Now));
        Assert.AreEqual("pass-expired", EnrollmentRules.GetSeatChangeConflictReason(enrollment, scheduledClass, expiredPass, 3, 0, Now));
        Assert.AreEqual("capacity-exceeded", EnrollmentRules.GetSeatChangeConflictReason(enrollment, NewClass(capacity: 4), NewPass(remainingVisits: 5), 3, 2, Now));
        Assert.AreEqual("pass-exhausted", EnrollmentRules.GetSeatChangeConflictReason(enrollment, scheduledClass, NewPass(remainingVisits: 0), 3, 0, Now));
    }

    [TestMethod]
    public void SeatChangeConflictReason_RejectsCancelledOrStartedEnrollment()
    {
        var enrollment = new Enrollment { Seats = 1, Status = "cancelled" };

        Assert.AreEqual("enrollment-cancelled", EnrollmentRules.GetSeatChangeConflictReason(enrollment, NewClass(), NewPass(), 1, 0, Now));
        enrollment.Status = "active";
        Assert.AreEqual("class-cancelled", EnrollmentRules.GetSeatChangeConflictReason(enrollment, NewClass(isCancelled: true), NewPass(), 1, 0, Now));
        Assert.AreEqual("class-started", EnrollmentRules.GetSeatChangeConflictReason(enrollment, NewClass(startsAt: Now), NewPass(), 1, 0, Now));
    }

    [TestMethod]
    public void GetRefundedVisits_UsesTwoHourBoundaryAndClubCancellation()
    {
        Assert.AreEqual(3, EnrollmentRules.GetRefundedVisits(3, Now.AddHours(2), false, Now));
        Assert.AreEqual(0, EnrollmentRules.GetRefundedVisits(3, Now.AddHours(1).AddMinutes(59), false, Now));
        Assert.AreEqual(3, EnrollmentRules.GetRefundedVisits(3, Now.AddHours(-1), true, Now));
    }

    private static Class NewClass(int capacity = 10, bool isCancelled = false, DateTime? startsAt = null)
    {
        return new Class
        {
            Capacity = capacity,
            IsCancelled = isCancelled,
            StartsAt = startsAt ?? Now.AddDays(1)
        };
    }

    private static Pass NewPass(int remainingVisits = 10, DateOnly? validUntil = null)
    {
        return new Pass
        {
            RemainingVisits = remainingVisits,
            ValidUntil = validUntil ?? DateOnly.FromDateTime(Now)
        };
    }
}