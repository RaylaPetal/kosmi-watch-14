// Minimal static file server for the fake-kosmi fixtures (design.md §16: "served from a local
// static server so CI can run the agent deterministically"). No dependencies — just Node's
// built-in http/fs — since these are static files with no build step.
const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");

const ROOT = __dirname;
const PORT = process.env.PORT ? Number(process.env.PORT) : 8973;

const MIME_TYPES = {
  ".html": "text/html",
  ".js": "text/javascript",
  ".webm": "video/webm",
  ".mp4": "video/mp4",
};

const server = http.createServer((req, res) => {
  const urlPath = decodeURIComponent(req.url.split("?")[0]);
  const filePath = path.join(ROOT, urlPath === "/" ? "/name-gate.html" : urlPath);

  if (!filePath.startsWith(ROOT)) {
    res.writeHead(403);
    res.end("Forbidden");
    return;
  }

  fs.readFile(filePath, (err, data) => {
    if (err) {
      res.writeHead(404);
      res.end("Not found: " + urlPath);
      return;
    }

    const ext = path.extname(filePath);
    res.writeHead(200, { "Content-Type": MIME_TYPES[ext] || "application/octet-stream" });
    res.end(data);
  });
});

if (require.main === module) {
  server.listen(PORT, () => {
    console.log(`fake-kosmi fixtures serving at http://127.0.0.1:${PORT}/`);
  });
}

module.exports = { server, PORT };
