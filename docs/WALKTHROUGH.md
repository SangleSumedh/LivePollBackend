# LivePollBackend — Full Walkthrough

> A step-by-step guide to building a .NET 9 backend that replaces Firebase Firestore poll functionality with PostgreSQL + SignalR.

---

## Table of Contents

1. [Project Setup](#1-project-setup)
2. [Domain Models & Enums](#2-domain-models--enums)
3. [Database Context (EF Core)](#3-database-context-ef-core)
4. [Configuration & Program.cs](#4-configuration--programcs)
5. [DTOs (Data Transfer Objects)](#5-dtos-data-transfer-objects)
6. [Services Layer](#6-services-layer)
7. [Controllers (API Endpoints)](#7-controllers-api-endpoints)
8. [SignalR Hub (Real-Time)](#8-signalr-hub-real-time)
9. [Error Handling & Validation](#9-error-handling--validation)
10. [Testing the API](#10-testing-the-api)

---

## 1. Project Setup

### Prerequisites

- .NET 9 SDK
- PostgreSQL server running
- VS Code (or any editor)

### Create the project

```bash
dotnet new webapi -n LivePollBackend -o ./LivePollBackend --no-https
cd LivePollBackend
```

### Add NuGet packages

```bash
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL --version 9.0.3
dotnet add package Microsoft.EntityFrameworkCore.Tools
dotnet add package Swashbuckle.AspNetCore
```

> `Npgsql.EntityFrameworkCore.PostgreSQL` 9.x is compatible with .NET 9. Version 10+ requires .NET 10.

### Install EF CLI tool (for migrations)

```bash
dotnet tool install --global dotnet-ef
```

---

## 2. Domain Models & Enums

### PollStatus Enum

**`Models/Enums/PollStatus.cs`**

```csharp
namespace LivePollBackend.Models.Enums;

public enum PollStatus
{
    Draft,
    Live,
    Ended
}
```

Maps to Firebase's `status` field which can be `"draft"`, `"live"`, or `"ended"`.

### Poll Entity

**`Models/Entities/Poll.cs`**

| Property                | Type         | Firebase Equivalent     | Notes                              |
| ----------------------- | ------------ | ----------------------- | ---------------------------------- |
| `Id`                    | `string`     | `id`                    | 6-char short code (e.g., "A7K2M9") |
| `Title`                 | `string`     | `title`                 |                                    |
| `CreatedBy`             | `string`     | `createdBy`             | User ID                            |
| `CreatedByEmail`        | `string`     | `createdByEmail`        |                                    |
| `CreatedByName`         | `string`     | `createdByName`         | Default "Anonymous"                |
| `Status`                | `PollStatus` | `status`                | Draft → Live → Ended               |
| `ActiveQuestionIndex`   | `int`        | `activeQuestionIndex`   | Default -1                         |
| `CurrentQuestionActive` | `bool`       | `currentQuestionActive` | Is voting open?                    |
| `CreatedAt`             | `DateTime`   | `createdAt`             | Server UTC                         |
| `UpdatedAt`             | `DateTime`   | `updatedAt`             | Server UTC                         |

Navigation properties link to `Questions`, `VoteCounts`, and `Votes`.

### Question Entity

**`Models/Entities/Question.cs`**

A poll has many questions. Each question belongs to one poll via `PollId` foreign key.

| Property | Type     | Notes                       |
| -------- | -------- | --------------------------- |
| `Id`     | `int`    | Auto-increment              |
| `PollId` | `string` | FK → Poll                   |
| `Index`  | `int`    | Order within poll (0-based) |
| `Text`   | `string` | The question text           |

Navigation: `Options` collection.

### Option Entity

**`Models/Entities/Option.cs`**

Each question has multiple options. Cascades on question delete.

| Property     | Type     | Notes                           |
| ------------ | -------- | ------------------------------- |
| `Id`         | `int`    | Auto-increment                  |
| `QuestionId` | `int`    | FK → Question                   |
| `Index`      | `int`    | Order within question (0-based) |
| `Text`       | `string` | Option text                     |

### VoteCount Entity

**`Models/Entities/VoteCount.cs`**

Denormalized vote counts for fast reads — one row per `(questionIndex, optionIndex)` combination.

| Property        | Type     | Notes             |
| --------------- | -------- | ----------------- |
| `Id`            | `int`    | Auto-increment    |
| `PollId`        | `string` | FK → Poll         |
| `QuestionIndex` | `int`    | Which question    |
| `OptionIndex`   | `int`    | Which option      |
| `Count`         | `int`    | Tally (default 0) |

**Unique index**: `(PollId, QuestionIndex, OptionIndex)` — one count row per option.

### Vote Entity

**`Models/Entities/Vote.cs`**

Records individual votes to prevent duplicates.

| Property        | Type       | Notes                      |
| --------------- | ---------- | -------------------------- |
| `Id`            | `int`      | Auto-increment             |
| `PollId`        | `string`   | FK → Poll                  |
| `QuestionIndex` | `int`      | Which question             |
| `OptionIndex`   | `int`      | Which option was chosen    |
| `SessionId`     | `string`   | Anonymous voter identifier |
| `VotedAt`       | `DateTime` | Server UTC timestamp       |

**Unique index**: `(PollId, QuestionIndex, SessionId)` — one vote per session per question. This replaces Firebase's document-based dedup (`votes/${sessionId}_${questionIndex}`).

### Entity Relationship Diagram

```
Poll (1) ──┨ (N) Question (1) ──┨ (N) Option
  │
  ├── (1) ──┨ (N) VoteCount     # denormalized tallies
  └── (1) ──┨ (N) Vote          # individual votes
```

---

## 3. Database Context (EF Core)

**`Data/AppDbContext.cs`**

The `AppDbContext` registers all 5 entity sets and configures:

### Fluent API Configuration

**Poll table:**

- `Id` as PK with max 10 chars
- `Status` stored as string via `HasConversion<string>()` with default `PollStatus.Draft`
- `ActiveQuestionIndex` defaults to -1
- `CurrentQuestionActive` defaults to false
- `CreatedAt` / `UpdatedAt` use `NOW()` default (PostgreSQL)

**Question → Poll relationship:**

```csharp
entity.HasOne(e => e.Poll)
    .WithMany(p => p.Questions)
    .HasForeignKey(e => e.PollId)
    .OnDelete(DeleteBehavior.Cascade);  // deleting poll deletes its questions
```

**Option → Question relationship:**

- Cascade delete (deleting a question deletes its options)

**VoteCount:**

- Unique index on `(PollId, QuestionIndex, OptionIndex)` — ensures one row per option
- Cascade delete with Poll

**Vote:**

- Unique index on `(PollId, QuestionIndex, SessionId)` — prevents duplicate votes (equivalent to Firebase's `voteRef` document check in `runTransaction`)
- Cascade delete with Poll

---

## 4. Configuration & Program.cs

### Connection String

**`appsettings.json`**

```json
{
  "ConnectionStrings": {
    "LivePoll": "Host=localhost;Database=LivePoll;Username=postgres;Password=your_password"
  }
}
```

### Program.cs — Service Registration

```csharp
// PostgreSQL DbContext
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("LivePoll")));

// Controllers + Swagger + CORS
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options => { ... });
```

**Key middleware order:**

```
Swagger → CORS → MapControllers → Auto-Migrate
```

**Auto-migration on startup** (dev convenience):

```csharp
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}
```

This automatically creates/updates the database schema on every startup — no manual SQL needed.

### Create the database

```bash
dotnet ef migrations add InitialCreate
```

Then run the application — `Migrate()` will apply the migration automatically, or run manually:

```bash
dotnet ef database update
```

---

## 5. DTOs (Data Transfer Objects)

DTOs define the shape of data going in/out of the API, keeping entity models separate from the wire format.

### CreatePollRequest

```csharp
public class CreatePollRequest
{
    public string Title { get; set; }
    public List<QuestionDto> Questions { get; set; }
    public string CreatedBy { get; set; }
    public string CreatedByEmail { get; set; }
    public string CreatedByName { get; set; }
}

public class QuestionDto
{
    public string Text { get; set; }
    public List<string> Options { get; set; }  // list of option texts
}
```

### PollResponse

Returned by `GET` endpoints — includes nested questions, options, and vote counts.

```csharp
public class PollResponse
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string Status { get; set; }
    public int ActiveQuestionIndex { get; set; }
    public bool CurrentQuestionActive { get; set; }
    public List<QuestionResponse> Questions { get; set; }
    public Dictionary<string, int> VoteCounts { get; set; }  // e.g., {"0_0": 5, "0_1": 3}
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

### VoteRequest

```csharp
public class VoteRequest
{
    public int QuestionIndex { get; set; }
    public int OptionIndex { get; set; }
    public string SessionId { get; set; }
}
```

---

## 6. Services Layer

Services contain all business logic. The controller delegates to services.

### IPollService / PollService

| Method                             | Purpose                            | Firebase Equivalent |
| ---------------------------------- | ---------------------------------- | ------------------- |
| `GetPollsAsync(userId)`            | Fetch all polls by user            | `fetchPolls`        |
| `GetPollByIdAsync(pollId)`         | Single poll with full detail       | `fetchPollById`     |
| `CreatePollAsync(request)`         | Create with auto-generated ID      | `createPoll`        |
| `UpdatePollAsync(pollId, request)` | Update title/questions             | `savePoll`          |
| `DeletePollAsync(pollId)`          | Delete poll + cascade              | `deletePoll`        |
| `RestartPollAsync(pollId)`         | Clear votes, reset to draft        | `restartPoll`       |
| `StartVotingAsync(pollId, qIdx)`   | Set status=live, activate question | `startVoting`       |
| `StopVotingAsync(pollId)`          | Deactivate current question        | `stopVoting`        |
| `NextQuestionAsync(pollId)`        | Advance question index             | `nextQuestion`      |
| `PrevQuestionAsync(pollId)`        | Go back question index             | `prevQuestion`      |
| `EndPollAsync(pollId)`             | Set status=ended                   | `endPoll`           |

### IVoteService / VoteService

| Method                                          | Purpose                       | Firebase Equivalent |
| ----------------------------------------------- | ----------------------------- | ------------------- |
| `CheckVoteStatusAsync(pollId, qIdx, sessionId)` | Has this session voted?       | `checkVoteStatus`   |
| `CastVoteAsync(pollId, request)`                | Transactional vote with dedup | `voteForOption`     |

### Poll ID Generator

**`Services/PollIdGenerator.cs`**

```csharp
public static class PollIdGenerator
{
    public static string Generate() =>
        Guid.NewGuid().ToString("N")[..6].ToUpper();
}
```

Firebase used `Math.random().toString(36).substring(2, 8).toUpperCase()`. The GUID approach is simpler and equally collision-resistant in practice. Add a retry loop for safety.

---

## 7. Controllers (API Endpoints)

**`Controllers/PollsController.cs`**

### Endpoint Map

| Method   | Route                                                              | Handler           |
| -------- | ------------------------------------------------------------------ | ----------------- |
| `GET`    | `/api/polls?userId={userId}`                                       | `GetPollsByUser`  |
| `GET`    | `/api/polls/{pollId}`                                              | `GetPollById`     |
| `POST`   | `/api/polls`                                                       | `CreatePoll`      |
| `PUT`    | `/api/polls/{pollId}`                                              | `UpdatePoll`      |
| `DELETE` | `/api/polls/{pollId}`                                              | `DeletePoll`      |
| `POST`   | `/api/polls/{pollId}/restart`                                      | `RestartPoll`     |
| `POST`   | `/api/polls/{pollId}/start`                                        | `StartVoting`     |
| `POST`   | `/api/polls/{pollId}/stop`                                         | `StopVoting`      |
| `POST`   | `/api/polls/{pollId}/next`                                         | `NextQuestion`    |
| `POST`   | `/api/polls/{pollId}/prev`                                         | `PrevQuestion`    |
| `POST`   | `/api/polls/{pollId}/end`                                          | `EndPoll`         |
| `GET`    | `/api/polls/{pollId}/votes/status?questionIndex={n}&sessionId={s}` | `CheckVoteStatus` |
| `POST`   | `/api/polls/{pollId}/votes`                                        | `CastVote`        |

### Vote Transaction Logic (critical path)

This is the most important endpoint — it replaces Firebase's `runTransaction`:

```csharp
[HttpPost("{pollId}/votes")]
public async Task<IActionResult> CastVote(string pollId, VoteRequest request)
{
    try
    {
        var result = await _voteService.CastVoteAsync(pollId, request);
        return Ok(result);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("already voted"))
    {
        return Conflict(new { error = "You have already voted on this question" });
    }
}
```

Inside `VoteService.CastVoteAsync`:

1. **Begin transaction** → `DbContext.Database.BeginTransactionAsync()`
2. **Check duplicate** → Query `Votes` table for `(PollId, QuestionIndex, SessionId)`. If exists, reject.
3. **Insert vote** → Add new `Vote` row
4. **Upsert count** → Find or create `VoteCount` row for `(PollId, QuestionIndex, OptionIndex)`, increment `Count`
5. **Commit transaction**
6. **Broadcast via SignalR** → Notify connected clients of the updated counts

---

## 8. SignalR Hub (Real-Time)

Replaces Firebase's `onSnapshot` real-time listener.

### Hub Setup

**`Hubs/PollHub.cs`**

```csharp
[Authorize]  // Optional: if you want auth on the hub
public class PollHub : Hub
{
    // Clients don't send messages — they just listen.
    // All actions go through REST endpoints.

    public async Task JoinPollGroup(string pollId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"poll_{pollId}");
    }

    public async Task LeavePollGroup(string pollId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"poll_{pollId}");
    }
}
```

### Register in Program.cs

```csharp
builder.Services.AddSignalR();

// ...

app.MapHub<PollHub>("/hubs/poll");
```

### Broadcasting from Services

Inject `IHubContext<PollHub>` into your services:

```csharp
public class VoteService
{
    private readonly IHubContext<PollHub> _hubContext;

    public async Task CastVoteAsync(string pollId, VoteRequest request)
    {
        // ... transactional vote logic ...

        // Broadcast updated counts to all listeners
        var counts = await GetVoteCountsAsync(pollId);
        await _hubContext.Clients.Group($"poll_{pollId}")
            .SendAsync("VoteCountsUpdated", new { pollId, voteCounts = counts });
    }
}
```

### Client-Side JavaScript (for reference)

```javascript
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/poll")
  .build();

connection.on("PollUpdated", (data) => {
  /* update poll state */
});
connection.on("VoteCountsUpdated", (data) => {
  /* update tallies */
});
connection.on("PollEnded", (data) => {
  /* show ended state */
});

await connection.start();
await connection.invoke("JoinPollGroup", pollId);
```

This maps directly to your Firebase `onSnapshot` listener in `subscribeToPoll`.

### Event Map

| SignalR Event       | Payload                  | Triggered By                                                                 |
| ------------------- | ------------------------ | ---------------------------------------------------------------------------- |
| `PollUpdated`       | `PollResponse`           | Create, Update, StartVoting, StopVoting, NextQuestion, PrevQuestion, Restart |
| `VoteCountsUpdated` | `{ pollId, voteCounts }` | CastVote                                                                     |
| `PollEnded`         | `{ pollId }`             | EndPoll                                                                      |

---

## 9. Error Handling & Validation

### Global Error Handling Middleware

```csharp
// Middleware to catch unhandled exceptions
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (NotFoundException ex)
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "An internal error occurred" });
    }
});
```

### Custom Exception Classes

```csharp
public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }
}

public class DuplicateVoteException : Exception
{
    public DuplicateVoteException()
        : base("You have already voted on this question") { }
}
```

### Input Validation (FluentValidation)

```bash
dotnet add package FluentValidation.AspNetCore
```

```csharp
public class CreatePollRequestValidator : AbstractValidator<CreatePollRequest>
{
    public CreatePollRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Questions).NotEmpty();
        RuleForEach(x => x.Questions).ChildRules(q =>
        {
            q.RuleFor(x => x.Text).NotEmpty().MaximumLength(1000);
            q.RuleFor(x => x.Options).NotEmpty()
                .Must(opts => opts.Count >= 2)
                .WithMessage("Each question must have at least 2 options");
        });
    }
}
```

---

## 10. Testing the API

### Swagger UI

Run the project and navigate to:

```
http://localhost:{port}/swagger
```

All endpoints are documented and interactive.

### Sample Flow (Postman / curl)

**1. Create a poll**

```bash
curl -X POST http://localhost:5000/api/polls \
  -H "Content-Type: application/json" \
  -d '{
    "title": "Favorite Programming Language?",
    "createdBy": "user123",
    "createdByEmail": "user@example.com",
    "createdByName": "Alice",
    "questions": [
      {
        "text": "Which language do you prefer?",
        "options": ["C#", "JavaScript", "Python", "Rust"]
      }
    ]
  }'
