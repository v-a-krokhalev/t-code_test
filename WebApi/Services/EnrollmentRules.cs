using DataAccessLayer.Entities;

namespace WebApi.Services;

public static class EnrollmentRules
{
    public static string? GetCreateConflictReason(
        Class scheduledClass,
        Pass? pass,
        int requestedSeats,
        int activeSeats,
        DateTime now)
    {
        if (pass == null || pass.ValidUntil < DateOnly.FromDateTime(now))
        {
            return "pass-expired";
        }

        if (scheduledClass.IsCancelled)
        {
            return "class-cancelled";
        }

        if (scheduledClass.StartsAt <= now)
        {
            return "class-started";
        }

        if (activeSeats + requestedSeats > scheduledClass.Capacity)
        {
            return "capacity-exceeded";
        }

        if (pass.RemainingVisits < requestedSeats)
        {
            return "pass-exhausted";
        }

        return null;
    }

    public static string? GetSeatChangeConflictReason(
        Enrollment enrollment,
        Class scheduledClass,
        Pass? pass,
        int requestedSeats,
        int otherActiveSeats,
        DateTime now)
    {
        if (enrollment.Status == "cancelled")
        {
            return "enrollment-cancelled";
        }

        if (scheduledClass.IsCancelled)
        {
            return "class-cancelled";
        }

        if (scheduledClass.StartsAt <= now)
        {
            return "class-started";
        }

        if (requestedSeats <= enrollment.Seats)
        {
            return null;
        }

        if (pass == null || pass.ValidUntil < DateOnly.FromDateTime(now))
        {
            return "pass-expired";
        }

        if (otherActiveSeats + requestedSeats > scheduledClass.Capacity)
        {
            return "capacity-exceeded";
        }

        if (pass.RemainingVisits < requestedSeats - enrollment.Seats)
        {
            return "pass-exhausted";
        }

        return null;
    }

    public static int GetRefundedVisits(int seats, DateTime startsAt, bool classCancelled, DateTime now)
    {
        return classCancelled || now.AddHours(2) <= startsAt ? seats : 0;
    }
}