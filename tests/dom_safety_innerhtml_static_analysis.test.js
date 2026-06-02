const assert = require('assert');
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '../pb_public/staff/js');

function walk(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap(entry => {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) return walk(full);
    return entry.isFile() && entry.name.endsWith('.js') ? [full] : [];
  });
}

function previousLineAllowsStaticHtml(lines, index) {
  const prev = lines[index - 1] || '';
  return prev.includes('dom-safe-static-html: static developer-authored markup only');
}

function collectInnerHtmlAssignments(source) {
  const assignments = [];
  const pattern = /\.innerHTML\s*(\+=|=)\s*/g;
  let match;

  while ((match = pattern.exec(source))) {
    const start = match.index;
    let cursor = pattern.lastIndex;
    let rhs = '';
    let quote = '';
    let escaped = false;
    let depth = 0;
    let inLineComment = false;
    let inBlockComment = false;

    while (cursor < source.length) {
      const ch = source[cursor];
      const next = source[cursor + 1];
      rhs += ch;

      if (inLineComment) {
        if (ch === '\n') inLineComment = false;
      } else if (inBlockComment) {
        if (ch === '*' && next === '/') {
          rhs += next;
          cursor++;
          inBlockComment = false;
        }
      } else if (quote) {
        if (escaped) {
          escaped = false;
        } else if (ch === '\\') {
          escaped = true;
        } else if (ch === quote) {
          quote = '';
        }
      } else {
        if (ch === '/' && next === '/') {
          rhs += next;
          cursor++;
          inLineComment = true;
        } else if (ch === '/' && next === '*') {
          rhs += next;
          cursor++;
          inBlockComment = true;
        } else if (ch === '"' || ch === "'" || ch === '`') {
          quote = ch;
        } else if (ch === '(' || ch === '[' || ch === '{') {
          depth++;
        } else if (ch === ')' || ch === ']' || ch === '}') {
          if (depth > 0) depth--;
        } else if (ch === ';' && depth === 0) {
          break;
        }
      }

      cursor++;
    }

    assignments.push({ start, rhs });
  }

  return assignments;
}

function stripOuterParens(value) {
  let trimmed = value.trim();
  while (trimmed.startsWith('(') && trimmed.endsWith(')')) {
    let depth = 0;
    let ok = true;
    for (let i = 0; i < trimmed.length; i++) {
      const ch = trimmed[i];
      if (ch === '(') depth++;
      else if (ch === ')') {
        depth--;
        if (depth === 0 && i !== trimmed.length - 1) {
          ok = false;
          break;
        }
      }
    }
    if (!ok || depth !== 0) break;
    trimmed = trimmed.slice(1, -1).trim();
  }
  return trimmed;
}

function splitTopLevel(value, delimiter) {
  const parts = [];
  let current = '';
  let quote = '';
  let escaped = false;
  let depth = 0;
  let inLineComment = false;
  let inBlockComment = false;

  for (let i = 0; i < value.length; i++) {
    const ch = value[i];
    const next = value[i + 1];

    if (inLineComment) {
      current += ch;
      if (ch === '\n') inLineComment = false;
      continue;
    }

    if (inBlockComment) {
      current += ch;
      if (ch === '*' && next === '/') {
        current += next;
        i++;
        inBlockComment = false;
      }
      continue;
    }

    if (quote) {
      current += ch;
      if (escaped) {
        escaped = false;
      } else if (ch === '\\') {
        escaped = true;
      } else if (ch === quote) {
        quote = '';
      }
      continue;
    }

    if (ch === '/' && next === '/') {
      current += ch + next;
      i++;
      inLineComment = true;
      continue;
    }

    if (ch === '/' && next === '*') {
      current += ch + next;
      i++;
      inBlockComment = true;
      continue;
    }

    if (ch === '"' || ch === "'" || ch === '`') {
      quote = ch;
      current += ch;
      continue;
    }

    if (ch === '(' || ch === '[' || ch === '{') {
      depth++;
      current += ch;
      continue;
    }

    if (ch === ')' || ch === ']' || ch === '}') {
      if (depth > 0) depth--;
      current += ch;
      continue;
    }

    if (ch === delimiter && depth === 0) {
      parts.push(current.trim());
      current = '';
      continue;
    }

    current += ch;
  }

  if (current.trim()) parts.push(current.trim());
  return parts.length > 1 ? parts : [value.trim()];
}

