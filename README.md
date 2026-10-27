# HealthTrack

A working ASP.NET Core MVC personal health and fitness application for Clients, Trainers and Administrators. Built on **.NET 10 / EF Core 10 / SQL Server LocalDB**, with Identity accounts, Razor views, local Bootstrap and Chart.js assets.

## Start here

Open **`HealthTrack.sln`** in Visual Studio 2026, or use PowerShell at this directory:

```powershell
.\scripts\setup.ps1
dotnet dev-certs https --trust
.\scripts\start.ps1
```

Open **https://localhost:7000**. Setup writes generated demo credentials to **`.local/demo-credentials.txt`**, which is excluded from source control. It does not publish a built-in password. Four fictional accounts are available: `admin@healthtrack.demo`, `trainer@healthtrack.demo`, `client@healthtrack.demo`, and `client2@healthtrack.demo`.

Use the **https** launch profile when starting from Visual Studio. The web project retains its existing name, **HealthTrack application**, so the original project remains usable. Both solution formats include the library and test projects.

## What the app does

| Role | Working workflows |
|---|---|
| Client | Register/sign in; edit profile/preferences/password; record, correct and delete measurements, workouts, meals, water and sleep; personalized Today overview, daily diary, goals, saved meal/workout reuse and seven-day wellness trends; see published schedules and feedback; request/reschedule/cancel appointments; filtered progress charts and CSV; routine categories; notification inbox |
| Trainer | View current assigned clients; inspect progress; build draft workout/diet plans with weekly items; publish/archive plans; share feedback; confirm/reschedule/decline/cancel/complete own consultations; performance/routine/engagement reports and CSV |
| Administrator | Create/disable/reactivate trainers and clients; assign/revoke/transfer trainers; maintain professional directory; coordinate professional appointments; review privacy-suppressed aggregate performance, routine popularity, engagement, CSV reports and sanitized audit history |

All health writes are scoped to the client. Trainer reads require a current assignment. Administrators receive aggregate health usage, not unrestricted measurement access. SQL transaction locks protect appointment overlaps; rowversions detect stale changes. A hosted worker persists idempotent in-app reminders every 30 seconds.

## Repository

```text
HealthTrack.sln
HealthTrack application/       MVC web app, controllers, views, local assets
src/HealthTrack.Domain/        Entities, statuses, transitions, health math
src/HealthTrack.Application/   Use-case contracts, validated inputs, read models
src/HealthTrack.Infrastructure/ EF Core, Identity, services, migrations, seed
tests/HealthTrack.Tests/       Domain, SQL integration and HTTP tests
scripts/                      Repeatable setup and startup
docs/                         Implementation guide, manuals, evidence, diagrams
```

The original starter Identity migration files under the web project's `Data` folder are retained but excluded from compilation. **The active context and migrations are in Infrastructure.** This prevents accidentally applying the starter schema to the new app.

## Verify

```powershell
dotnet test HealthTrack.sln -c Release --logger "trx;LogFileName=healthtrack.trx" --results-directory TestResults
dotnet list HealthTrack.sln package --vulnerable --include-transitive
```

Integration tests use a newly named `HealthTrack_Test_<guid>` LocalDB database, apply the real migration, and delete only that test database after the run. They do not modify the demonstration database.

See [installation](docs/INSTALLATION_GUIDE.md), [user manual](docs/USER_MANUAL.md), [demo route](docs/DEMO_SCRIPT.md), [implementation status](docs/IMPLEMENTATION_STATUS.md), and [test results](docs/TEST_RESULTS.md).

This is a coursework fitness system, not a clinical service. Local email reset links use an operator-only development sink; no external email/SMS booking integration is claimed. Read the [security limitations](docs/SECURITY_AND_PRIVACY.md) before using real data. AI assistance must be disclosed, and students must write their own reflections.

## Original coursework requirements rechecked

The original CS6004ES Coursework 2 DOCX has been audited against the implementation. See [requirement-by-requirement audit](docs/REQUIREMENTS_AUDIT.md). The audit added trainer rescheduling, explicit routine categories, performance/engagement reports and downloads, and authenticated field encryption with a verified existing-data upgrade. Keep `.local/data-protection-keys` with protected database backups; see the recovery instructions before moving or cleaning the installation.

The individual reflection and viva remain the student's own work. Browser visual verification is explicitly distinguished from successful HTTP and SQL integration tests.


Visitors can send enquiries through Contact us. Administrators review encrypted submissions in **Contact inbox**, follow up through their email application and mark them handled. See [contact-form operation](docs/CONTACT_FORM.md).

Latest additions: linked workout/meal completion, weekly dashboard totals, registration/workload charts, paged privacy-aware reports and batched reminders. See [feature completion](docs/FEATURE_COMPLETION.md) for the 56-test/53-live-check evidence and remaining external acceptance tasks.


## Researched wellness improvements

See [the eight-app research and implementation report](docs/HEALTH_APP_RESEARCH.md) for sources, flow comparisons, delivered features and limits. Latest verification: 70 automated tests and 85 live HTTPS checks pass; the fifth database migration is applied. Browser visual acceptance remains blocked by the embedded browser's localhost certificate rejection. Open `/Dashboard`, `/Wellness`, `/Wellness/Settings`, `/Wellness/Library` and `/Wellness/Trends` as a client.


Both demo clients now have **five months of varied synthetic data (3,802 entries)**, including water/sleep, meals, workouts, measurements, goals, plan history and consultations. [Dataset details and safe seed command](docs/REALISTIC_DEMO_DATA.md). Existing demo credentials are unchanged.
# Helth-tracking-System-ADCW-02
