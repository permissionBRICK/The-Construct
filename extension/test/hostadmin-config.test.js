"use strict";
const assert = require("assert");
const fs = require("fs");
const path = require("path");
const schema = require("../src/hostadmin-config-schema");
const fixture = require("../../test/fixtures/companion-parity/hostadmin-config-schema.json");
const records = fs.readFileSync(path.join(__dirname, "../../service/src/Constructd.Core/Domain/HostConfig.cs"), "utf8");
assert.deepStrictEqual(fixture, schema);
assert.deepStrictEqual(schema.map(s => s.key), require("../src/hostadmin").CONFIG_SECTIONS);
for (const section of fixture) {
  const record = section.key[0].toUpperCase() + section.key.slice(1) + "Config";
  const properties = records.match(new RegExp(`record ${record}\\(([^;]+?)\\);`))[1].split(",")
    .map(p => p.trim().match(/^\w+\??\s+(\w+)/)[1]).map(p => p[0].toLowerCase() + p.slice(1));
  assert.deepStrictEqual([...section.fields.map(f => f.key), ...section.rawOnly].sort(), properties.sort(), record);
  assert.strictEqual(new Set(section.fields.map(f => f.key)).size, section.fields.length);
  for (const field of section.fields) {
    assert.ok(field.label && field.help && Object.hasOwn(field, "default"), field.key);
    assert.ok(["bool", "int", "bytes", "seconds", "minutes", "percent", "enum", "string", "list"].includes(field.type));
    if (field.default === null) assert.ok(field.nullable, field.key);
  }
}
console.log("Host configuration schema covers every C# record property.");
