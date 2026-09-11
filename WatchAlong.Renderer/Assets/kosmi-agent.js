/**
 * kosmi-agent.js — injected into the Kosmi page at document start (design.md §6.4).
 *
 * Every exported function here is pure with respect to its inputs (a `document`/root node,
 * a schema-validated selector profile, and/or a clock) so it can be unit tested with jsdom
 * without a real browser. The bottom of the file wires these into the live page — reading
 * `window.WatchAlongProfile`, polling on a timer, and posting to .NET via
 * `CefSharp.PostMessage` — none of which is exercised by the unit tests, only by a live CEF
 * session (see tasks.md 10.3).
 *
 * Selectors and text patterns from the profile are only ever used as CSS selectors or RegExp
 * source strings — never `eval`'d — per specs/kosmi-session/spec.md "Selector-driven behavior
 * is data, never executable code".
 */
(function (root, factory) {
  if (typeof module !== "undefined" && module.exports) {
    module.exports = factory();
  } else {
    root.WatchAlongAgent = factory();
  }
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  // ---- 5.6: :has-text() custom selector extension ----

  var HAS_TEXT_RE = /^(.*):has-text\('([^']*)'\)$/;

  function querySelectorAllExt(root, selector) {
    var match = selector.match(HAS_TEXT_RE);
    if (!match) {
      return toArray(root.querySelectorAll(selector));
    }

    var tagSelector = match[1].trim() || "*";
    var textPattern = new RegExp(match[2], "i");
    return toArray(root.querySelectorAll(tagSelector)).filter(function (el) {
      return textPattern.test(el.textContent || "");
    });
  }

  function toArray(nodeList) {
    return Array.prototype.slice.call(nodeList);
  }

  // ---- 5.1: page-state detection ----

  function matchesSelectorGroup(root, group) {
    if (!group) return false;

    if (group.any) {
      for (var i = 0; i < group.any.length; i++) {
        if (querySelectorAllExt(root, group.any[i]).length > 0) return true;
      }
    }

    if (group.textMatches) {
      var text = (root.body ? root.body.textContent : root.textContent) || "";
      for (var j = 0; j < group.textMatches.length; j++) {
        if (new RegExp(group.textMatches[j], "i").test(text)) return true;
      }
    }

    return false;
  }

  /** Loading -> JoinGate -> InRoom, with LoginRequired/RoomNotFound taking priority. */
  function detectPageState(root, profile, documentReadyState) {
    if (matchesSelectorGroup(root, profile.stateProbes.loginRequired)) return "LoginRequired";
    if (matchesSelectorGroup(root, profile.stateProbes.roomNotFound)) return "RoomNotFound";
    if (matchesSelectorGroup(root, profile.stateProbes.joinGate)) return "JoinGate";
    if (documentReadyState !== "complete") return "Loading";
    return "InRoom";
  }

  // ---- 5.9: Unknown-state-for-too-long -> debug snapshot trigger ----

  function UnknownStateTracker(thresholdMs) {
    this.thresholdMs = thresholdMs || 10000;
    this.unknownSince = null;
    this.fired = false;
  }

  /** Returns true exactly once per continuous Unknown streak, when it first crosses the threshold. */
  UnknownStateTracker.prototype.observe = function (state, now) {
    if (state !== "Unknown") {
      this.unknownSince = null;
      this.fired = false;
      return false;
    }

    if (this.unknownSince === null) this.unknownSince = now;

    if (!this.fired && now - this.unknownSince >= this.thresholdMs) {
      this.fired = true;
      return true;
    }

    return false;
  };

  // ---- 5.2: join-gate automation ----

  function setNativeInputValue(win, input, value) {
    var proto = win.HTMLInputElement.prototype;
    var setter = Object.getOwnPropertyDescriptor(proto, "value").set;
    setter.call(input, value);
    input.dispatchEvent(new win.Event("input", { bubbles: true }));
  }

  /** Returns true if both elements were found and the join was attempted. */
  function tryJoinGate(win, root, profile, displayName) {
    var nameInput = root.querySelector(profile.joinGate.nameInput);
    var submit = root.querySelector(profile.joinGate.submit);
    if (!nameInput || !submit) return false;

    setNativeInputValue(win, nameInput, displayName);
    submit.click();
    return true;
  }

  // ---- 5.3: primary-media candidate scoring, with hysteresis ----

  function visibleArea(rect, viewportW, viewportH) {
    var w = Math.max(0, Math.min(rect.right, viewportW) - Math.max(rect.left, 0));
    var h = Math.max(0, Math.min(rect.bottom, viewportH) - Math.max(rect.top, 0));
    return { w: w, h: h, area: w * h };
  }

  function isInsideAnySelector(el, selectors) {
    for (var i = 0; i < selectors.length; i++) {
      if (el.matches && el.matches(selectors[i])) return true;
      if (el.closest && el.closest(selectors[i])) return true;
    }
    return false;
  }

  function scoreElement(el, profile, viewportW, viewportH) {
    var rect = el.getBoundingClientRect();
    var visible = visibleArea(rect, viewportW, viewportH);
    var score = visible.area;

    if (el.tagName === "VIDEO") {
      var tracks = el.srcObject && el.srcObject.getVideoTracks ? el.srcObject.getVideoTracks() : [];
      if (tracks.length > 0 && tracks[0].readyState === "live") score += 1000000;
      if (el.videoWidth >= 640) score += 500000;
    }

    if (el.tagName === "IFRAME") {
      var src = el.getAttribute("src") || "";
      if (/youtube|twitch|vimeo/i.test(src)) score += 500000;
    }

    if (isInsideAnySelector(el, profile.participantTileSelectors || [])) score -= 2000000;
    if (visible.w < 200 && visible.h < 200 && Math.abs(visible.w - visible.h) < 40) score -= 500000;

    return score;
  }

  function pickBestCandidate(root, profile, viewportW, viewportH) {
    var candidates = toArray(root.querySelectorAll("video, iframe, canvas"));
    var best = null;
    var bestScore = -Infinity;

    for (var i = 0; i < candidates.length; i++) {
      var s = scoreElement(candidates[i], profile, viewportW, viewportH);
      if (s > bestScore) {
        bestScore = s;
        best = candidates[i];
      }
    }

    return best;
  }

  /** Holds the current primary element and only switches after 1.5s of a different winner (design.md §6.4 item 3). */
  function PrimarySelector(hysteresisMs) {
    this.hysteresisMs = hysteresisMs || 1500;
    this.current = null;
    this.pendingCandidate = null;
    this.pendingSince = null;
  }

  PrimarySelector.prototype.consider = function (candidate, now) {
    if (candidate === this.current) {
      this.pendingCandidate = null;
      this.pendingSince = null;
      return this.current;
    }

    if (candidate !== this.pendingCandidate) {
      this.pendingCandidate = candidate;
      this.pendingSince = now;
      return this.current;
    }

    if (now - this.pendingSince >= this.hysteresisMs) {
      this.current = candidate;
      this.pendingCandidate = null;
      this.pendingSince = null;
    }

    return this.current;
  };

  // ---- 5.4: theater mode (CSS-driven, DOM nodes never moved) ----

  var THEATER_CSS =
    "html.wa-theater body * { visibility: hidden !important; }\n" +
    "html.wa-theater [data-wa-primary],\n" +
    "html.wa-theater [data-wa-primary] * { visibility: visible !important; }\n" +
    "html.wa-theater [data-wa-ancestor] { transform: none !important; filter: none !important;\n" +
    "                                     contain: none !important; overflow: visible !important; }\n" +
    "html.wa-theater [data-wa-primary] { position: fixed !important; inset: 0 !important;\n" +
    "  width: 100vw !important; height: 100vh !important; max-width: none !important;\n" +
    "  z-index: 2147483647 !important; object-fit: contain !important; background: #000 !important; }\n" +
    "html.wa-theater, html.wa-theater body { background: #000 !important; }\n";

  function injectTheaterStylesheet(doc) {
    if (doc.getElementById("wa-theater-style")) return;
    var style = doc.createElement("style");
    style.id = "wa-theater-style";
    style.textContent = THEATER_CSS;
    doc.head.appendChild(style);
  }

  function applyTheaterMode(doc, primaryEl) {
    injectTheaterStylesheet(doc);

    toArray(doc.querySelectorAll("[data-wa-primary]")).forEach(function (el) {
      el.removeAttribute("data-wa-primary");
    });
    toArray(doc.querySelectorAll("[data-wa-ancestor]")).forEach(function (el) {
      el.removeAttribute("data-wa-ancestor");
    });

    primaryEl.setAttribute("data-wa-primary", "");
    var ancestor = primaryEl.parentElement;
    while (ancestor) {
      ancestor.setAttribute("data-wa-ancestor", "");
      ancestor = ancestor.parentElement;
    }

    doc.documentElement.classList.add("wa-theater");
  }

  // ---- 5.5: content rectangle, accounting for object-fit letterboxing ----

  function computeContentRect(el) {
    var rect = el.getBoundingClientRect();
    var contentRect;

    if (el.tagName !== "VIDEO" || !el.videoWidth || !el.videoHeight) {
      contentRect = { x: rect.left, y: rect.top, w: rect.width, h: rect.height };
    } else {
      // Correct for CSS object-fit letterboxing *within* the <video> element's own box (its native
      // pixel aspect vs. its styled box aspect) — unrelated to the controls-bar expansion below.
      var elementAspect = rect.width / rect.height;
      var videoAspect = el.videoWidth / el.videoHeight;

      if (videoAspect > elementAspect) {
        var displayedH = rect.width / videoAspect;
        contentRect = { x: rect.left, y: rect.top + (rect.height - displayedH) / 2, w: rect.width, h: displayedH };
      } else {
        var displayedW = rect.height * videoAspect;
        contentRect = { x: rect.left + (rect.width - displayedW) / 2, y: rect.top, w: displayedW, h: rect.height };
      }
    }

    // Custom player skins (Kosmi included) commonly render their own controls bar as an overlay
    // sibling/child positioned below the media element within a wrapping container, not inside the
    // media element's own box — cropping to just that element's (letterbox-corrected) rect cuts the
    // controls bar off. When the immediate parent is a reasonably-sized wrapper around this element
    // (not some much larger unrelated ancestor), extend the captured rect DOWN to the wrapper's
    // bottom edge only — expanding on every side (including up) previously swept in whatever sits
    // above the video within the same wrapper too (e.g. a title bar), producing an empty top band
    // that pushed the real content down and off the bottom of the placed screen.
    var parentEl = el.parentElement;
    if (parentEl) {
      var p = parentEl.getBoundingClientRect();
      if (p.width <= rect.width * 1.5 && p.height <= rect.height * 1.5 && p.bottom > contentRect.y + contentRect.h) {
        contentRect = { x: contentRect.x, y: contentRect.y, w: contentRect.w, h: p.bottom - contentRect.y };
      }
    }

    return { x: round(contentRect.x), y: round(contentRect.y), w: round(contentRect.w), h: round(contentRect.h) };
  }

  function round(n) {
    return Math.round(n);
  }

  // ---- 5.9: DOM outline for debug snapshots (text stripped, per design.md §6.4 item 1) ----

  function buildDomOutline(el, depth) {
    depth = depth || 0;
    if (depth > 12 || !el || !el.tagName) return null;

    var node = {
      tag: el.tagName.toLowerCase(),
      id: el.id || undefined,
      classes: el.className && typeof el.className === "string" ? el.className.split(/\s+/).filter(Boolean) : undefined,
      children: [],
    };

    for (var i = 0; i < el.children.length; i++) {
      var child = buildDomOutline(el.children[i], depth + 1);
      if (child) node.children.push(child);
    }

    return node;
  }

  // ---- 5.7: media/room-info payload shapes ----

  function buildMediaState(kind, el, contentRect, error) {
    return {
      type: "media",
      kind: kind,
      w: el ? el.videoWidth || contentRect.w : contentRect.w,
      h: el ? el.videoHeight || contentRect.h : contentRect.h,
      paused: el && "paused" in el ? el.paused : null,
      currentTime: el && kind === "html5" ? el.currentTime : null,
      src: el && kind === "html5" ? el.currentSrc || el.src : el && kind === "iframe" ? el.src : null,
      error: error || null,
      rect: [contentRect.x, contentRect.y, contentRect.w, contentRect.h],
    };
  }

  function buildRoomInfo(title, members, presenter) {
    return { type: "room", title: title, members: members, presenter: presenter || null };
  }

  // ---- 5.8: participant voice muting (default RoomVoiceMode.Off) ----

  function muteAllExceptPrimary(doc, primaryEl) {
    toArray(doc.querySelectorAll("video, audio")).forEach(function (el) {
      el.muted = el !== primaryEl;
    });
  }

  return {
    querySelectorAllExt: querySelectorAllExt,
    matchesSelectorGroup: matchesSelectorGroup,
    detectPageState: detectPageState,
    UnknownStateTracker: UnknownStateTracker,
    buildDomOutline: buildDomOutline,
    setNativeInputValue: setNativeInputValue,
    tryJoinGate: tryJoinGate,
    scoreElement: scoreElement,
    pickBestCandidate: pickBestCandidate,
    PrimarySelector: PrimarySelector,
    THEATER_CSS: THEATER_CSS,
    applyTheaterMode: applyTheaterMode,
    computeContentRect: computeContentRect,
    buildMediaState: buildMediaState,
    buildRoomInfo: buildRoomInfo,
    muteAllExceptPrimary: muteAllExceptPrimary,
  };
});