function stripQuotedStrings(value) {
  let out = '';
  let quote = '';
  let escaped = false;
  let inLineComment = false;
  let inBlockComment = false;

  for (let i = 0; i < value.length; i++) {
    const ch = value[i];
    const next = value[i + 1];

    if (inLineComment) {
      out += ch;
      if (ch === '\n') inLineComment = false;
      continue;
    }

    if (inBlockComment) {
      out += ch;
      if (ch === '*' && next === '/') {
        out += next;
        i++;
        inBlockComment = false;
      }
      continue;
    }

    if (quote) {
      out += ' ';
      if (escaped) {
        escaped = false;
      } else if (ch === '\\') {
        escaped = true;
      } else if (ch === quote) {
        quote = '';
      }
      continue;
    }

    if (ch === '/' && next === '/') {
      out += ch + next;
      i++;
      inLineComment = true;
      continue;
    }

    if (ch === '/' && next === '*') {
      out += ch + next;
      i++;
      inBlockComment = true;
      continue;
    }

    if (ch === '"' || ch === "'") {
      quote = ch;
      out += ' ';
      continue;
    }

    out += ch;
  }

  return out;
}

function stripSanitizerCalls(value) {
  const names = ['escapeAttr', 'escapeHtml', 'sanitizeHtml'];
  let out = '';
  let i = 0;

  while (i < value.length) {
    let matched = null;
    for (const name of names) {
      if (value.startsWith(name, i)) {
        matched = name;
        break;
      }
    }

    if (matched) {
      let cursor = i + matched.length;
      while (cursor < value.length && /\s/.test(value[cursor])) cursor++;
      if (value[cursor] !== '(') {
        out += value[i];
        i++;
        continue;
      }

      let depth = 0;
      let quote = '';
      let escaped = false;
      let inLineComment = false;
      let inBlockComment = false;
      cursor++;
      depth = 1;

      while (cursor < value.length) {
        const ch = value[cursor];
        const next = value[cursor + 1];

        if (inLineComment) {
          if (ch === '\n') inLineComment = false;
        } else if (inBlockComment) {
          if (ch === '*' && next === '/') {
            cursor++;
            inBlockComment = false;
          }
        } else if (quote) {
          if (escaped) {
            escaped = false;
          } else if (ch === '\\') {
            escaped = true;
          } else if (ch === quote) {
            quote = '';
          }
        } else {
          if (ch === '/' && next === '/') {
            inLineComment = true;
            cursor++;
          } else if (ch === '/' && next === '*') {
            inBlockComment = true;
            cursor++;
          } else if (ch === '"' || ch === "'" || ch === '`') {
            quote = ch;
          } else if (ch === '(') {
            depth++;
          } else if (ch === ')') {
            depth--;
            if (depth === 0) {
              cursor++;
              break;
            }
          }
        }

        cursor++;
      }

      i = cursor;
      continue;
    }

    out += value[i];
    i++;
  }

  return out;
}

function stripAndValidateTemplates(value) {
  let out = '';
  let unsafe = false;
  let i = 0;

  while (i < value.length) {
    const ch = value[i];
    const next = value[i + 1];

    if (ch === '`') {
      i++;
      let escaped = false;
      while (i < value.length) {
        const inner = value[i];
        const innerNext = value[i + 1];
        if (escaped) {
          escaped = false;
        } else if (inner === '\\') {
          escaped = true;
        } else if (inner === '$' && innerNext === '{') {
          let start = i + 2;
          let depth = 1;
          let cursor = start;
          let quote = '';
          let inLineComment = false;
          let inBlockComment = false;
          let escapedExpr = false;

          while (cursor < value.length) {
            const exprCh = value[cursor];
            const exprNext = value[cursor + 1];

            if (inLineComment) {
              if (exprCh === '\n') inLineComment = false;
            } else if (inBlockComment) {
              if (exprCh === '*' && exprNext === '/') {
                cursor++;
                inBlockComment = false;
              }
            } else if (quote) {
              if (escapedExpr) {
                escapedExpr = false;
              } else if (exprCh === '\\') {
                escapedExpr = true;
              } else if (exprCh === quote) {
                quote = '';
              }
            } else {
              if (exprCh === '/' && exprNext === '/') {
                inLineComment = true;
                cursor++;
              } else if (exprCh === '/' && exprNext === '*') {
                inBlockComment = true;
                cursor++;
              } else if (exprCh === '"' || exprCh === "'" || exprCh === '`') {
                quote = exprCh;
              } else if (exprCh === '{') {
                depth++;
              } else if (exprCh === '}') {
                depth--;
                if (depth === 0) {
                  const expr = value.slice(start, cursor);
                  if (isUnsafeInnerHtmlAssignment(expr)) unsafe = true;
                  i = cursor + 1;
                  break;
                }
              }
            }

            cursor++;
          }

          if (depth !== 0) {
            unsafe = true;
            i = value.length;
            break;
          }

          out += ' ';
          continue;
        } else if (inner === '`') {
          i++;
          break;
        }
        i++;
      }
      continue;
    }

    out += ch;
    i++;
  }

  return { stripped: out, unsafe };
}

