const { test } = require("node:test");
const assert = require("node:assert/strict");
const { JSDOM } = require("jsdom");
const agent = require("../../WatchAlong.Renderer/Assets/kosmi-agent.js");

function makeDom(html) {
  return new JSDOM(html, { pretendToBeVisual: true });
}

const profile = {
  stateProbes: {
    joinGate: { any: ["input[name='nickname']", "button:has-text('Join')"] },
    roomNotFound: { textMatches: ["room (was )?not found", "doesn't exist"] },
    loginRequired: { any: ["[data-testid='login-modal']"] },
  },
  joinGate: { nameInput: "input[name='nickname']", submit: "button[type='submit']" },
  participantTileSelectors: [".participant", "[class*='webcam']"],
};

// ---- :has-text() ----

test(":has-text() matches elements by tag and text content", () => {
  const dom = makeDom("<button>Join now</button><button>Leave</button>");
  const matches = agent.querySelectorAllExt(dom.window.document, "button:has-text('Join')");
  assert.equal(matches.length, 1);
  assert.equal(matches[0].textContent, "Join now");
});

test(":has-text() with empty tag selector matches any element containing the text", () => {
  const dom = makeDom("<span>Room not found</span>");
  const matches = agent.querySelectorAllExt(dom.window.document, ":has-text('not found')");
  // textContent bubbles up, so ancestors of the matching span (html, body) match too —
  // that's expected: this selector form is used only as a boolean "does this text appear
  // anywhere" probe (matchesSelectorGroup), not to pick out a single element.
  assert.ok(matches.length >= 1);
  assert.ok(matches.some((el) => el.tagName === "SPAN"));
});

test("plain selectors without :has-text() fall back to normal querySelectorAll", () => {
  const dom = makeDom("<input name='nickname'>");
  const matches = agent.querySelectorAllExt(dom.window.document, "input[name='nickname']");
  assert.equal(matches.length, 1);
});

// ---- 5.1 page-state detection ----

test("detects JoinGate when the name input is present", () => {
  const dom = makeDom("<input name='nickname'>");
  const state = agent.detectPageState(dom.window.document, profile, "complete");
  assert.equal(state, "JoinGate");
});

test("detects RoomNotFound via text match, ahead of JoinGate", () => {
  const dom = makeDom("<div>Sorry, this room was not found</div><input name='nickname'>");
  const state = agent.detectPageState(dom.window.document, profile, "complete");
  assert.equal(state, "RoomNotFound");
});

test("detects LoginRequired ahead of RoomNotFound and JoinGate", () => {
  const dom = makeDom(
    "<div data-testid='login-modal'></div><div>room not found</div><input name='nickname'>"
  );
  const state = agent.detectPageState(dom.window.document, profile, "complete");
  assert.equal(state, "LoginRequired");
});

test("reports Loading while the document is not yet complete and nothing else matches", () => {
  const dom = makeDom("<div>hello</div>");
  const state = agent.detectPageState(dom.window.document, profile, "loading");
  assert.equal(state, "Loading");
});

test("reports InRoom once loaded and no gate/error probe matches", () => {
  const dom = makeDom("<div>welcome to the room</div>");
  const state = agent.detectPageState(dom.window.document, profile, "complete");
  assert.equal(state, "InRoom");
});

// ---- 5.9 Unknown-state tracker ----

test("UnknownStateTracker fires exactly once after crossing the threshold", () => {
  const tracker = new agent.UnknownStateTracker(10000);
  assert.equal(tracker.observe("Unknown", 0), false);
  assert.equal(tracker.observe("Unknown", 5000), false);
  assert.equal(tracker.observe("Unknown", 10000), true);
  assert.equal(tracker.observe("Unknown", 15000), false); // doesn't re-fire every tick
});

test("UnknownStateTracker resets once state leaves Unknown", () => {
  const tracker = new agent.UnknownStateTracker(10000);
  tracker.observe("Unknown", 0);
  tracker.observe("Unknown", 10000); // fires
  tracker.observe("InRoom", 11000); // recovered
  assert.equal(tracker.observe("Unknown", 11500), false); // fresh streak, not yet at threshold
  assert.equal(tracker.observe("Unknown", 21500), true); // fires again after a fresh 10s
});

