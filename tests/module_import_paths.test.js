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

function parseNamedList(raw) {
  return raw
    .split(',')
    .map(part => part.trim())
    .filter(Boolean)
    .map(part => part.split(/\s+as\s+/)[0].trim())
    .filter(name => name && name !== 'default');
}

function findNamedImports(source) {
  const imports = [];
  const re = /import\s*\{([^}]+)\}\s*from\s*['"]([^'"]+)['"]/g;
  let match;
  while ((match = re.exec(source)) !== null) {
    imports.push({ importPath: match[2], names: parseNamedList(match[1]) });
  }
  return imports;
}

function resolveImportPath(importerFile, importPath) {
  if (bareImportPattern.test(importPath)) {
    return null;
  }
  const resolved = path.resolve(path.dirname(importerFile), importPath);
  return resolved;
}

function resolveImportFile(importerFile, importPath) {
  const resolved = resolveImportPath(importerFile, importPath);
  if (resolved === null) return null;
  return fileExistsLookup(resolved);
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

const exportCache = new Map();

function exportedNames(file) {
  if (exportCache.has(file)) return exportCache.get(file);

  const names = new Set();
  exportCache.set(file, names);

  const source = fs.readFileSync(file, 'utf8');
  const declarationRe = /export\s+(?:async\s+)?(?:function|class|const|let|var)\s+([A-Za-z_$][\w$]*)/g;
  let match;
  while ((match = declarationRe.exec(source)) !== null) {
    names.add(match[1]);
  }

  const namedExportRe = /export\s*\{([^}]+)\}(?:\s*from\s*['"]([^'"]+)['"])?/g;
  while ((match = namedExportRe.exec(source)) !== null) {
    if (match[2]) {
      parseNamedList(match[1]).forEach(name => names.add(name));
    } else {
      match[1]
        .split(',')
        .map(part => part.trim())
        .filter(Boolean)
        .map(part => {
          const pieces = part.split(/\s+as\s+/);
          return (pieces[1] || pieces[0]).trim();
        })
        .filter(name => name && name !== 'default')
        .forEach(name => names.add(name));
    }
  }

  const starExportRe = /export\s+\*\s+from\s*['"]([^'"]+)['"]/g;
  while ((match = starExportRe.exec(source)) !== null) {
    const resolved = resolveImportFile(file, match[1]);
    if (!resolved) continue;
    exportedNames(resolved).forEach(name => names.add(name));
  }

  return names;
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

  for (const namedImport of findNamedImports(source)) {
    const resolved = resolveImportFile(file, namedImport.importPath);
    if (resolved === null) continue;

    const exported = exportedNames(resolved);
    for (const name of namedImport.names) {
      total++;
      if (!exported.has(name)) {
        const importRel = path.relative(ROOT, resolved);
        console.error(`  FAIL: ${relPath} imports { ${name} } from '${namedImport.importPath}' -> ${importRel} does not export '${name}'`);
        failed++;
      }
    }
  }
}

if (failed > 0) {
  console.error(`\n${failed}/${total} import paths failed to resolve.`);
  process.exit(1);
}

console.log(`All ${total} import paths resolve correctly.`);
