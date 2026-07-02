"use strict";
// Hand-rolled ZIP writer (STORED entries, no compression). Produces archives
// that Windows Explorer, Expand-Archive, and standard Unix tooling can extract.
//
// Why hand-rolled: the extension has zero npm dependencies ("No build step.
// Plain JS." — ARCHITECTURE.md) and zlib's deflate is overkill for the tiny
// config bundles this serves. STORED entries are byte-identical to the input,
// making round-trip tests trivial and extraction universal.
//
// Format: local file headers + file data + central directory + EOCD record.
// All multi-byte integers are little-endian. Entry names use forward slashes
// (the ZIP spec mandates this; .NET Framework's ZipArchive on Windows chokes on
// backslashes). A fixed DOS date/time (2026-01-01 00:00) keeps the archive
// deterministic across runs.

// ── CRC-32 (ISO 3309 / ITU-T V.42, the one ZIP uses) ────────────────────────

const crcTable = new Uint32Array(256);
for (let n = 0; n < 256; n++) {
  let c = n;
  for (let k = 0; k < 8; k++) c = (c & 1) ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1);
  crcTable[n] = c;
}

/**
 * Compute the CRC-32 of a Buffer (or Uint8Array). Returns an unsigned 32-bit
 * integer. The well-known check value is crc32(Buffer.from('123456789')) ===
 * 0xCBF43926.
 */
function crc32(buf) {
  let crc = 0xFFFFFFFF;
  for (let i = 0; i < buf.length; i++) {
    crc = crcTable[(crc ^ buf[i]) & 0xFF] ^ (crc >>> 8);
  }
  return (crc ^ 0xFFFFFFFF) >>> 0;
}

// ── ZIP builder ──────────────────────────────────────────────────────────────

// Fixed DOS date/time: 2026-01-01 00:00:00.
// DOS time = 0 (00:00:00), DOS date = ((2026-1980)<<9) | (1<<5) | 1 = 0x5C21.
const DOS_TIME = 0x0000;
const DOS_DATE = 0x5C21;

function writeU16(buf, offset, val) { buf[offset] = val & 0xFF; buf[offset + 1] = (val >>> 8) & 0xFF; }
function writeU32(buf, offset, val) {
  buf[offset]     =  val         & 0xFF;
  buf[offset + 1] = (val >>> 8)  & 0xFF;
  buf[offset + 2] = (val >>> 16) & 0xFF;
  buf[offset + 3] = (val >>> 24) & 0xFF;
}

/**
 * Build a ZIP archive (as a Buffer) from an array of entries.
 *
 * Each entry: { path: string, data: Buffer|string }.
 *
 * - Paths use forward slashes (required by ZIP spec).
 * - No compression (STORED, method 0) — the data is written verbatim.
 * - A fixed DOS timestamp keeps the output deterministic.
 * - Correct local-file headers + central directory + EOCD.
 *
 * The result is extractable by Windows Explorer, PowerShell Expand-Archive,
 * Python zipfile, and Info-ZIP unzip.
 */
function buildZip(entries) {
  const records = [];
  let totalSize = 0;

  // Pre-compute per-entry buffers and metadata.
  for (const entry of entries) {
    const nameBytes = Buffer.from(entry.path, "utf8");
    const dataBytes = typeof entry.data === "string" ? Buffer.from(entry.data, "utf8") : entry.data;
    const fileCrc = crc32(dataBytes);
    const size = dataBytes.length;

    // Local file header: 30 bytes + name length.
    const lfh = Buffer.alloc(30 + nameBytes.length);
    writeU32(lfh, 0, 0x04034B50);   // local file header signature
    writeU16(lfh, 4, 20);           // version needed to extract (2.0)
    writeU16(lfh, 6, 0);            // general purpose bit flag
    writeU16(lfh, 8, 0);            // compression method: STORED
    writeU16(lfh, 10, DOS_TIME);
    writeU16(lfh, 12, DOS_DATE);
    writeU32(lfh, 14, fileCrc);
    writeU32(lfh, 18, size);        // compressed size
    writeU32(lfh, 22, size);        // uncompressed size
    writeU16(lfh, 26, nameBytes.length);
    writeU16(lfh, 28, 0);           // extra field length
    nameBytes.copy(lfh, 30);

    records.push({ nameBytes, dataBytes, fileCrc, size, lfh, offset: totalSize });
    totalSize += lfh.length + dataBytes.length;
  }

  // Central directory.
  const cdParts = [];
  let cdSize = 0;
  for (const rec of records) {
    const cdh = Buffer.alloc(46 + rec.nameBytes.length);
    writeU32(cdh, 0, 0x02014B50);   // central directory header signature
    writeU16(cdh, 4, 20);           // version made by (2.0)
    writeU16(cdh, 6, 20);           // version needed to extract
    writeU16(cdh, 8, 0);            // general purpose bit flag
    writeU16(cdh, 10, 0);           // compression method: STORED
    writeU16(cdh, 12, DOS_TIME);
    writeU16(cdh, 14, DOS_DATE);
    writeU32(cdh, 16, rec.fileCrc);
    writeU32(cdh, 20, rec.size);    // compressed size
    writeU32(cdh, 24, rec.size);    // uncompressed size
    writeU16(cdh, 28, rec.nameBytes.length);
    writeU16(cdh, 30, 0);           // extra field length
    writeU16(cdh, 32, 0);           // file comment length
    writeU16(cdh, 34, 0);           // disk number start
    writeU16(cdh, 36, 0);           // internal file attributes
    writeU32(cdh, 38, 0);           // external file attributes
    writeU32(cdh, 42, rec.offset);  // relative offset of local header
    rec.nameBytes.copy(cdh, 46);
    cdParts.push(cdh);
    cdSize += cdh.length;
  }

  // End of central directory record (22 bytes).
  const eocd = Buffer.alloc(22);
  writeU32(eocd, 0, 0x06054B50);    // EOCD signature
  writeU16(eocd, 4, 0);             // disk number
  writeU16(eocd, 6, 0);             // disk with CD start
  writeU16(eocd, 8, records.length);  // total entries on this disk
  writeU16(eocd, 10, records.length); // total entries
  writeU32(eocd, 12, cdSize);       // size of central directory
  writeU32(eocd, 16, totalSize);    // offset of CD start
  writeU16(eocd, 20, 0);            // comment length

  // Assemble: local headers + data, then CD, then EOCD.
  const parts = [];
  for (const rec of records) { parts.push(rec.lfh, rec.dataBytes); }
  for (const cd of cdParts) parts.push(cd);
  parts.push(eocd);

  return Buffer.concat(parts);
}

module.exports = { crc32, buildZip };