// ---- 5.9 DOM outline (text stripped) ----

test("buildDomOutline captures tag/id/class structure without any text content", () => {
  const dom = makeDom(`<div id="root" class="a b"><span>secret text</span><video></video></div>`);
  const root = dom.window.document.getElementById("root");

  const outline = agent.buildDomOutline(root);

  assert.equal(outline.tag, "div");
  assert.equal(outline.id, "root");
  assert.deepEqual(outline.classes, ["a", "b"]);
  assert.equal(outline.children.length, 2);
  assert.equal(outline.children[0].tag, "span");
  assert.equal(JSON.stringify(outline).includes("secret text"), false);
});

test("buildDomOutline stops recursing past a depth limit rather than looping forever", () => {
  const dom = makeDom("<div id='a'></div>");
  const document = dom.window.document;
  let el = document.getElementById("a");
  for (let i = 0; i < 20; i++) {
    const child = document.createElement("div");
    el.appendChild(child);
    el = child;
  }

  const outline = agent.buildDomOutline(document.getElementById("a"));
  // Just needs to terminate and produce a bounded structure — no assertion on exact depth.
  assert.ok(outline);
});

// ---- 5.2 join-gate automation ----

test("tryJoinGate sets the value through the native setter and clicks submit", () => {
  const dom = makeDom("<input name='nickname'><button type='submit'></button>");
  const { window } = dom;
  const input = window.document.querySelector("input");
  let sawInputEvent = false;
  input.addEventListener("input", () => {
    sawInputEvent = true;
  });
  let clicked = false;
  window.document.querySelector("button").addEventListener("click", () => {
    clicked = true;
  });

  const ok = agent.tryJoinGate(window, window.document, profile, "Ray (in-game)");

  assert.equal(ok, true);
  assert.equal(input.value, "Ray (in-game)");
  assert.equal(sawInputEvent, true);
  assert.equal(clicked, true);
});

test("tryJoinGate returns false when the elements aren't present yet", () => {
  const dom = makeDom("<div>loading...</div>");
  const ok = agent.tryJoinGate(dom.window, dom.window.document, profile, "Ray");
  assert.equal(ok, false);
});

// ---- 5.3 primary-media scoring + hysteresis ----

test("a larger shared video outscores a small participant camera tile", () => {
  const dom = makeDom(`
    <div class="participant" style="position:absolute;left:0;top:0;width:100px;height:100px;">
      <video id="cam"></video>
    </div>
    <video id="shared" style="position:absolute;left:0;top:0;width:1280px;height:720px;"></video>
  `);
  const { document } = dom.window;
  // jsdom doesn't compute layout, so stub getBoundingClientRect for each element under test.
  stubRect(document.getElementById("cam"), 0, 0, 100, 100);
  stubRect(document.getElementById("shared"), 0, 0, 1280, 720);

  const best = agent.pickBestCandidate(document, profile, 1280, 720);
  assert.equal(best.id, "shared");
});

test("participant tile selector applies a penalty even if briefly larger", () => {
  const dom = makeDom(`
    <div class="participant"><video id="cam"></video></div>
    <video id="shared"></video>
  `);
  const { document } = dom.window;
  stubRect(document.getElementById("cam"), 0, 0, 500, 500);
  stubRect(document.getElementById("shared"), 0, 0, 480, 480);

  const best = agent.pickBestCandidate(document, profile, 1280, 720);
  assert.equal(best.id, "shared"); // penalty flips it despite the raw-area difference being small
});

test("PrimarySelector requires 1.5s of a consistent new winner before switching", () => {
  const selector = new agent.PrimarySelector(1500);
  const a = { id: "a" };
  const b = { id: "b" };

  selector.consider(a, 0);
  selector.consider(a, 100); // still a — but current starts null, so first call just tracks pending
  const afterSettle = selector.consider(a, 1600);
  assert.equal(afterSettle, a);

  const flicker = selector.consider(b, 1700); // b appears briefly
  assert.equal(flicker, a); // hasn't switched yet
  const stillA = selector.consider(b, 2500); // less than 1500ms since b first appeared
  assert.equal(stillA, a);
  const switched = selector.consider(b, 3300); // now >= 1500ms of consistent b
  assert.equal(switched, b);
});

