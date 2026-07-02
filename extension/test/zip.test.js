"use strict";
// Plain-node unit tests for the hand-rolled ZIP writer (src/zip.js).
// Verifies structural correctness (signatures, CRC-32), then extracts the
// archive with python3 -m zipfile and byte-compares the results. No deps.
// Run: node zip.test.js
const fs = require("fs");
const os = require("os");
const path = require("path");
const { execSync } = require("child_process");
const { crc32, buildZip } = require("../src/zip");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}

// ── CRC-32 check value ──────────────────────────────────────────────────────

ok("crc32: check value 123456789 => 0xCBF43926",
  crc32(Buffer.from("123456789")) === 0xCBF43926,
  "got 0x" + crc32(Buffer.from("123456789")).toString(16).toUpperCase());

ok("crc32: empty buffer => 0x00000000", crc32(Buffer.alloc(0)) === 0x00000000);

ok("crc32: single byte", crc32(Buffer.from([0x61])) === 0xE8B7BE43,
  "got 0x" + crc32(Buffer.from([0x61])).toString(16).toUpperCase());

// ── ZIP structure ────────────────────────────────────────────────────────────

const entries = [
  { path: "hello.txt", data: "Hello, World!\n" },
  { path: "projects/test.json", data: Buffer.from('{"name":"test"}\n') },
  { path: "empty.txt", data: "" },
];
const zipBuf = buildZip(entries);

// Local file header signature (PK\x03\x04 = 0x04034B50 LE).
ok("zip: starts with local file header signature",
  zipBuf[0] === 0x50 && zipBuf[1] === 0x4B && zipBuf[2] === 0x03 && zipBuf[3] === 0x04);

// Central directory signature somewhere in the buffer (PK\x01\x02 = 0x02014B50).
const cdSig = Buffer.from([0x50, 0x4B, 0x01, 0x02]);
ok("zip: contains central directory header signature", zipBuf.indexOf(cdSig) >= 0);

// EOCD signature (PK\x05\x06 = 0x06054B50).
const eocdSig = Buffer.from([0x50, 0x4B, 0x05, 0x06]);
ok("zip: ends with EOCD record", zipBuf.indexOf(eocdSig) >= 0);

// Entry count in EOCD.
const eocdOff = zipBuf.indexOf(eocdSig);
const entryCount = zipBuf.readUInt16LE(eocdOff + 10);
ok("zip: EOCD reports correct entry count", entryCount === entries.length,
  "got " + entryCount);

// Verify that CRC in the first local file header matches the data.
const helloData = Buffer.from("Hello, World!\n");
const expectedCrc = crc32(helloData);
const crcInHeader = zipBuf.readUInt32LE(14); // offset 14 in the first local file header
ok("zip: CRC in first local header matches data",
  crcInHeader === expectedCrc,
  "expected 0x" + expectedCrc.toString(16) + " got 0x" + crcInHeader.toString(16));

// Forward-slash entry names (ZIP spec requirement; .NET/Explorer choke on backslash).
ok("zip: entry names use forward slashes", !zipBuf.toString("utf8").includes("projects\\"));

// Fixed DOS date: 0x5C21 at offset 12 of the first local file header.
const dosDate = zipBuf.readUInt16LE(12);
ok("zip: fixed DOS date (2026-01-01)", dosDate === 0x5C21,
  "got 0x" + dosDate.toString(16));

// Fixed DOS time: 0x0000 at offset 10.
const dosTime = zipBuf.readUInt16LE(10);
ok("zip: fixed DOS time (00:00)", dosTime === 0x0000);

// Compression method: STORED (0) at offset 8.
const method = zipBuf.readUInt16LE(8);
ok("zip: compression method is STORED (0)", method === 0);

// ── Extraction test ─────────────────────────────────────────────────────────

const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), "construct-zip-"));
try {
  const zipPath = path.join(tmpDir, "test.zip");
  const extractDir = path.join(tmpDir, "out");
  fs.writeFileSync(zipPath, zipBuf);
  fs.mkdirSync(extractDir);

  // Extract with python3 -m zipfile -e.
  let extractOk = false;
  try {
    execSync(`python3 -m zipfile -e "${zipPath}" "${extractDir}"`, { stdio: "pipe" });
    extractOk = true;
  } catch (e) {
    console.log("  WARN  python3 -m zipfile extraction failed: " + e.message);
  }
  ok("zip: python3 extraction succeeds", extractOk);

  if (extractOk) {
    // Byte-compare extracted files.
    const helloOut = fs.readFileSync(path.join(extractDir, "hello.txt"), "utf8");
    ok("zip: extracted hello.txt matches", helloOut === "Hello, World!\n");

    const jsonOut = fs.readFileSync(path.join(extractDir, "projects", "test.json"), "utf8");
    ok("zip: extracted projects/test.json matches", jsonOut === '{"name":"test"}\n');

    const emptyOut = fs.readFileSync(path.join(extractDir, "empty.txt"), "utf8");
    ok("zip: extracted empty.txt is empty", emptyOut === "");
  }

  // Also try unzip -t (integrity check).
  let unzipOk = false;
  try {
    execSync(`unzip -t "${zipPath}"`, { stdio: "pipe" });
    unzipOk = true;
  } catch (e) {
    console.log("  WARN  unzip -t failed: " + e.message);
  }
  ok("zip: unzip -t integrity check passes", unzipOk);

  // ── Edge case: single entry ──────────────────────────────────────────────
  const singleZip = buildZip([{ path: "one.txt", data: "only" }]);
  const singlePath = path.join(tmpDir, "single.zip");
  const singleDir = path.join(tmpDir, "single-out");
  fs.writeFileSync(singlePath, singleZip);
  fs.mkdirSync(singleDir);
  let singleOk = false;
  try {
    execSync(`python3 -m zipfile -e "${singlePath}" "${singleDir}"`, { stdio: "pipe" });
    singleOk = fs.readFileSync(path.join(singleDir, "one.txt"), "utf8") === "only";
  } catch (_) { /* skip */ }
  ok("zip: single-entry zip round-trips", singleOk);

  // ── Edge case: empty archive ──────────────────────────────────────────────
  const emptyZip = buildZip([]);
  ok("zip: empty archive has EOCD only", emptyZip.length === 22);
  ok("zip: empty archive starts with EOCD signature",
    emptyZip[0] === 0x50 && emptyZip[1] === 0x4B && emptyZip[2] === 0x05 && emptyZip[3] === 0x06);

  // ── Edge case: binary data ────────────────────────────────────────────────
  const binData = Buffer.from([0x00, 0xFF, 0x80, 0x7F, 0x01]);
  const binZip = buildZip([{ path: "bin.dat", data: binData }]);
  const binPath = path.join(tmpDir, "bin.zip");
  const binDir = path.join(tmpDir, "bin-out");
  fs.writeFileSync(binPath, binZip);
  fs.mkdirSync(binDir);
  let binOk = false;
  try {
    execSync(`python3 -m zipfile -e "${binPath}" "${binDir}"`, { stdio: "pipe" });
    const extracted = fs.readFileSync(path.join(binDir, "bin.dat"));
    binOk = Buffer.compare(extracted, binData) === 0;
  } catch (_) { /* skip */ }
  ok("zip: binary data round-trips", binOk);

} finally {
  fs.rmSync(tmpDir, { recursive: true, force: true });
}

console.log(`\n  zip unit tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);
