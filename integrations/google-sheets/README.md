# AudioBit Google Sheets Web App

This Apps Script creates one Google Sheets tab per device using the `deviceName` sent by AudioBit.

Expected response fields:

- `status`
- `deviceId`
- `sheetSource`
- `sheetName`
- `createdSheet`
- `entryCount`

## Deploy

1. Create a Google Apps Script project bound to your spreadsheet, or set `AUDIOBIT_SPREADSHEET_ID` or `AUDIOBIT_SPREADSHEET_URL` in `AudioBitLogsWebApp.gs`.
2. Paste in `AudioBitLogsWebApp.gs`.
3. Deploy it as a Web App, then create a new deployment or update the existing deployment.
4. Copy the Web App URL.
5. If the deployment URL changes, update the built-in endpoint in `AudioBit.App/Services/GoogleSheetsLogSyncService.cs`.

## Verify Deployment

- Open the Web App URL in a browser.
- A current deployment returns JSON with `status: "ready"` and `scriptVersion: "2026-04-06.2"`.
- If the browser still shows `Script function not found: doGet`, you are still hitting an older deployment.

## Behavior

- Every request must include `deviceId`; the script names the sheet from `deviceName` after Google Sheets-safe sanitization, and falls back to `deviceId` only if `deviceName` is blank.
- If the device sheet exists, rows append to the next empty row in that sheet.
- If the device sheet does not exist, the script creates it and returns `createdSheet: true`.
- Each sheet uses the same five-column layout: `Timestamp`, `DeviceId`, `Level`, `Message`, `Endpoint`.
- The desktop app already parses `sheetName` and `createdSheet` and will show the confirmed result.
- If AudioBit still logs `confirmedSheet='(unconfirmed)'`, you are still hitting the old deployed web app.
