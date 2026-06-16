# LivePollBackend - .NET Implementation Plan

## Overview

Replicate the Firebase Firestore poll functionality in a .NET 9 ASP.NET Core Web API with SignalR for real-time updates and **PostgreSQL** for persistence.

Database name: **LivePoll**

---

## 1. Data Model (Firebase → SQL)

### Polls Table

| Firebase Field          | SQL Column              | Type          | Notes                                      |
| ----------------------- | ----------------------- | ------------- | ------------------------------------------ |
| `id`                    | `Id`                    | `string (PK)` | Auto-generated short code (e.g., "A7K2M9") |
| `title`                 | `Title`                 | `string`      |                                            |
| `createdBy`             | `CreatedBy`             | `string`      | User ID                                    |
| `createdByEmail`        | `CreatedByEmail`        | `string`      |                                            |
| `createdByName`         | `CreatedByName`         | `string`      |                                            |
| `status`                | `Status`                | `string`      | "draft" \| "live" \| "ended"               |
| `activeQuestionIndex`   | `ActiveQuestionIndex`   | `int`         | Default -1                                 |
| `currentQuestionActive` | `CurrentQuestionActive` | `bool`        |                                            |
| `createdAt`             | `CreatedAt`             | `DateTime`    | Server UTC                                 |
| `updatedAt`             | `UpdatedAt`             | `DateTime`    | Server UTC                                 |

### Questions Table (separate from Polls)

| Column   | Type          | Notes               |
| -------- | ------------- | ------------------- |
| `Id`     | `int (PK)`    | Auto-increment      |
| `PollId` | `string (FK)` | References Polls.Id |
| `Index`  | `int`         | Order within poll   |
| `Text`   | `string`      | Question text       |

### Options Table

| Column       | Type       | Notes                   |
| ------------ | ---------- | ----------------------- |
| `Id`         | `int (PK)` | Auto-increment          |
| `QuestionId` | `int (FK)` | References Questions.Id |
| `Index`      | `int`      | Order within question   |
| `Text`       | `string`   | Option text             |

### VoteCounts Table (denormalized for fast reads)

| Column          | Type          | Notes          |
| --------------- | ------------- | -------------- |
| `Id`            | `int (PK)`    | Auto-increment |
| `PollId`        | `string (FK)` |                |
| `QuestionIndex` | `int`         |                |
| `OptionIndex`   | `int`         |                |
| `Count`         | `int`         | Default 0      |

### Votes Table

| Column          | Type          | Notes                      |
| --------------- | ------------- | -------------------------- |
| `Id`            | `int (PK)`    | Auto-increment             |
| `PollId`        | `string (FK)` |                            |
| `QuestionIndex` | `int`         |                            |
| `OptionIndex`   | `int`         |                            |
| `SessionId`     | `string`      | Anonymous voter identifier |
| `VotedAt`       | `DateTime`    | Server UTC                 |

**Unique constraint**: `(PollId, QuestionIndex, SessionId)` — prevents duplicate votes.

---

## 2. Project Structure

```
LivePollBackend/
├── Program.cs                          # App entry, DI, middleware
├── appsettings.json
├── appsettings.Development.json
│
├── Models/
│   ├── Entities/
│   │   ├── Poll.cs
│   │   ├── Question.cs
│   │   ├── Option.cs
│   │   ├── VoteCount.cs
│   │   └── Vote.cs
│   ├── DTOs/
│   │   ├── CreatePollRequest.cs
│   │   ├── UpdatePollRequest.cs
│   │   ├── PollResponse.cs
│   │   ├── VoteRequest.cs
│   │   └── ...
│   └── Enums/
│       └── PollStatus.cs
│
├── Data/
│   ├── AppDbContext.cs
│   └── Migrations/
│
├── Services/
│   ├── IPollService.cs
│   ├── PollService.cs
│   ├── IVoteService.cs
│   ├── VoteService.cs
│   └── PollIdGenerator.cs
│
├── Hubs/
│   └── PollHub.cs                      # SignalR hub for real-time
│
├── Controllers/
│   └── PollsController.cs
│
└── docs/
    └── IMPLEMENTATION_PLAN.md
```

---

## 3. API Endpoints

