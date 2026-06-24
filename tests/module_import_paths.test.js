const assert = require('assert');
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');

const SEARCH_DIRS = [
  path.resolve(ROOT, 'pb_public/staff'),
  path.resolve(ROOT, 'pb_public/patron'),
];

const bareImportPattern = /^[a-zA-Z@]/;

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

function resolveImportPath(importerFile, importPath) {
  if (bareImportPattern.test(importPath)) {
    return null;
  }
  const resolved = path.resolve(path.dirname(importerFile), importPath);
  return resolved;
}

function fileExistsLookup(basePath) {
  if (fs.existsSync(basePath) && fs.statSync(basePath).isFile()) {
    return basePath;
  }
  if (fs.existsSync(basePath + '.js')) {
    return basePath + '.js';
  }
  if (fs.existsSync(basePath + '.mjs')) {
    return basePath + '.mjs';
  }
  return null;
}

function collectJsFiles(dir) {
  const results = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      results.push(...collectJsFiles(full));
    } else if (entry.isFile() && (entry.name.endsWith('.js') || entry.name.endsWith('.mjs'))) {
      results.push(full);
    }
  }
  return results;
}

console.log('Running module import path validation tests...');

const allFiles = SEARCH_DIRS.flatMap(collectJsFiles);
let total = 0;
let failed = 0;

for (const file of allFiles) {
  const relPath = path.relative(ROOT, file);
  const source = fs.readFileSync(file, 'utf8');
  const importPaths = findImportPaths(source);

  for (const importPath of importPaths) {
    total++;
    const resolved = resolveImportPath(file, importPath);
    if (resolved === null) {
      continue;
    }
    const existing = fileExistsLookup(resolved);
    if (existing === null) {
      const importRel = path.relative(ROOT, resolved);
      console.error(`  FAIL: ${relPath} imports '${importPath}' -> resolves to ${importRel} (not found)`);
      failed++;
    }
  }
}

if (failed > 0) {
  console.error(`\n${failed}/${total} import paths failed to resolve.`);
  process.exit(1);
}

console.log(`All ${total} import paths resolve correctly.`);