```

**2. Start voting**

```bash
curl -X POST http://localhost:5000/api/polls/A7K2M9/start \
  -H "Content-Type: application/json" \
  -d '{ "questionIndex": 0 }'
```

**3. Cast a vote**

```bash
curl -X POST http://localhost:5000/api/polls/A7K2M9/votes \
  -H "Content-Type: application/json" \
  -d '{
    "questionIndex": 0,
    "optionIndex": 2,
    "sessionId": "session_abc123"
  }'
```

**4. Check results**

```bash
curl http://localhost:5000/api/polls/A7K2M9
```

**5. End poll**

```bash
curl -X POST http://localhost:5000/api/polls/A7K2M9/end
```

### Verify Constraints

- ✅ **Duplicate vote** → `POST /api/polls/{id}/votes` with same `sessionId` returns `409 Conflict`
- ✅ **Poll not found** → returns `404 Not Found`
- ✅ **Delete poll** → cascades to questions, options, votes, counts

---

## Appendix: Firebase → .NET Mapping

| Firebase Concept                     | .NET Equivalent                                      |
| ------------------------------------ | ---------------------------------------------------- |
| `collection("polls")`                | `DbContext.Polls`                                    |
| `doc(db, "polls", pollId)`           | `DbContext.Polls.FindAsync(pollId)`                  |
| `onSnapshot(doc(...), callback)`     | SignalR `PollUpdated` event                          |
| `runTransaction(db, ...)`            | EF Core `BeginTransactionAsync()`                    |
| `writeBatch(db)`                     | EF Core cascade deletes + transaction                |
| `serverTimestamp()`                  | `DateTime.UtcNow` or `NOW()` in SQL                  |
| `increment(1)`                       | EF Core: read → increment → save                     |
| `where("createdBy", "==", userId)`   | `db.Polls.Where(p => p.CreatedBy == userId)`         |
| `doc.id` (auto-ID)                   | `PollIdGenerator.Generate()` (6-char code)           |
| `{sessionId}_{questionIndex}` doc ID | Unique index on `(PollId, QuestionIndex, SessionId)` |
| `voteCounts.${qIdx}_${optIdx}`       | `VoteCounts` table rows                              |
