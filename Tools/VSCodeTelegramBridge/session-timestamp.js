"use strict";

const LOCAL_UTC_TIMESTAMP = /^(\d{1,2})\.(\d{1,2})\.(\d{4})[ ,]+(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?$/;

function parseCopilotTimestamp(value) {
  if (typeof value !== "string") return Number.NaN;

  const match = LOCAL_UTC_TIMESTAMP.exec(value);
  if (!match) return Date.parse(value);

  const [, dayText, monthText, yearText, hourText, minuteText, secondText, fraction = ""] = match;
  const day = Number(dayText);
  const month = Number(monthText);
  const year = Number(yearText);
  const hour = Number(hourText);
  const minute = Number(minuteText);
  const second = Number(secondText);
  const millisecond = Number((fraction + "000").slice(0, 3));
  const timestamp = Date.UTC(year, month - 1, day, hour, minute, second, millisecond);
  const parsed = new Date(timestamp);

  if (parsed.getUTCFullYear() !== year
    || parsed.getUTCMonth() !== month - 1
    || parsed.getUTCDate() !== day
    || parsed.getUTCHours() !== hour
    || parsed.getUTCMinutes() !== minute
    || parsed.getUTCSeconds() !== second) {
    return Number.NaN;
  }

  return timestamp;
}

module.exports = { parseCopilotTimestamp };
