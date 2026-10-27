using System.ComponentModel.DataAnnotations;
using HealthTrack.Application;
using HealthTrack.Domain;
using Xunit;

namespace HealthTrack.Tests;
public class DomainTests
{
    [Theory]
    [InlineData(70, 175, 22.9)]
    [InlineData(100, 200, 25)]
    public void BmiUsesMetricUnits(decimal weight, decimal height, decimal expected) => Assert.Equal(expected, HealthMath.Bmi(weight, height));
    [Fact] public void MissingHeightOrWeightHasNoBmi() { Assert.Null(HealthMath.Bmi(70, null)); Assert.Null(HealthMath.Bmi(null, 175)); Assert.Null(HealthMath.Bmi(70, 0)); }
    [Fact] public void AdjacentAppointmentsDoNotOverlap()
    {
        var at = DateTimeOffset.UtcNow;
        Assert.False(HealthMath.Overlaps(at, at.AddMinutes(30), at.AddMinutes(30), at.AddHours(1)));
        Assert.True(HealthMath.Overlaps(at, at.AddMinutes(30), at.AddMinutes(29), at.AddHours(1)));
    }
    [Fact] public void PlanRequiresItemsAndCorrectLifecycle()
    {
        var plan = new HealthPlan(); Assert.Throws<ArgumentException>(() => plan.Transition(PlanStatus.Published));
        plan.Items.Add(new PlanItem()); plan.Transition(PlanStatus.Published); plan.Transition(PlanStatus.Archived);
        Assert.Throws<ArgumentException>(() => plan.Transition(PlanStatus.Published));
    }
    [Fact] public void ClientCannotConfirmOwnBooking()
    {
        var a = new Appointment { StartsAtUtc = DateTimeOffset.UtcNow.AddDays(1) };
        Assert.Throws<ArgumentException>(() => a.Transition(AppointmentStatus.Confirmed, true, DateTimeOffset.UtcNow));
        a.Transition(AppointmentStatus.Cancelled, true, DateTimeOffset.UtcNow); Assert.Equal(AppointmentStatus.Cancelled, a.Status);
    }
    [Fact] public void CompletionRequiresEndedConfirmedVisit()
    {
        var now = DateTimeOffset.UtcNow; var a = new Appointment { StartsAtUtc = now.AddDays(1), EndsAtUtc = now.AddDays(1).AddHours(1) };
        a.Transition(AppointmentStatus.Confirmed, false, now);
        Assert.Throws<ArgumentException>(() => a.Transition(AppointmentStatus.Completed, false, now));
        a.Transition(AppointmentStatus.Completed, false, now.AddDays(2));
    }
    [Fact] public void ReminderKeysAreStableAcrossTimezoneOffsets()
    {
        var at = DateTimeOffset.UtcNow; var id = Guid.NewGuid();
        Assert.Equal(HealthMath.ReminderKey("client", NotificationKind.Meal, id, at), HealthMath.ReminderKey("client", NotificationKind.Meal, id, at.ToOffset(TimeSpan.FromHours(5.5))));
        Assert.NotEqual(HealthMath.ReminderKey("client", NotificationKind.Meal, id, at), HealthMath.ReminderKey("client", NotificationKind.Meal, id, at.AddDays(1)));
    }
    [Fact] public void EmptyMeasurementAndInvalidBloodPressureAreRejected()
    {
        var input = new LogInput { Kind = LogKind.Measurement, Description = "Check" }; var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(input, new ValidationContext(input), results, true));
        input.WeightKg = 70; input.Systolic = 70; input.Diastolic = 90; results.Clear();
        Assert.False(Validator.TryValidateObject(input, new ValidationContext(input), results, true));
    }
}
