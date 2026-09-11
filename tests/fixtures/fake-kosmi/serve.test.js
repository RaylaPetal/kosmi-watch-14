const { test, after } = require("node:test");
const assert = require("node:assert/strict");
const http = require("node:http");
const { server, PORT } = require("./serve.js");

after(() => {
  server.close();
});

function get(path) {
  return new Promise((resolve, reject) => {
    http.get(`http://127.0.0.1:${PORT}${path}`, (res) => {
      const chunks = [];
      res.on("data", (c) => chunks.push(c));
      res.on("end", () => resolve({ status: res.statusCode, contentType: res.headers["content-type"], body: Buffer.concat(chunks) }));
    }).on("error", reject);
  });
}

test("serves every fixture page and asset with a 200 and the right content type", async (t) => {
  await new Promise((resolve) => server.listen(PORT, resolve));

  const cases = [
    ["/name-gate.html", "text/html"],
    ["/webcam-grid.html", "text/html"],
    ["/codec-error.html", "text/html"],
    ["/youtube-embed.html", "text/html"],
    ["/webrtc-loopback.html", "text/html"],
    ["/navigation-attempt.html", "text/html"],
    ["/shared-video.webm", "video/webm"],
    ["/cam-tile.webm", "video/webm"],
    ["/codec-error.mp4", "video/mp4"],
  ];

  for (const [path, expectedType] of cases) {
    const res = await get(path);
    assert.equal(res.status, 200, `${path} should return 200`);
    assert.equal(res.contentType, expectedType, `${path} content-type`);
    assert.ok(res.body.length > 0, `${path} should have a non-empty body`);
  }
});

test("root path serves the name gate as the default page", async () => {
  const res = await get("/");
  assert.equal(res.status, 200);
  assert.ok(res.body.toString("utf8").includes("Join the room"));
});

test("returns 404 for unknown paths", async () => {
  const res = await get("/nope.html");
  assert.equal(res.status, 404);
});

test("path traversal outside the fixtures directory is rejected", async () => {
  const res = await get("/../../../etc/passwd");
  assert.ok(res.status === 403 || res.status === 404);
});
