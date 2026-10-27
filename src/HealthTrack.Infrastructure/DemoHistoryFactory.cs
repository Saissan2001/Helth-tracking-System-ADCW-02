using System.Security.Cryptography;
using System.Text;
using HealthTrack.Domain;

namespace HealthTrack.Infrastructure;

// Synthetic scenarios, not actual people, nutritional reference data or clinical recommendations.
public sealed class DemoHistory
{
    public List<HealthLog> Logs { get; } = [];
    public List<HealthPlan> Plans { get; } = [];
    public List<Appointment> Appointments { get; } = [];
    public List<TrainerFeedback> Feedback { get; } = [];
    public WellnessPreferences Preferences { get; set; } = new();
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
}

public static class DemoHistoryFactory
{
    public const int Days = 150;
    public const string Version = "realistic-history-v1";
    public static Guid Key(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(Version + ":" + value)).AsSpan(0, 16));
    private record Food(string Name, decimal Protein, decimal Carbs, decimal Fat);
    private static readonly Food[][] Meals = [
        [new("Oats with milk, banana and peanut butter", 22, 66, 16), new("Two eggs, wholegrain toast and papaya", 23, 51, 19), new("String hoppers, dhal and coconut sambol", 15, 78, 18), new("Greek yogurt, mango and granola", 25, 62, 14), new("Chickpeas, grated coconut and a banana", 18, 73, 15), new("Egg hoppers with dhal", 22, 68, 20)],
        [new("Red rice, chicken curry, beans and cucumber", 38, 85, 21), new("Rice, dhal, beetroot and grilled fish", 35, 91, 18), new("Chickpea and roasted vegetable rice bowl", 23, 97, 22), new("Chicken and vegetable noodles", 34, 89, 24), new("Tuna sandwich, fruit and yogurt", 36, 74, 19), new("Rice, tofu curry, dhal and leafy greens", 29, 93, 23)],
        [new("Grilled fish, sweet potato and vegetables", 37, 62, 20), new("Chicken stir-fry with rice", 40, 77, 22), new("Dhal, roti and mixed vegetable curry", 24, 88, 23), new("Pasta with chicken and tomato sauce", 38, 94, 21), new("Tofu, rice and sauteed vegetables", 29, 78, 24), new("Homemade chicken kottu with vegetables", 36, 96, 28)],
        [new("Yogurt and a small handful of cashews", 12, 19, 13), new("Banana and milk", 9, 39, 6), new("Apple and peanut butter", 6, 29, 10), new("Tea with milk and two biscuits", 4, 30, 7), new("Roasted chickpeas and an orange", 10, 38, 5)]
    ];
    public static DemoHistory Build(string client, string trainer, int persona, DateTimeOffset now, string timeZone)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var start = today.AddDays(-Days);
        var data = new DemoHistory { Start = start, End = today };
        var random = new Random(8129 + persona * 1783);
        Guid Id(string value) => Key(client + ":" + value);
        DateTimeOffset At(DateOnly day, int minute) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue).AddMinutes(minute), DateTimeKind.Unspecified), zone));
        bool Add(HealthLog log) { if (log.OccurredAtUtc > now) return false; data.Logs.Add(log); return true; }
        var workoutDays = persona == 0 ? new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday, DayOfWeek.Saturday } : new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday, DayOfWeek.Saturday, DayOfWeek.Sunday };
        for (var phase = 0; phase < 3; phase++)
        {
            var from = start.AddDays(phase * 50); var to = phase < 2 ? from.AddDays(49) : today.AddDays(28);
            var workout = new HealthPlan { Id = Id($"workout-plan-{phase}"), ClientId = client, TrainerId = trainer, Kind = PlanKind.Workout, StartDate = from, EndDate = to,
                Title = new[] { "Finding a weekly rhythm", "Building consistency", "A routine that fits real life" }[phase],
                Instructions = "Alternate active and easy days. Adapt a session when work or family plans change, and record the activity you actually complete.", Status = phase < 2 ? PlanStatus.Archived : PlanStatus.Published };
            foreach (var day in workoutDays)
            {
                var routine = day == DayOfWeek.Saturday ? (persona == 0 ? WorkoutRoutine.Walking : WorkoutRoutine.Cycling) : persona == 0 ? WorkoutRoutine.StrengthTraining : day == DayOfWeek.Sunday ? WorkoutRoutine.Yoga : WorkoutRoutine.Running;
                workout.Items.Add(new() { Id = Id($"workout-{phase}-{day}"), PlanId = workout.Id, Day = day, LocalTime = new(18, 15), Routine = routine,
                    Description = routine switch { WorkoutRoutine.Walking => "Weekend park walk", WorkoutRoutine.Cycling => "Easy neighbourhood ride", WorkoutRoutine.Yoga => "Yoga and stretching", WorkoutRoutine.Running => "Easy run at a conversational pace", _ => "Full-body strength session" },
                    DurationMinutes = routine == WorkoutRoutine.Yoga ? 25 : 35 + phase * 5 });
            }
            var diet = new HealthPlan { Id = Id($"diet-plan-{phase}"), ClientId = client, TrainerId = trainer, Kind = PlanKind.Diet, StartDate = from, EndDate = to,
                Title = new[] { "Regular meals around work", "More variety through the week", "Flexible meals and preparation" }[phase],
                Instructions = "A flexible meal schedule, not a prescription. Swap foods to suit your preferences and availability. Log actual portions separately; sample amounts are estimates.", Status = workout.Status };
            foreach (var day in Enum.GetValues<DayOfWeek>()) for (var meal = 0; meal < 3; meal++)
                diet.Items.Add(new() { Id = Id($"meal-{phase}-{day}-{meal}"), PlanId = diet.Id, Day = day, LocalTime = new[] { new TimeOnly(7, 45), new(12, 45), new(19, 45) }[meal], MealType = new[] { "Breakfast", "Lunch", "Dinner" }[meal], Description = new[] { "Breakfast with a protein source and fruit", "Lunch with grains, vegetables and a protein source", "A flexible evening meal" }[meal] });
            data.Plans.AddRange([workout, diet]);
        }
        decimal drift = 0;
        for (var index = 0; index <= Days; index++)
        {
            var day = start.AddDays(index); var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var phase = Math.Min(index / 50, 2); var trip = index is >= 67 and <= 70;
            // Occasional missed logging is intentional; keep recent days populated for the demo.
            if (index < Days - 7 && index % 47 == 19) continue;
            var wakeMinute = (weekend ? 8 * 60 : 6 * 60 + 40) + random.Next(-25, 36) + persona * 15;
            var lateNight = index % 13 == 6;
            var sleepMinutes = (weekend ? 475 : 435) + random.Next(-40, 41) - (lateNight ? 80 : 0);
            if (!trip && random.NextDouble() > .06)
            {
                var wake = At(day, wakeMinute);
                Add(new() { Id = Id($"{day}-sleep"), ClientId = client, Kind = LogKind.Sleep, OccurredAtUtc = wake, SleepStartedAtUtc = wake.AddMinutes(-sleepMinutes), DurationMinutes = sleepMinutes,
                    Description = "Overnight sleep", Notes = lateNight ? "Stayed up later than usual finishing work. Woke before the alarm." : weekend ? "No early alarm today. A slower start to the morning." : index % 5 == 0 ? "Woke once during the night, then went back to sleep." : "" });
            }
            drift = Math.Clamp(drift + (decimal)(random.NextDouble() - .5) * .12m, -.35m, .35m);
            if (index == 0 || day.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Thursday || index == Days - 1)
            {
                var weight = persona == 0 ? 76.4m - index * .018m : 64.1m + .22m * (decimal)Math.Sin(index / 19d);
                Add(new() { Id = Id($"{day}-weight"), ClientId = client, Kind = LogKind.Measurement, OccurredAtUtc = At(day, wakeMinute + 15), Description = "Morning weigh-in", WeightKg = Math.Round(weight + drift + random.Next(-25, 26) / 100m, 1), HeightCm = index == 0 ? persona == 0 ? 175 : 168 : null,
                    HeartRate = random.Next(61, 74) + (lateNight ? 3 : 0), Notes = index % 9 == 0 ? "Before breakfast, using the same scale." : "" });
            }
            if (index % 14 == 0) Add(new() { Id = Id($"{day}-bp"), ClientId = client, Kind = LogKind.Measurement, OccurredAtUtc = At(day, wakeMinute + 25), Description = "Seated blood-pressure reading", Systolic = random.Next(112, 127), Diastolic = random.Next(70, 83), Notes = "Recorded after sitting quietly for a few minutes." });
            var scheduled = data.Plans.Single(p => p.Kind == PlanKind.Workout && p.StartDate <= day && p.EndDate >= day).Items.FirstOrDefault(x => x.Day == day.DayOfWeek);
            var completed = scheduled != null && !trip && random.NextDouble() > .18;
            var bonusWalk = scheduled == null && !trip && random.NextDouble() < .45;
            var routine = completed ? scheduled!.Routine : WorkoutRoutine.Walking;
            var duration = completed ? Math.Max(20, scheduled!.DurationMinutes!.Value + random.Next(-10, 16)) : random.Next(18, 36);
            var active = false;
            if (completed || bonusWalk)
            {
                var minutes = weekend ? 9 * 60 + random.Next(0, 46) : 18 * 60 + random.Next(0, 51);
                var workoutEnd = minutes + duration;
                var met = routine switch { WorkoutRoutine.Running => 7m, WorkoutRoutine.Cycling => 5.5m, WorkoutRoutine.StrengthTraining => 4m, WorkoutRoutine.Yoga => 2.5m, _ => 3.5m };
                active = Add(new() { Id = Id($"{day}-workout"), ClientId = client, Kind = LogKind.Workout, OccurredAtUtc = At(day, workoutEnd), Routine = routine, Description = completed ? scheduled!.Description : "Evening neighbourhood walk", DurationMinutes = duration,
                    Calories = Math.Round(met * (persona == 0 ? 75 : 64) * duration / 60m), PlanItemId = completed ? scheduled!.Id : null, ScheduledDate = completed ? day : null,
                    Notes = completed && duration < scheduled!.DurationMinutes ? "Shortened the session because dinner plans changed." : index % 5 == 0 ? "Felt more comfortable than last week. Took a couple of breaks." : routine == WorkoutRoutine.StrengthTraining ? "Three rounds of squats, rows and presses with breaks between sets." : "Comfortable pace; finished without rushing.", IsFavorite = index >= Days - 7 && completed && day.DayOfWeek != DayOfWeek.Saturday });
            }
            var stepTotal = random.Next(3600, 7300) + (active && routine is WorkoutRoutine.Running or WorkoutRoutine.Walking ? duration * random.Next(70, 106) : 0) + (weekend ? 1000 : 0) - (trip ? 1000 : 0);
            if (index % 4 == 0) Add(new() { Id = Id($"{day}-steps-midday"), ClientId = client, Kind = LogKind.Measurement, OccurredAtUtc = At(day, 13 * 60 + 10), Description = "Midday step total", Steps = stepTotal * 37 / 100 });
            Add(new() { Id = Id($"{day}-steps"), ClientId = client, Kind = LogKind.Measurement, OccurredAtUtc = At(day, 21 * 60 + 30 + random.Next(0, 20)), Description = "Evening step total", Steps = stepTotal, Notes = "Cumulative steps for the day." });
            for (var meal = 0; meal < 4; meal++)
            {
                if (meal == 3 && random.NextDouble() < .25 || trip && meal == 1 || index % 17 == 8 && meal == 0) continue;
                var food = Meals[meal][random.Next(Meals[meal].Length)];
                var scale = (persona == 0 ? 1.05m : .95m) * new[] { .85m, 1m, 1m, 1.15m }[random.Next(4)];
                var protein = Math.Round(food.Protein * scale, 1); var carbs = Math.Round(food.Carbs * scale, 1); var fat = Math.Round(food.Fat * scale, 1);
                var unknown = meal == 2 && index % 23 == 11;
                var mealTime = meal switch { 0 => wakeMinute + random.Next(35, 66), 1 => 12 * 60 + random.Next(15, 90), 2 => 19 * 60 + random.Next(15, 105), _ => 16 * 60 + random.Next(0, 40) };
                // Do not place dinner in the middle of the workout.
                if (meal == 2 && active) mealTime = Math.Max(mealTime, (int)(TimeZoneInfo.ConvertTime(data.Logs.Last(x => x.Kind == LogKind.Workout).OccurredAtUtc, zone).TimeOfDay.TotalMinutes) + 30);
                var item = meal < 3 ? data.Plans.Single(p => p.Kind == PlanKind.Diet && p.StartDate <= day && p.EndDate >= day).Items.Single(x => x.Day == day.DayOfWeek && x.MealType == new[] { "Breakfast", "Lunch", "Dinner" }[meal]) : null;
                Add(new() { Id = Id($"{day}-meal-{meal}"), ClientId = client, Kind = LogKind.Meal, OccurredAtUtc = At(day, mealTime), Description = new[] { "Breakfast", "Lunch", "Dinner", "Snack" }[meal] + " · " + (unknown ? "Dinner out with friends" : food.Name),
                    Protein = unknown ? null : protein, Carbs = unknown ? null : carbs, Fat = unknown ? null : fat, Calories = unknown ? null : Math.Round(4 * protein + 4 * carbs + 9 * fat),
                    Notes = unknown ? "Shared dishes at a restaurant. Portions and nutrition were not measured." : meal == 1 && index % 3 == 0 ? "Packed lunch from last night's cooking; portion estimated." : scale < 1 ? "A smaller portion today. Nutrition amounts estimated." : "Home-style serving; nutrition amounts estimated.",
                    PlanItemId = item?.Id, ScheduledDate = item != null ? day : null, IsFavorite = !unknown && index == Days - 1 && meal < 3 });
            }
            // Drinks are separate events; missing records are not interpreted as no drinking.
            var cups = trip ? 3 : random.Next(5, 8);
            for (var cup = 0; cup < cups; cup++)
            {
                var minute = wakeMinute + 20 + cup * 115 + random.Next(0, 40);
                Add(new() { Id = Id($"{day}-water-{cup}"), ClientId = client, Kind = LogKind.Hydration, OccurredAtUtc = At(day, minute), Description = cup == 0 ? "Water with breakfast" : cup == cups - 1 ? "Afternoon water refill" : "Water bottle refill", WaterMl = new[] { 200, 250, 300, 350, 500 }[random.Next(5)], Notes = "" });
            }
        }
        data.Preferences = new() { Id = Id("preferences"), UserId = client, Focus = persona == 0 ? WellnessFocus.Movement : WellnessFocus.Balance, WeeklyWorkoutMinutes = persona == 0 ? 150 : 180, DailyWaterMl = persona == 0 ? 2200 : 2000, NightlySleepMinutes = 450, DailySteps = persona == 0 ? 8000 : 9000, SetupCompletedAtUtc = At(start.AddDays(2), 18 * 60) };
        var comments = new[] { "The shorter weekday sessions seem to fit your work schedule. Keep the weekend walk flexible.", "Several lunches are missing during your trip. Leave unknown portions blank rather than filling in guessed totals.", "Your notes mention late evenings. Compare how those days feel with the mornings after an earlier night.", "It is fine to shorten a session when plans change. Record what you completed so the history stays useful.", "There is more variety in your recent meals. Save a few regular lunches to make logging quicker." };
        for (var visit = 0; visit < 5; visit++)
        {
            var day = start.AddDays(12 + visit * 28); var at = At(day, 14 * 60 + persona * 60);
            var cancelled = visit == 2;
            data.Appointments.Add(new() { Id = Id($"visit-{visit}"), ClientId = client, TrainerId = trainer, StartsAtUtc = at, EndsAtUtc = at.AddMinutes(30), Status = cancelled ? AppointmentStatus.Cancelled : AppointmentStatus.Completed,
                Purpose = visit == 0 ? "Review work hours and agree on a manageable weekly routine" : "Monthly review of activity notes, meal logging and upcoming schedule", ResponseNote = cancelled ? "Client had a work commitment; follow-up arranged for the next week." : "Reviewed the recorded entries together and agreed on the next small adjustment." });
            var reviewAt = cancelled ? at.AddDays(7) : at;
            if (cancelled) data.Appointments.Add(new() { Id = Id("visit-rebooked"), ClientId = client, TrainerId = trainer, StartsAtUtc = reviewAt, EndsAtUtc = reviewAt.AddMinutes(30), Status = AppointmentStatus.Completed, Purpose = "Follow-up after the postponed check-in", ResponseNote = "Discussed how travel affected the usual routine." });
            data.Feedback.Add(new() { Id = Id($"feedback-{visit}"), ClientId = client, TrainerId = trainer, CreatedAtUtc = reviewAt.AddMinutes(40), Comment = comments[visit] });
        }
        for (var upcoming = 0; upcoming < 2; upcoming++)
        {
            var at = At(today.AddDays(3 + upcoming * 18), 14 * 60 + persona * 60);
            data.Appointments.Add(new() { Id = Id($"upcoming-{upcoming}"), ClientId = client, TrainerId = trainer, StartsAtUtc = at, EndsAtUtc = at.AddMinutes(30), Status = upcoming == 0 ? AppointmentStatus.Confirmed : AppointmentStatus.Requested, Purpose = upcoming == 0 ? "Review the past month and adjust next week's routine" : "Discuss meal preparation and schedule for the coming month", ResponseNote = upcoming == 0 ? "Time agreed for the next check-in." : "" });
        }
        return data;
    }
}
