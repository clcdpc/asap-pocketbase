const assert = require('assert');
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');

const ENTRY_FILES = [
  'pb_public/staff/js/settings.js',
  'pb_public/staff/js/settings-labels.js',
  'pb_public/staff/js/settings-polaris.js',
];

const SETTINGS_DIR = path.resolve(ROOT, 'pb_public/staff/js/settings');

function collectJsFiles(dir) {
  const results = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      results.push(...collectJsFiles(full));
    } else if (entry.isFile() && entry.name.endsWith('.js')) {
      results.push(full);
    }
  }
  return results;
}

function findImportPaths(source) {
  const paths = [];
  const re = /(?:import|export)\s+(?:(?:\*\s+as\s+\w+\s+from\s+['"]|[\s\S]*?\s+from\s+['"]|['"]))([^'"]+)(?=['"])/g;
  let match;
  while ((match = re.exec(source)) !== null) {
    paths.push(match[1]);
  }

  const sideEffectRe = /import\s+['"]([^'"]+)['"]/g;
  while ((match = sideEffectRe.exec(source)) !== null) {
    if (!paths.includes(match[1])) {
      paths.push(match[1]);
    }
  }

  return paths;
}

function resolveImport(importer, importPath) {
  if (!importPath.startsWith('.')) return null;

  const raw = path.resolve(path.dirname(importer), importPath);
  const candidates = [raw, `${raw}.js`, path.join(raw, 'index.js')];

  return candidates.find(candidate => fs.existsSync(candidate) && fs.statSync(candidate).isFile()) || null;
}

function normalize(file) {
  return path.relative(ROOT, file).replace(/\\/g, '/');
}

function isInScope(file) {
  const rel = normalize(file);
  return (
    rel === 'pb_public/staff/js/settings.js' ||
    rel === 'pb_public/staff/js/settings-labels.js' ||
    rel === 'pb_public/staff/js/settings-polaris.js' ||
    rel.startsWith('pb_public/staff/js/settings/')
  );
}

function buildGraph(files) {
  const graph = new Map();

  for (const file of files) {
    if (!isInScope(file)) continue;

    const source = fs.readFileSync(file, 'utf8');
    const deps = findImportPaths(source)
      .map(importPath => resolveImport(file, importPath))
      .filter(Boolean)
      .filter(isInScope);

    graph.set(normalize(file), deps.map(normalize));
  }

  return graph;
}

function findCycles(graph) {
  const cycles = [];
  const visiting = new Set();
  const visited = new Set();
  const stack = [];

  function visit(node) {
    if (visiting.has(node)) {
      const start = stack.indexOf(node);
      cycles.push([...stack.slice(start), node]);
      return;
    }

    if (visited.has(node)) return;

    visiting.add(node);
    stack.push(node);

    for (const dep of graph.get(node) || []) {
      visit(dep);
    }

    stack.pop();
    visiting.delete(node);
    visited.add(node);
  }

  for (const node of graph.keys()) {
    visit(node);
  }

  return cycles;
}

console.log('Running module import cycle regression tests...');

const files = [
  ...ENTRY_FILES.map(file => path.resolve(ROOT, file)),
  ...collectJsFiles(SETTINGS_DIR),
];

const graph = buildGraph(files);
const cycles = findCycles(graph);

assert.deepStrictEqual(
  cycles,
  [],
  `Settings import graph should be acyclic:\n${cycles.map(cycle => `- ${cycle.join(' -> ')}`).join('\n')}`
);

console.log('All settings import cycle regression checks passed.');
