"use strict";

// Masks personal data before it leaves the PC for the Cursor model. Masks are partial (last digits stay visible)
// so the user can still tell records apart.

const EMAIL = /([A-Za-z0-9._%+-])[A-Za-z0-9._%+-]*@([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,})/g;
// Unlabelled Turkish numbers need the 0/+90 prefix; bare 10-digit runs may be barcodes or serial numbers.
const PHONE = /(?<![\d])(?:\+90[\s-]?|0)\(?[2-5]\d{2}\)?[\s.-]?\d{3}[\s.-]?\d{2}[\s.-]?\d{2}(?![\d])/g;
const LABELLED_PHONE = /((?:tel(?:efon)?|gsm|cep|whatsapp)\s*[:#]?\s*)(\+?\d[\d\s().-]{5,}\d)/gi;
// TC kimlik (11 digits) and VKN (10 digits) only when labelled, so barcodes and serial numbers are not touched.
const LABELLED_ID = /((?:T\.?\s?C\.?|TCKN|kimlik(?:\s+no)?|VKN|vergi(?:\s+(?:no|numaras[ıi]))?)\s*[:#]?\s*)(\d{10,11})(?![\d])/gi;

function maskDigits(text, keep = 2) {
  const digits = text.replace(/\D/g, "");
  if (digits.length <= keep) return text;
  return `${"*".repeat(digits.length - keep)}${digits.slice(-keep)}`;
}

function maskPii(value) {
  if (typeof value !== "string" || value.length === 0) return value;
  return value
    .replace(EMAIL, (_, first, domain) => `${first}***@${domain}`)
    .replace(LABELLED_ID, (_, label, digits) => `${label}${maskDigits(digits)}`)
    .replace(LABELLED_PHONE, (_, label, digits) => `${label}${maskDigits(digits)}`)
    .replace(PHONE, (match) => maskDigits(match));
}

module.exports = { maskPii };
