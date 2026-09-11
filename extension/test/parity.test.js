"use strict";
const assert = require("assert");
const fs = require("fs");
const path = require("path");
const exporter = require("./export-parity-fixtures");
const scripts = require("../src/guest-scripts");
const areas = exporter.exportAll();
for (const [area, rows] of Object.entries(areas)) {
  assert.strictEqual(exporter.serialize(rows), fs.readFileSync(path.join(exporter.directory, area + ".json"), "utf8"), area + ": regenerate parity fixtures with the behavior change");
}
for (const row of areas["guest-scripts"]) assert.strictEqual(scripts.render(row.name, row.values), row.output, row.name);
assert.deepStrictEqual([...new Set(areas["guest-scripts"].map(row => row.name))].sort(), scripts.names.slice().sort());
assert.throws(() => scripts.render("missing"), /Unknown guest script/);
assert.throws(() => scripts.render("forwards-capability"), /Missing guest script value/);
console.log(`Parity: ${Object.keys(areas).length} areas, ${Object.values(areas).reduce((n, rows) => n + rows.length, 0)} fixtures passed`);
