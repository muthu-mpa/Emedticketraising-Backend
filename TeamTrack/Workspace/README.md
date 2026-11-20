# Workspace module (feature-flagged)

Campaign mails (draft, edit, send), shared Excel sheets, people directory, home dashboard and notifications.
Leave/permission and project documents are still here but have no frontend. Spec: `EmedTicketRaising/docs/workspace-spec.md`.

## Turn it on
    Features__Workspace=true            (or "Features": { "Workspace": true } in appsettings)

Optional settings:

    Workspace__CompanyAddress=...       footer line on every campaign mail (default "Emedlogix Solutions")
    Workspace__TimeZone=Asia/Kolkata    used for "today" and "this month"

With the flag off nothing here is registered: no services, no tables, and `/api/workspace/*` returns 404.
With the flag on, startup creates the `ws_*` tables (`CREATE ... IF NOT EXISTS`). If that fails it is logged at
Critical level and every workspace endpoint answers 503 with the reason; the rest of the app keeps running.

## Tests
    dotnet test TeamTrack.Tests

## Shared sheets
Values and formulas only (formatting is not kept: `formattingLost` is true for uploaded sheets). The data is JSON in `ws_shared_sheets.data`
(sheet name -> row number -> column number -> `{ v, f?, t? }`); the uploaded original is kept in `{ContentRoot}/uploads/sheets`, outside
wwwroot, so it is never served statically. A save is all-or-nothing: if another person changed one of the same cells after `baseVersion`
the answer is 409 with those cells and nothing is written. Two saves at the same version are serialised by an optimistic concurrency
token on `version`. A value starting with "=" is stored as a formula (no result is calculated until the file is opened in Excel).

## Things worth knowing
- **Document files** go to `wwwroot/uploads/documents` (pdf, docx, xlsx, pptx, png, jpg; 20 MB max; content is checked
  against the extension). This is separate from the work-item screenshot upload, which is unchanged.
- **Sending a campaign** first claims it with `UPDATE ... WHERE status = 'draft'` (status `sending`), so it can never
  go out twice. A failed send puts it back to `draft` and records a "Send failed" history entry. If the app is killed
  in the middle of a send, the mail stays `sending` on purpose (it may or may not have gone out). Check the mailbox,
  then fix the row by hand: `UPDATE ws_campaigns SET status = 'draft'` (not sent) or `'sent'` (it went out) `WHERE id = ...`.
- **Access:** a ProductManager (role in the JWT) can view and add documents in every project; everyone else needs to be in `project_members`.
