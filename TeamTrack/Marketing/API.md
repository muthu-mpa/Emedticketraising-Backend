# Marketing Lead Desk: API guide

For backend developers. It explains what the module is for, what each endpoint does, and the rules behind it.
Code: `TeamTrack/Marketing/` (controller, service, DTOs, models, schema). Sample requests: `Marketing.http`.

## 1. What this feature is

The Marketing team works a shared pool of **leads** (people who showed interest through ads, webinars, the newsletter and so on).

1. Unassigned leads wait in a shared **queue**.
2. A marketer **picks up** a lead. It moves to their personal **work bench**, and nobody else can take it.
3. The marketer works the lead: logs emails, replies, calls and notes, with file attachments such as signed PDFs. They also move the lead through **stages** and adjust its **score**.
4. The lead ends **closed** with a reason, or is **returned** to the queue.

A **dashboard** shows team-wide numbers, plus how many leads are on the caller's own bench.

## 2. Conventions

| Topic | Rule |
|---|---|
| Base URL | `/api/marketing` |
| Auth | JWT bearer token. Role must be `Marketing`. A wrong or missing role gives a bare 403 or 401 with an empty body, not the envelope. |
| Feature flag | `Features:Marketing=true` (on in Development). If off, every route returns 404. If the schema failed to initialise, 503. |
| Response shape | `ApiResponse<T>`: `{ success, message, data }` with the **real HTTP status** (400, 403, 404, 409). |
| Exception | `GET /attachments/{id}` returns the **raw file**, not the envelope. |
| JSON | camelCase. Times are UTC ISO-8601. |
| Why no `[ApiController]` | It would turn binding errors into `ProblemDetails`. The frontend client expects the envelope everywhere, so a bad body is handled by hand (400 envelope). |
| User identity | `CurrentUser.From(User)` (id, name). Lead owner is `users.id` (int). |

**Visibility rule (used by every endpoint):** a user can see a lead if they **own it**, or it is **unowned and not closed**.
Anything else gives 403. Changing a lead (return, stage, score, close, history) requires that the caller **owns** it, otherwise 403 "This lead is not on your work bench."

## 3. Lookups: allowed values

`GET /lookups` returns all of these. They are the single source of truth for dropdowns and validation.

| Field | Values |
|---|---|
| stages | New, Contacted, Replied, Interested, Closed |
| openStages | New, Contacted, Replied, Interested |
| channels | Paid search, Social ads, Email, Webinar, Content download, Event, Organic, Referral |
| campaigns | Spring product launch, Q4 webinar series, Buyer’s guide ebook, Pricing page retargeting, SaaS Connect trade show, Newsletter |
| interests | Starter plan, Pro plan, Enterprise, Integrations, Not sure yet |
| closeReasons | Became a customer, Not interested, No response, Wrong contact, Duplicate |
| historyKinds | Captured, Picked up, Status, Score, Email sent, Reply received, Call, Note, Returned, Closed |
| loggableKinds | Email sent, Reply received, Call, Note (the ones a user may add by hand) |
| temperatures | Hot, Warm, Cold |

**Temperature** is derived from the score and is never stored: **Hot** is 70 or more, **Warm** is 40 to 69, **Cold** is below 40.

## 4. Endpoints

### 4.1 `GET /lookups`
Returns the lists above. Use it to fill filters and forms. 200 always.

### 4.2 `GET /dashboard`
Team-wide numbers, except `onMyBench`, which is personal.

| Field | Meaning |
|---|---|
| leadsTracked | All leads |
| emailsSent | Count of "Email sent" history entries |
| waitingInQueue | Unowned and open leads |
| onMyBench | Open leads owned by the caller |
| replyRate | Leads with a reply ÷ leads with an email sent. A **0 to 1 fraction**, not a percentage. 0 when no emails. |
| byStage, byChannel, byTemperature, byCampaign | Counts per value. `byCampaign` has `leads` and `emails`. |
| recentActivity | Last 10 activity lines (`text`, `at`) |

### 4.3 `GET /leads/queue`
The shared pool: **unowned and not closed** leads only.
Query parameters (all optional):

| Param | Notes |
|---|---|
| search | Case-insensitive match on name, company, email or title |
| channel, campaign, stage, temperature | Must be one of the lookup values, otherwise 400 |
| sort | `score` (default), `name`, `created`, `lastContact`, `emails`. Ties are broken by name. |
| dir | `desc` (default) or `asc` |

Returns `LeadSummary[]`.

### 4.4 `GET /leads/bench?status=open|closed|all`
The caller's own leads. Default is `open`. Newest pick-up first. Any other `status` gives 400.

### 4.5 `GET /leads/{id}`
`LeadDetail` is a `LeadSummary` plus `history[]`, newest first. Each entry has `kind`, `occurredAt`, `subject`, `body`, `text`, `createdByName` and `attachments[]`.
404 if the lead is missing, 403 if the caller may not see it.

### 4.6 `POST /leads/{id}/pick-up`
Claims an unowned, open lead.
- Writes a "Picked up" history entry and an activity line.
- **Race-safe:** it uses one conditional `UPDATE ... WHERE owner IS NULL`. If two people click at once, one wins and the other gets **409** "already picked up by someone else".
- 409 if the lead is already on the caller's bench, or is taken or closed.