function isEntireSanitizerCall(value) {
  const match = value.match(/^(escapeAttr|escapeHtml|sanitizeHtml)\s*\(/);
  if (!match) return false;

  let cursor = match[0].length;
  let depth = 1;
  let quote = '';
  let escaped = false;
  let inLineComment = false;
  let inBlockComment = false;

  while (cursor < value.length) {
    const ch = value[cursor];
    const next = value[cursor + 1];

    if (inLineComment) {
      if (ch === '\n') inLineComment = false;
    } else if (inBlockComment) {
      if (ch === '*' && next === '/') {
        cursor++;
        inBlockComment = false;
      }
    } else if (quote) {
      if (escaped) {
        escaped = false;
      } else if (ch === '\\') {
        escaped = true;
      } else if (ch === quote) {
        quote = '';
      }
    } else {
      if (ch === '/' && next === '/') {
        inLineComment = true;
        cursor++;
      } else if (ch === '/' && next === '*') {
        inBlockComment = true;
        cursor++;
      } else if (ch === '"' || ch === "'" || ch === '`') {
        quote = ch;
      } else if (ch === '(') {
        depth++;
      } else if (ch === ')') {
        depth--;
        if (depth === 0) {
          return value.slice(cursor + 1).trim() === '';
        }
      }
    }

    cursor++;
  }

  return false;
}

function isUnsafeInnerHtmlAssignment(rhs) {
  const trimmed = rhs.trim().replace(/;\s*$/, '').trim();
  if (!trimmed) return false;

  // Static literals are fine when explicitly allowed by the nearby comment.
  if (/^[`'"][\s\S]*[`'"]$/.test(trimmed) && !trimmed.includes('${')) return false;

  if (isEntireSanitizerCall(trimmed)) return false;

  const cleaned = stripSanitizerCalls(trimmed);
  const templateScan = stripAndValidateTemplates(cleaned);
  if (templateScan.unsafe) return true;

  const residual = stripQuotedStrings(templateScan.stripped).replace(/\s+/g, ' ');
  if (/^render[A-Za-z0-9_]*\s*\(/.test(residual.trim())) return false;

  const dynamicTokens = [
    'err', 'error', 'data', 'row', 'message', 'label',
    'title', 'value', 'status', 'response', 'result'
  ];

  return dynamicTokens.some(token => new RegExp(`\\b${token}\\b`).test(residual));
}

assert.strictEqual(isUnsafeInnerHtmlAssignment('escapeHtml(message);'), false);
assert.strictEqual(isUnsafeInnerHtmlAssignment('sanitizeHtml(html);'), false);
assert.strictEqual(isUnsafeInnerHtmlAssignment('escapeHtml(title) + row.untrustedHtml;'), true);
assert.strictEqual(isUnsafeInnerHtmlAssignment('escapeHtml(title) + String(message);'), true);
assert.strictEqual(isUnsafeInnerHtmlAssignment('sanitizeHtml(html) + message;'), true);
assert.strictEqual(isUnsafeInnerHtmlAssignment('`${escapeHtml(title)} ${message}`;'), true);

function isUnsafeInsertAdjacentHtml(line) {
  return line.includes('.insertAdjacentHTML(');
}

const violations = [];

for (const file of walk(root)) {
  const rel = path.relative(path.resolve(__dirname, '..'), file);
  const source = fs.readFileSync(file, 'utf8');
  const lines = source.split(/\r?\n/);

  for (const assignment of collectInnerHtmlAssignments(source)) {
    const lineNumber = source.slice(0, assignment.start).split(/\r?\n/).length;
    const allowedStatic = previousLineAllowsStaticHtml(lines, lineNumber - 1);
    if (isUnsafeInnerHtmlAssignment(assignment.rhs) && !allowedStatic) {
      violations.push(`${rel}:${lineNumber}: unsafe dynamic innerHTML usage`);
    }
  }

  lines.forEach((line, index) => {
    const allowedStatic = previousLineAllowsStaticHtml(lines, index);

    if (isUnsafeInsertAdjacentHtml(line) && !allowedStatic) {
      violations.push(`${rel}:${index + 1}: unsafe insertAdjacentHTML usage`);
    }
  });
}

assert.strictEqual(
  violations.length,
  0,
  [
    'Unsafe dynamic HTML construction detected.',
    'Use createElement/appendChild/replaceChildren and assign dynamic text with textContent.',
    '',
    ...violations
  ].join('\n')
);