function stubRect(el, x, y, w, h) {
  el.getBoundingClientRect = () => ({
    left: x,
    top: y,
    right: x + w,
    bottom: y + h,
    width: w,
    height: h,
  });
}

// ---- 5.4 theater mode ----

test("applyTheaterMode marks the primary and its ancestors without moving any DOM nodes", () => {
  const dom = makeDom(`
    <div id="grandparent"><div id="parent"><video id="primary"></video><div id="sibling"></div></div></div>
  `);
  const { document } = dom.window;
  const primary = document.getElementById("primary");
  const parent = document.getElementById("parent");
  const grandparent = document.getElementById("grandparent");
  const sibling = document.getElementById("sibling");

  agent.applyTheaterMode(document, primary);

  assert.equal(primary.hasAttribute("data-wa-primary"), true);
  assert.equal(parent.hasAttribute("data-wa-ancestor"), true);
  assert.equal(grandparent.hasAttribute("data-wa-ancestor"), true);
  assert.equal(sibling.hasAttribute("data-wa-ancestor"), false);
  assert.equal(document.documentElement.classList.contains("wa-theater"), true);

  // Structure is untouched: same parent references, same child count.
  assert.equal(primary.parentElement, parent);
  assert.equal(parent.parentElement, grandparent);
  assert.equal(parent.children.length, 2);
});

test("applyTheaterMode clears stale markers from a previous primary", () => {
  const dom = makeDom(`<video id="old"></video><video id="new"></video>`);
  const { document } = dom.window;
  const oldEl = document.getElementById("old");
  const newEl = document.getElementById("new");

  agent.applyTheaterMode(document, oldEl);
  agent.applyTheaterMode(document, newEl);

  assert.equal(oldEl.hasAttribute("data-wa-primary"), false);
  assert.equal(newEl.hasAttribute("data-wa-primary"), true);
});

// ---- 5.5 content rect / letterboxing ----

test("content rect matches the element rect when there's no letterboxing to account for", () => {
  const dom = makeDom("<video id='v'></video>");
  const el = dom.window.document.getElementById("v");
  stubRect(el, 10, 20, 800, 600);
  const rect = agent.computeContentRect(el);
  assert.deepEqual(rect, { x: 10, y: 20, w: 800, h: 600 });
});

test("content rect accounts for pillarboxing when the video is narrower than the element", () => {
  const dom = makeDom("<video id='v'></video>");
  const el = dom.window.document.getElementById("v");
  stubRect(el, 0, 0, 800, 600); // element is 4:3-ish
  Object.defineProperty(el, "videoWidth", { value: 400, configurable: true });
  Object.defineProperty(el, "videoHeight", { value: 600, configurable: true }); // narrow video, letterboxed left/right

  const rect = agent.computeContentRect(el);
  assert.equal(rect.h, 600);
  assert.equal(rect.w, 400); // 600 * (400/600)
  assert.equal(rect.x, 200); // (800-400)/2
  assert.equal(rect.y, 0);
});

// ---- 5.8 voice muting ----

test("muteAllExceptPrimary mutes every media element except the chosen primary", () => {
  const dom = makeDom(`<video id="a"></video><video id="primary"></video><audio id="c"></audio>`);
  const { document } = dom.window;
  const primary = document.getElementById("primary");

  agent.muteAllExceptPrimary(document, primary);

  assert.equal(document.getElementById("a").muted, true);
  assert.equal(document.getElementById("c").muted, true);
  assert.equal(primary.muted, false);
});

test("muteAllExceptPrimary re-applies to elements added after the fact when called again", () => {
  const dom = makeDom(`<video id="primary"></video>`);
  const { document } = dom.window;
  const primary = document.getElementById("primary");
  agent.muteAllExceptPrimary(document, primary);

  const newVideo = document.createElement("video");
  newVideo.id = "late";
  document.body.appendChild(newVideo);
  agent.muteAllExceptPrimary(document, primary); // simulates the MutationObserver re-applying

  assert.equal(document.getElementById("late").muted, true);
});

