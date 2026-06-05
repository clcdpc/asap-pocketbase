const config = require(`${__hooks}/../lib/config.js`);

function applyPatronFrameHeaders(e) {
  const headers = e.response.header();
  headers.set("Content-Security-Policy", config.patronEmbedFrameAncestors(e.app));
  headers.del("X-Frame-Options");
}

function bytesToString(value) {
  if (!Array.isArray(value)) return String(value);
  var parts = [];
  var chunkSize = 4096;
  for (var i = 0; i < value.length; i += chunkSize) {
    parts.push(String.fromCharCode.apply(null, value.slice(i, i + chunkSize)));
  }
  return parts.join("");
}

function servePatronAsset(e, path) {
  if (path.indexOf("/patron/") !== 0) {
    return e.json(404, { message: "File not found." });
  }

  const relativePath = path.slice("/patron/".length);
  if (!relativePath || relativePath.indexOf("..") !== -1 || relativePath.indexOf("\\") !== -1 || relativePath.charAt(0) === "/") {
    return e.json(404, { message: "File not found." });
  }
  if (!/\.js$/.test(relativePath)) {
    return e.json(404, { message: "File not found." });
  }

  try {
    const content = bytesToString($os.readFile(`${__hooks}/../pb_public/patron/${relativePath}`));
    e.response.header().set("Content-Type", "application/javascript; charset=utf-8");
    return e.string(200, content);
  } catch (err) {
    return e.json(404, { message: "File not found." });
  }
}

function patronIndex(e) {
  const path = e.request && e.request.url ? String(e.request.url.path || "") : "";
  if (path !== "/patron" && path !== "/patron/") {
    return servePatronAsset(e, path);
  }
  applyPatronFrameHeaders(e);
  const html = $os.readFile(`${__hooks}/../pb_public/patron/index.html`);
  return e.html(200, bytesToString(html));
}

module.exports = {
  patronIndex: patronIndex,
  servePatronAsset: servePatronAsset,
  applyPatronFrameHeaders: applyPatronFrameHeaders
};
