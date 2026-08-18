#!/usr/bin/env node
// Minimal static server for a published MTile.Web wwwroot.
//
//   node MTile.Web/smoke/serve.js <wwwroot-dir> [port]
//
// Exists because the Blazor/KNI build will not boot off a naive file server: .wasm and .dat
// need their real MIME types, and the AOT runtime is fetched as .wasm/.js from _framework.
// The python one-liner in smoke/README.md is the Linux equivalent.
const http = require('http'), fs = require('fs'), path = require('path');

const root = path.resolve(process.argv[2] || '.');
const port = Number(process.argv[3] || 8080);
const MIME = {
  '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript',
  '.css': 'text/css', '.json': 'application/json', '.wasm': 'application/wasm',
  '.dll': 'application/octet-stream', '.dat': 'application/octet-stream',
  '.pdb': 'application/octet-stream', '.blat': 'application/octet-stream',
  '.png': 'image/png', '.jpg': 'image/jpeg', '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon', '.ogg': 'audio/ogg', '.xnb': 'application/octet-stream',
  '.woff2': 'font/woff2', '.txt': 'text/plain',
};

http.createServer((req, res) => {
  let rel = decodeURIComponent(req.url.split('?')[0]);
  if (rel.endsWith('/')) rel += 'index.html';
  const file = path.join(root, rel);
  if (!file.startsWith(root)) { res.writeHead(403).end(); return; }
  fs.readFile(file, (err, buf) => {
    if (err) { res.writeHead(404).end('not found: ' + rel); return; }
    res.writeHead(200, {
      'Content-Type': MIME[path.extname(file).toLowerCase()] || 'application/octet-stream',
      'Cache-Control': 'no-cache',
    });
    res.end(buf);
  });
}).listen(port, () => console.log(`serving ${root} on http://127.0.0.1:${port}/`));
