#!/usr/bin/env node
/**
 * Regenerates src/Sofia.Server/Providers/ModelsDevSnapshot.json — the boot-time floor of the
 * ModelCatalog: a trimmed copy of https://models.dev/api.json holding only the context window and
 * the output limit of every model.
 *
 * Usage:
 *   node script/update-model-snapshot.mjs                  # fetch the live catalog
 *   node script/update-model-snapshot.mjs --input api.json # build from a local copy
 *
 * SOFIA_MODELS_URL overrides the source URL. The output is deterministic (providers and models
 * sorted, one provider per line), so running it twice with the same input produces the same file.
 */
import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const target = path.join(root, 'src', 'Sofia.Server', 'Providers', 'ModelsDevSnapshot.json');

const args = process.argv.slice(2);
const inputIndex = args.indexOf('--input');
const input = inputIndex >= 0 ? args[inputIndex + 1] : null;
if (inputIndex >= 0 && !input) {
  console.error('--input needs a file path.');
  process.exit(1);
}

let source;
if (input) {
  source = await readFile(path.resolve(input), 'utf8');
} else {
  const url = process.env.SOFIA_MODELS_URL || 'https://models.dev/api.json';
  const response = await fetch(url, { headers: { accept: 'application/json' } });
  if (!response.ok) {
    console.error(`Fetching ${url} failed: ${response.status} ${response.statusText}`);
    process.exit(1);
  }
  source = await response.text();
}

const catalog = JSON.parse(source);
if (!catalog || typeof catalog !== 'object') {
  console.error('The catalog is not a JSON object of providers.');
  process.exit(1);
}

const trimmed = {};
let entries = 0;
for (const providerId of Object.keys(catalog).sort()) {
  const models = catalog[providerId]?.models;
  if (!models || typeof models !== 'object') continue;
  const kept = {};
  for (const modelId of Object.keys(models).sort()) {
    const context = Math.trunc(Number(models[modelId]?.limit?.context));
    if (!Number.isFinite(context) || context <= 0) continue;
    const output = Math.trunc(Number(models[modelId]?.limit?.output));
    kept[modelId] = { c: context, o: Number.isFinite(output) && output > 0 ? output : 0 };
    entries++;
  }
  if (Object.keys(kept).length > 0) trimmed[providerId] = kept;
}

const body = '{\n' + Object.entries(trimmed)
  .map(([providerId, models]) => `${JSON.stringify(providerId)}: ${JSON.stringify(models)}`)
  .join(',\n') + '\n}\n';

await writeFile(target, body, 'utf8');
const providers = Object.keys(trimmed).length;
console.log(`${entries} model limits from ${providers} providers → ${path.relative(root, target)} (${(Buffer.byteLength(body) / 1024).toFixed(0)} KB).`);
