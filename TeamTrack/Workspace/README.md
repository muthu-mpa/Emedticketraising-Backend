# Workspace module (feature-flagged)

Campaign mail approval, leave & permission, people directory, project documents, home dashboard and
notifications. Spec: `EmedTicketRaising/docs/workspace-spec.md`.

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

## Things worth knowing
- **Document files** go to `wwwroot/uploads/documents` (pdf, docx, xlsx, pptx, png, jpg; 20 MB max; content is checked
  against the extension). This is separate from the work-item screenshot upload, which is unchanged.
- **Sending a campaign** first claims it with `UPDATE ... WHERE status = 'approved'` (status `sending`), so it can never
  go out twice. A failed send puts it back to `approved` and records a "Send failed" history entry. If the app is killed
  in the middle of a send, the mail stays `sending` on purpose (it may or may not have gone out). Check the mailbox,
  then fix the row by hand: `UPDATE ws_campaigns SET status = 'approved'` (not sent) or `'sent'` (it went out) `WHERE id = ...`.
- **Access:** a ProductManager (role in the JWT) can view and add documents in every project; everyone else needs to be in `project_members`.
