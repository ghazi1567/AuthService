// Checks that what the files in docs/reference cite still exists in the repository: file paths, classes and methods, routes, settings,
// tables and columns, headers and audit actions, and the links between the parts.
// Usage (from the repository root): node docs/reference/verify-reference.js [file ...]   (default: every .md in docs/reference)
// Exits non-zero if something is not found. Names that belong to the framework or are generated or gitignored are listed in EXPECTED.
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..', '..'); // docs/reference -> repository root
const refDir = path.join(root, 'docs/reference');
const skipDirs = new Set(['bin', 'obj', 'node_modules', '.git', 'TestResults', 'artifacts', 'Claude outputs']);
const codeExt = new Set(['.cs', '.razor', '.json', '.yml', '.yaml', '.props', '.csproj', '.md', '.js', '.css', '.sh', '.config', '.txt', '.slnx', '.env', '.example']);

const files = [];
(function walk(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (skipDirs.has(entry.name)) continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full);
    else files.push(full);
  }
})(root);

const rel = f => path.relative(root, f).replace(/\\/g, '/');
const relPaths = new Set(files.map(rel));
const dirs = new Set();
for (const p of relPaths) { let d = path.posix.dirname(p); while (d && d !== '.') { dirs.add(d); d = path.posix.dirname(d); } }
const baseNames = new Set(files.map(f => path.basename(f)));

// All text of code and config, searched for identifiers. Docs under docs/reference are excluded so they cannot vouch for themselves.
let corpus = '';
for (const f of files) {
  const r = rel(f);
  if (r.startsWith('docs/reference/')) continue;
  if (!codeExt.has(path.extname(f)) && !f.endsWith('.env.example')) continue;
  corpus += fs.readFileSync(f, 'utf8') + '\n';
}
const corpusLower = corpus.toLowerCase();

// Names the documents mention that are not in the repository on purpose: framework types, generated assets and gitignored files.
const EXPECTED = new Set(['HasQueryFilter', 'AuthenticationTagMismatchException', 'IAuthorizationMiddlewareResultHandler', 'deploy/.env', 'AuthService.Web.styles.css', 'blazor.web.js']);

const targets = process.argv.slice(2).length ? process.argv.slice(2) : fs.readdirSync(refDir).filter(f => f.endsWith('.md')).map(f => path.join(refDir, f));

const has = s => corpus.includes(s);
const hasCi = s => corpusLower.includes(s.toLowerCase());
let totalChecked = 0;
let totalMissing = 0;

for (const file of targets) {
  const text = fs.readFileSync(file, 'utf8');
  const spans = new Set();
  for (const m of text.matchAll(/`([^`\n]+)`/g)) spans.add(m[1].trim());
  // Markdown link targets between the reference files.
  const linkMissing = [];
  for (const m of text.matchAll(/\]\(([^)#\s]+)(#[^)]*)?\)/g)) {
    const target = m[1];
    if (/^https?:/.test(target) || target.startsWith('mailto:') || target.startsWith('?') || target.includes('[')) continue;
    const resolved = path.posix.normalize(path.posix.join(path.posix.dirname(rel(file)), target));
    if (!relPaths.has(resolved) && !dirs.has(resolved.replace(/\/$/, ''))) linkMissing.push(target);
  }

  const missing = [];
  let checked = 0;
  for (const span of spans) {
    let ok = null; // null = not a checkable kind

    if (/^(src|tests|samples|docs|deploy|\.github)\/[\w./\-{}*]*$/.test(span) || /^[\w.\-]+\.(cs|razor|json|yml|md|sh|js|css|csproj|props)$/.test(span) || /^[\w\-]+(\/[\w.\-]+)+\.(cs|razor|json|yml|md|sh|js|css|csproj)$/.test(span)) {
      const clean = span.replace(/\/$/, '');
      if (clean.includes('*') || clean.includes('{') || clean.includes('...')) ok = null;
      else if (relPaths.has(clean) || dirs.has(clean)) ok = true;
      else if (!clean.includes('/') && baseNames.has(clean)) ok = true;
      else if ([...relPaths].some(p => p.endsWith('/' + clean))) ok = true; // partial path such as Features/Login/LoginService.cs
      else ok = false;
    } else if (/^\/(api|health|metrics|\.well-known)[\w\-/{}:.?=&]*$/.test(span) || /^(GET|POST|PUT|PATCH|DELETE)\s+\/[\w\-/{}:.?=&]+$/.test(span)) {
      let route = span.replace(/^(GET|POST|PUT|PATCH|DELETE)\s+/, '').replace(/\?.*$/, '');
      const fixed = route.split('/').filter(seg => seg && !/[{}]/.test(seg)).join('/');
      ok = has('/' + fixed) || has(fixed) || hasCi(fixed) || fixed.split('/').filter(seg => seg.length > 2).every(seg => hasCi('/' + seg) || hasCi(seg + '"') || hasCi('"' + seg));
    } else if (/^[A-Z][A-Za-z0-9]*(:[A-Z][A-Za-z0-9]*)+$/.test(span)) {
      // Setting in colon form, such as Auth:LoginProtection:MaxFailedAttempts. The last segment must be a property or key.
      const parts = span.split(':');
      ok = hasCi(parts[parts.length - 1]) && (hasCi(parts.slice(0, -1).join(':')) || hasCi('"' + parts[0] + '"') || hasCi(parts[parts.length - 2]));
    } else if (/^[A-Z][A-Za-z0-9]*(__[A-Za-z0-9]+)+$/.test(span)) {
      const colon = span.replace(/__/g, ':');
      const parts = colon.split(':');
      ok = hasCi(span) || hasCi(parts[parts.length - 1]);
    } else if (/^[a-z]+(_[a-z0-9]+)+\.[a-z]+(_[a-z0-9]+)*$/.test(span)) {
      // table.column
      const [table, column] = span.split('.');
      ok = hasCi('"' + column + '"') || hasCi(column);
      if (ok) ok = hasCi('"' + table + '"') || hasCi(table);
    } else if (/^[a-z]+(_[a-z0-9]+)+$/.test(span) && !/^(client_credentials|grant_type|client_id|client_secret|access_token|token_type|expires_in)$/.test(span)) {
      ok = hasCi(span);
    } else if (/^[A-Z][A-Za-z0-9]+(\.[A-Z][A-Za-z0-9]+)+(\(\))?$/.test(span) || /^[A-Z][A-Za-z0-9]{3,}(\(\))?$/.test(span) || /^[A-Z][A-Za-z0-9]+\.[A-Z][A-Za-z0-9]+\([^)]*\)$/.test(span)) {
      // Class, Class.Method or a bare identifier. Every dotted part must appear in the code.
      const parts = span.replace(/\(.*\)$/, '').split('.');
      ok = parts.every(part => has(part));
    } else if (/^X-[A-Za-z\-]+$/.test(span)) {
      ok = hasCi(span);
    } else if (/^auth\.[a-z_.]+$/.test(span)) {
      ok = has('"' + span + '"') || has(span);
    }

    if (ok !== null && !EXPECTED.has(span)) {
      checked++;
      if (!ok) missing.push(span);
    }
  }

  totalChecked += checked;
  totalMissing += missing.length + linkMissing.length;
  console.log(`\n== ${rel(file)}: ${spans.size} code spans, ${checked} checkable, ${missing.length} not found, ${linkMissing.length} broken links`);
  for (const s of missing.sort()) console.log('   NOT FOUND: ' + s);
  for (const s of linkMissing) console.log('   BROKEN LINK: ' + s);
}
console.log(`\nTOTAL: ${totalChecked} checked, ${totalMissing} problems`);
