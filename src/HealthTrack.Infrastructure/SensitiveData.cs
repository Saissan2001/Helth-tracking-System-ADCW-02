using System.Globalization;
using HealthTrack.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HealthTrack.Infrastructure;

// Encrypt values before they reach SQL. Indexable ownership, kind and timestamps remain metadata.
public sealed class SensitiveData(IDataProtectionProvider provider)
{
    public const string Prefix = "ht1:";
    public string Protect(string purpose, string value) => Prefix + provider.CreateProtector("HealthTrack.HealthData.v1", purpose).Protect(value);
    public string Unprotect(string purpose, string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Unencrypted legacy data found. Run the documented --migrate upgrade before starting HealthTrack.");
        return provider.CreateProtector("HealthTrack.HealthData.v1", purpose).Unprotect(value[Prefix.Length..]);
    }
    public static readonly IReadOnlyDictionary<Type, (string Table, string[] Columns)> Fields = new Dictionary<Type, (string, string[])>
    {
        [typeof(WellnessPreferences)] = ("WellnessPreferences", ["WeeklyWorkoutMinutes", "DailyWaterMl", "NightlySleepMinutes", "DailySteps"]),
        [typeof(HealthLog)] = ("Logs", ["WaterMl", "Description", "Notes", "WeightKg", "HeightCm", "HeartRate", "Systolic", "Diastolic", "Steps", "DurationMinutes", "Calories", "Protein", "Carbs", "Fat"]),
        [typeof(HealthPlan)] = ("Plans", ["Title", "Instructions"]),
        [typeof(PlanItem)] = ("PlanItems", ["Description", "MealType", "Sets", "Repetitions", "DurationMinutes", "Calories", "Protein", "Carbs", "Fat"]),
        [typeof(Appointment)] = ("Appointments", ["Purpose", "ResponseNote"]),
        [typeof(TrainerFeedback)] = ("Feedback", ["Comment"]),
        [typeof(MemberProfile)] = ("Profiles", ["Bio"]),
        [typeof(ContactMessage)] = ("ContactMessages", ["Name", "Email", "Subject", "Message"])
    };
    public void Configure(ModelBuilder model)
    {
        foreach (var (type, definition) in Fields)
            foreach (var column in definition.Columns)
            {
                var property = model.Entity(type).Property(column).Metadata;
                var purpose = definition.Table + "." + column;
                var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                ValueConverter converter = clr == typeof(decimal)
                    ? new ValueConverter<decimal, string>(v => Protect(purpose, v.ToString(CultureInfo.InvariantCulture)), v => decimal.Parse(Unprotect(purpose, v), CultureInfo.InvariantCulture))
                    : clr == typeof(int)
                    ? new ValueConverter<int, string>(v => Protect(purpose, v.ToString(CultureInfo.InvariantCulture)), v => int.Parse(Unprotect(purpose, v), CultureInfo.InvariantCulture))
                    : new ValueConverter<string, string>(v => Protect(purpose, v), v => Unprotect(purpose, v));
                property.SetValueConverter(converter);
                property.SetColumnType("nvarchar(max)");
                property.SetMaxLength(null); property.SetPrecision(null); property.SetScale(null);
            }
    }

    // Called only by the explicit migration command, before seed data or application reads.
    // Existing plaintext is upgraded in one transaction; all normal reads fail closed on plaintext.
    public async Task UpgradeExisting(HealthTrackDbContext db)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("DECLARE @r int; EXEC @r=sp_getapplock @Resource='HealthTrack.Write',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Database upgrade is busy.',1;");
        var connection = db.Database.GetDbConnection();
        foreach (var definition in Fields.Values)
            foreach (var column in definition.Columns)
            {
                // Identifiers come only from the fixed map above; values are always parameters.
                var remaining = true;
                while (remaining)
                {
                    await using var read = connection.CreateCommand(); read.Transaction = tx.GetDbTransaction();
                    read.CommandText = $"SELECT TOP (200) [Id],[{column}] FROM [{definition.Table}] WHERE [{column}] IS NOT NULL AND [{column}] NOT LIKE 'ht1:%'";
                    var batch = new List<(Guid Id, string Value)>();
                    await using (var reader = await read.ExecuteReaderAsync())
                        while (await reader.ReadAsync()) batch.Add((reader.GetGuid(0), reader.GetString(1)));
                    remaining = batch.Count == 200;
                    foreach (var row in batch)
                    {
                        await using var update = connection.CreateCommand(); update.Transaction = tx.GetDbTransaction();
                        update.CommandText = $"UPDATE [{definition.Table}] SET [{column}]=@value WHERE [Id]=@id";
                        var value = update.CreateParameter(); value.ParameterName = "@value"; value.Value = Protect(definition.Table + "." + column, row.Value); update.Parameters.Add(value);
                        var id = update.CreateParameter(); id.ParameterName = "@id"; id.Value = row.Id; update.Parameters.Add(id);
                        await update.ExecuteNonQueryAsync();
                    }
                }
            }
        await tx.CommitAsync(); db.ChangeTracker.Clear();
    }
}