// ---- 5.7 payload shapes ----

test("buildMediaState reports html5 fields including currentTime and src", () => {
  const dom = makeDom("<video id='v' src='https://example/video.webm'></video>");
  const el = dom.window.document.getElementById("v");
  Object.defineProperty(el, "currentTime", { value: 12.5, configurable: true });
  Object.defineProperty(el, "paused", { value: false, configurable: true });

  const state = agent.buildMediaState("html5", el, { x: 0, y: 0, w: 1280, h: 720 }, null);

  assert.equal(state.type, "media");
  assert.equal(state.kind, "html5");
  assert.equal(state.paused, false);
  assert.equal(state.currentTime, 12.5);
  assert.deepEqual(state.rect, [0, 0, 1280, 720]);
});

test("buildMediaState omits currentTime for iframe kind (cross-origin, unreadable)", () => {
  const dom = makeDom("<iframe id='f' src='https://youtube.com/embed/x'></iframe>");
  const el = dom.window.document.getElementById("f");
  const state = agent.buildMediaState("iframe", el, { x: 0, y: 0, w: 1280, h: 720 }, null);

  assert.equal(state.currentTime, null);
  assert.equal(state.src, "https://youtube.com/embed/x");
});

test("buildRoomInfo carries title, members and optional presenter", () => {
  const info = agent.buildRoomInfo("Movie night", ["Ray", "Aya (in-game)"], "Ray");
  assert.deepEqual(info, { type: "room", title: "Movie night", members: ["Ray", "Aya (in-game)"], presenter: "Ray" });
});

// ---- session-roster announcement (piggybacked on Kosmi's own chat) ----

test("buildAnnouncement formats a recognizable join line", () => {
  assert.equal(agent.buildAnnouncement("kaede"), "[WatchAlong] kaede joined");
});

test("findAnnouncedNames extracts names from surrounding chat text, deduped", () => {
  const dom = makeDom(
    "<div>hey everyone</div>" +
    "<div>[WatchAlong] kaede joined</div>" +
    "<div>lol hi</div>" +
    "<div>[WatchAlong] Ray (in-game) joined</div>" +
    "<div>[WatchAlong] kaede joined</div>"
  );

  const names = agent.findAnnouncedNames(dom.window.document);
  assert.deepEqual(names, ["kaede", "Ray (in-game)"]);
});

test("findAnnouncedNames returns an empty list when no announcement is present", () => {
  const dom = makeDom("<div>just a normal chat message</div>");
  assert.deepEqual(agent.findAnnouncedNames(dom.window.document), []);
});

test("sendChatMessage returns false when the profile has no chat input selector", () => {
  const dom = makeDom("<div contenteditable='true'></div>");
  const ok = agent.sendChatMessage(dom.window, dom.window.document, { chat: {} }, "hi");
  assert.equal(ok, false);
});

test("sendChatMessage returns false when the input element isn't found", () => {
  const dom = makeDom("<div>no chat panel open</div>");
  const ok = agent.sendChatMessage(dom.window, dom.window.document, { chat: { input: "[data-uitag='chat-message-input']" } }, "hi");
  assert.equal(ok, false);
});

test("sendChatMessage types the text into the input and submits with Enter", () => {
  const dom = makeDom("<div contenteditable='true' data-uitag='chat-message-input'></div>");
  const input = dom.window.document.querySelector("[data-uitag='chat-message-input']");

  let sawEnter = false;
  input.addEventListener("keydown", (e) => { if (e.key === "Enter") sawEnter = true; });

  const ok = agent.sendChatMessage(dom.window, dom.window.document, { chat: { input: "[data-uitag='chat-message-input']" } }, "[WatchAlong] kaede joined");

  assert.equal(ok, true);
  assert.equal(input.textContent, "[WatchAlong] kaede joined");
  assert.equal(sawEnter, true);
});