### 4.7 `POST /leads/{id}/return`
Puts a lead the caller owns back in the queue (clears owner and pickedUpAt). Writes a "Returned" entry.
**409** if the lead is closed ("Reopen it first").

### 4.8 `PUT /leads/{id}/stage`  body `{ "stage": "Interested" }`
Changes the stage of an owned lead and writes a "Status" entry, for example `Contacted → Replied`.
- 400 if the stage is invalid.
- **400 if the stage is `Closed`.** Closing must go through `/close` because it needs a reason.
- Moving a closed lead to an open stage **reopens** it and clears `closeReason`.
- Setting the same stage again does nothing.

### 4.9 `PUT /leads/{id}/score`  body `{ "score": 85 }`
Sets the score. It is **clamped to 0..100**, not rejected. The temperature follows from it. A change writes a "Score" entry.

### 4.10 `POST /leads/{id}/close`  body `{ "reason": "Not interested" }`
Closes an owned lead with a reason from `closeReasons`.
400 for a missing or invalid reason, 409 if the lead is already closed. The lead stays on the owner's bench (visible under `status=closed`).

### 4.11 `POST /leads/{id}/history`  (`multipart/form-data`)
Logs something the marketer did, with optional files. The caller must own the lead.

| Form field | Rules |
|---|---|
| kind | Required. `Email sent`, `Reply received`, `Call` or `Note`. |
| occurredAt | Optional ISO date. Defaults to now. Parsed as UTC. **Up to 5 minutes in the future is tolerated**, anything later gives 400. |
| subject | Required for `Email sent`. Single line, max 300 characters. |
| body | Optional, max 20,000 characters |
| text | Max 4,000 characters. For every kind except `Email sent`, either `text` or at least one file is required. |
| files | Up to **5** files, each **at most 10 MB**, not empty |
| filesMeta | Optional JSON array, same order and count as `files`: `[{ "fileName": "x.pdf", "signedBy": "Priya Raman", "signedAt": "iso" }]`. `signedBy` is max 200 characters. |

Allowed file types: pdf, png, jpg, jpeg, gif, webp, doc, docx, xls, xlsx, ppt, pptx, txt, csv, zip.

**Automatic stage changes** (each also writes a "Status" entry):
- `Email sent` on a `New` lead moves it to `Contacted`.
- `Reply received` on a `New` or `Contacted` lead moves it to `Replied`.

Returns the updated `LeadDetail` ("Saved."). Validation failures are 400. If saving to the database fails, the files already written are deleted.

### 4.12 `GET /attachments/{id}`
Streams the file. It is **not** the envelope.
- Headers: `Content-Disposition: attachment; filename=...` and `X-Content-Type-Options: nosniff`.
- 404 if unknown or the file is missing from disk, 403 if the caller cannot see the owning lead.

## 5. Behaviour worth knowing

- **Audit trail:** every state change (pick up, return, stage, score, close, auto-stage) adds a history entry. Every action also adds a line to the team activity feed (dashboard `recentActivity`).
- **Counters** `emailsSent` and `lastContactAt` on a lead are computed from history (kinds Email sent, Reply received, Call), not stored.
- **Upload safety:**
  - Files are stored under `uploads/marketing/<guid>.<ext>`, **outside `wwwroot`**, so they are never served as static files.
  - The original name is kept only for display, never used on disk.
  - The served content type comes from our extension map, not from the client.
- **Pagination:** none. The queue and bench return full lists.

## 6. Data model (Postgres, tables created at startup)

| Table | Purpose |
|---|---|
| `mk_leads` | The lead: contact info, channel, campaign, interest, score, stage, closeReason, consent, owner (`users.id`, nullable), pickedUpAt, createdAt, updatedAt |
| `mk_lead_history` | Timeline entries (kind, occurredAt, subject, body, text, createdBy) |
| `mk_lead_attachments` | Files on a history entry (name, contentType, size, storagePath, signedBy, signedAt) |
| `mk_activity` | Team activity feed |

The schema comes from idempotent `CREATE TABLE IF NOT EXISTS` SQL (`MarketingSchemaInitializer`), **not** EF migrations. It uses its own `MarketingDbContext`, in the same pattern as the Workspace module. `AppDbContext` is untouched.

## 7. Status code cheat sheet

| Code | When |
|---|---|
| 200 | Success. Envelope, or raw file for attachments. |
| 400 | Invalid value, missing field, bad date, bad file, bad body, `Closed` via the stage endpoint |
| 401 or 403 | Not logged in or wrong role (empty body). Inside the module, 403 means the lead is not yours or not visible to you. |
| 404 | Lead or attachment not found, or the feature flag is off |
| 409 | Already picked up, already on bench, return or close on a closed lead |
| 503 | Marketing schema failed to initialise at startup |

## 8. Local development

- The demo user `marketing.demo@example.com` (role `Marketing`) and one demo lead are seeded **in Development only**. The password comes from user secret `Seed:MarketingDemoPassword`.
- The demo email skips the login OTP **only in Development**. The guard is in `Services/OtpExemption.cs`, with tests.
- Setup, user secrets and safe `dotnet ef` usage: see `Marketing/README.md`.
- Tests: `TeamTrack.Tests/MarketingServiceTests.cs`. They use SQLite in memory and a fake clock.