| Method   | Route                                                              | Description                                   | Equivalent Firebase Op |
| -------- | ------------------------------------------------------------------ | --------------------------------------------- | ---------------------- |
| `GET`    | `/api/polls?userId={userId}`                                       | Get all polls by user                         | `fetchPolls`           |
| `GET`    | `/api/polls/{pollId}`                                              | Get single poll with questions/options/counts | `fetchPollById`        |
| `POST`   | `/api/polls`                                                       | Create a new poll                             | `createPoll`           |
| `PUT`    | `/api/polls/{pollId}`                                              | Update poll title/questions                   | `savePoll`             |
| `DELETE` | `/api/polls/{pollId}`                                              | Delete poll + votes                           | `deletePoll`           |
| `POST`   | `/api/polls/{pollId}/restart`                                      | Reset poll to draft, clear votes              | `restartPoll`          |
| `POST`   | `/api/polls/{pollId}/start`                                        | Start voting on a question                    | `startVoting`          |
| `POST`   | `/api/polls/{pollId}/stop`                                         | Stop voting on current question               | `stopVoting`           |
| `POST`   | `/api/polls/{pollId}/next`                                         | Move to next question                         | `nextQuestion`         |
| `POST`   | `/api/polls/{pollId}/prev`                                         | Move to previous question                     | `prevQuestion`         |
| `POST`   | `/api/polls/{pollId}/end`                                          | End poll                                      | `endPoll`              |
| `GET`    | `/api/polls/{pollId}/votes/status?questionIndex={n}&sessionId={s}` | Check if voted                                | `checkVoteStatus`      |
| `POST`   | `/api/polls/{pollId}/votes`                                        | Cast a vote (transactional)                   | `voteForOption`        |

---

## 4. Real-Time (SignalR) — Replaces Firestore `onSnapshot`

**Hub**: `/hubs/poll`

### Server → Client Events

| Event               | Payload                  | When                                                       |
| ------------------- | ------------------------ | ---------------------------------------------------------- |
| `PollUpdated`       | `PollResponse`           | Any poll field changes (status, activeQuestionIndex, etc.) |
| `VoteCountsUpdated` | `{ pollId, voteCounts }` | A vote is cast                                             |
| `PollEnded`         | `{ pollId }`             | Presenter ends poll                                        |

### Client → Server (not needed — clients call REST endpoints)

---

## 5. Key Implementation Details

### 5.1 Poll ID Generation

- Same as Firebase: 6-char uppercase alphanumeric
- Retry on collision (unlikely but safe)

### 5.2 Vote Transaction (replaces `runTransaction`)

- Use a SQL transaction / EF Core `DbContext.Database.BeginTransactionAsync`
- Check if `(PollId, QuestionIndex, SessionId)` already exists → reject with 409 Conflict
- Insert vote row
- Upsert `VoteCounts` row or recompute from `Votes` table
- Broadcast via SignalR

### 5.3 Vote Counts Strategy

**Option A (recommended)**: Store `VoteCounts` as a separate table, update during vote transaction. On poll load, aggregate counts or read from table.
**Option B**: Compute on-the-fly with `GROUP BY` queries — simpler but slower for large polls.

Recommend **Option A** for performance, and also include a `GET /api/polls/{pollId}/results` endpoint that returns counts.

### 5.4 Poll Deletion (replaces `writeBatch`)

- Use EF Core `Include` to load all related entities
- Delete in cascade order: Votes → VoteCounts → Options → Questions → Poll
- Or configure cascade deletes in EF Core

### 5.5 Poll Restart (replaces `writeBatch`)

- Delete all votes for the poll
- Reset VoteCounts to zero
- Set status="draft", activeQuestionIndex=-1, currentQuestionActive=false
- All within a single transaction

---

## 6. NuGet Dependencies

```xml
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="9.0.x" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Tools" Version="9.0.x" />
<PackageReference Include="Microsoft.AspNetCore.SignalR.Common" Version="9.0.x" />
<PackageReference Include="Swashbuckle.AspNetCore" Version="6.x" />
```

---

## 7. Implementation Order

1. **Phase 1 — Foundation**
   - Set up EF Core with PostgreSQL
   - Create entity models and `AppDbContext`
   - Create initial migration

2. **Phase 2 — Core API**
   - Implement `PollService` & `VoteService`
   - Implement `PollsController` with all endpoints
   - Test with Swagger/Postman

3. **Phase 3 — Real-Time**
   - Create `PollHub` (SignalR)
   - Wire SignalR broadcasts into services
   - Test with a simple client

4. **Phase 4 — Polish**
   - Error handling middleware
   - Input validation (FluentValidation)
   - Rate limiting on vote endpoint
   - CORS configuration for frontend
