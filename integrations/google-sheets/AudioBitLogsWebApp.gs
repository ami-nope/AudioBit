const AUDIOBIT_SPREADSHEET_ID = '';
const AUDIOBIT_SPREADSHEET_URL = '';
const AUDIOBIT_DEFAULT_SHEET = 'Sheet1';
const AUDIOBIT_SCRIPT_VERSION = '2026-04-06.2';
const AUDIOBIT_HEADER = [
  'Timestamp',
  'DeviceId',
  'Level',
  'Message',
  'Endpoint'
];

function doGet() {
  return jsonOutput_({
    status: 'ready',
    scriptVersion: AUDIOBIT_SCRIPT_VERSION,
    expectedMethod: 'POST',
    sheetColumns: AUDIOBIT_HEADER
  });
}

function doPost(e) {
  const lock = LockService.getScriptLock();
  lock.waitLock(30000);

  try {
    const payload = parsePayload_(e);
    const deviceId = requireDeviceId_(payload);
    const spreadsheet = openSpreadsheet_();
    const sheetTarget = resolveSheetTarget_(payload, deviceId);
    const requestedSheetName = sheetTarget.sheetName;
    const sheetResult = getOrCreateSheet_(spreadsheet, requestedSheetName);
    const sheet = sheetResult.sheet;

    ensureHeader_(sheet);

    const rows = buildRows_(payload, deviceId);
    if (rows.length > 0) {
      const startRow = sheet.getLastRow() + 1;
      sheet.getRange(startRow, 1, rows.length, AUDIOBIT_HEADER.length).setValues(rows);
    }

    return jsonOutput_({
      status: 'success',
      scriptVersion: AUDIOBIT_SCRIPT_VERSION,
      deviceId: deviceId,
      sheetSource: sheetTarget.source,
      sheetName: sheet.getName(),
      createdSheet: sheetResult.createdSheet,
      entryCount: rows.length,
      spreadsheetUrl: spreadsheet.getUrl()
    });
  } catch (error) {
    return jsonOutput_({
      status: 'error',
      message: error && error.message ? error.message : String(error)
    });
  } finally {
    lock.releaseLock();
  }
}

function parsePayload_(e) {
  if (!e || !e.postData || !e.postData.contents) {
    throw new Error('Missing POST body.');
  }

  return JSON.parse(e.postData.contents);
}

function requireDeviceId_(payload) {
  const deviceId = safeString_(payload.deviceId).trim();
  if (!deviceId) {
    throw new Error('Missing deviceId.');
  }

  return deviceId;
}

function resolveSheetTarget_(payload, deviceId) {
  const deviceName = safeString_(payload.deviceName).trim();
  return {
    sheetName: sanitizeSheetName_(deviceName || deviceId),
    source: deviceName ? 'deviceName' : 'deviceId'
  };
}

function openSpreadsheet_() {
  const spreadsheetId = String(AUDIOBIT_SPREADSHEET_ID || '').trim();
  if (spreadsheetId && spreadsheetId !== '') {
    return SpreadsheetApp.openById(spreadsheetId);
  }

  const spreadsheetUrl = String(AUDIOBIT_SPREADSHEET_URL || '').trim();
  if (spreadsheetUrl && spreadsheetUrl !== '') {
    return SpreadsheetApp.openByUrl(spreadsheetUrl);
  }

  return SpreadsheetApp.getActiveSpreadsheet();
}

function getOrCreateSheet_(spreadsheet, requestedSheetName) {
  let sheet = spreadsheet.getSheetByName(requestedSheetName);
  if (sheet) {
    return { sheet: sheet, createdSheet: false };
  }

  sheet = spreadsheet.insertSheet(requestedSheetName);
  return { sheet: sheet, createdSheet: true };
}

function ensureHeader_(sheet) {
  const lastRow = sheet.getLastRow();
  if (lastRow === 0) {
    sheet.getRange(1, 1, 1, AUDIOBIT_HEADER.length).setValues([AUDIOBIT_HEADER]);
  } else {
    const currentHeader = sheet.getRange(1, 1, 1, AUDIOBIT_HEADER.length).getValues()[0];
    if (!headersMatch_(currentHeader, AUDIOBIT_HEADER)) {
      sheet.getRange(1, 1, 1, AUDIOBIT_HEADER.length).setValues([AUDIOBIT_HEADER]);
    }
  }

  sheet.setFrozenRows(1);
}

function headersMatch_(currentHeader, expectedHeader) {
  if (!currentHeader || currentHeader.length < expectedHeader.length) {
    return false;
  }

  for (var index = 0; index < expectedHeader.length; index++) {
    if (safeString_(currentHeader[index]) !== expectedHeader[index]) {
      return false;
    }
  }

  return true;
}

function buildRows_(payload, deviceId) {
  const requestTimestamp = safeString_(payload.timestamp);
  const requestLevel = safeString_(payload.level);
  const requestMessage = safeString_(payload.message);
  const requestEndpoint = resolveEndpoint_(payload);
  if (Array.isArray(payload.entries) && payload.entries.length > 0) {
    return payload.entries.map(function(entry) {
      return buildRow_(
        entry,
        deviceId,
        requestTimestamp,
        requestLevel,
        requestMessage,
        requestEndpoint
      );
    });
  }

  return [buildRow_(
    payload,
    deviceId,
    requestTimestamp,
    requestLevel,
    requestMessage,
    requestEndpoint
  )];
}

function buildRow_(entry, deviceId, fallbackTimestamp, fallbackLevel, fallbackMessage, fallbackEndpoint) {
  return [
    safeString_(entry.timestamp) || fallbackTimestamp,
    deviceId,
    safeString_(entry.level) || fallbackLevel,
    safeString_(entry.message) || fallbackMessage,
    resolveEndpoint_(entry) || fallbackEndpoint
  ];
}

function resolveEndpoint_(source) {
  return safeString_(
    source.endpoint
    || source.endpointUrl
    || source.requestEndpoint
    || source.requestUrl
  );
}

function sanitizeSheetName_(value) {
  const fallback = AUDIOBIT_DEFAULT_SHEET;
  const normalized = String(value || fallback)
    .trim()
    .replace(/[\[\]\*\/\\\?]/g, '_')
    .replace(/^'+|'+$/g, '');
  const candidate = normalized || fallback;
  return candidate.length <= 95 ? candidate : candidate.substring(0, 95);
}

function safeString_(value) {
  return value == null ? '' : String(value);
}

function jsonOutput_(value) {
  return ContentService
    .createTextOutput(JSON.stringify(value))
    .setMimeType(ContentService.MimeType.JSON);
}
