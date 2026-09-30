/*
 * Marten Studio's browser helpers.
 *
 * Deliberately NOT a Blazor JS initializer. It used to be one - `wwwroot/MartenStudio.lib.module.js`,
 * exporting `afterWebStarted` - and Blazor loads the initializers of every referenced RCL into every
 * Blazor app in the process. A host that runs its own Blazor Server app therefore fetched this file from
 * the *site root* on every page it served, including pages that have nothing to do with the studio; where
 * MartenStudioOptions.AuthorizationPolicy is set, that path is authorized too, so the fetch was redirected
 * to the login page, came back as text/html, and the browser logged a MIME-type error on every page load -
 * the host's unauthenticated login page included. Registering the studio must never change how the host
 * behaves, and that was the host's own app executing a script belonging to something it had only
 * referenced (issue #2).
 *
 * So it is an ordinary classic script now, loaded by the studio's own shell from a path that is relative
 * to the studio-rooted <base href>. Only the studio's pages ask for it, it resolves under whatever mount
 * path the studio was given, and a host that never opens the studio never fetches it.
 *
 * It runs at the end of <body>, which is earlier than `afterWebStarted` ever ran: the prerendered DOM is
 * parsed, so applyRememberedTheme below still has .ms-studio elements to find, and the globals are all
 * defined before blazor.web.js starts the circuit that calls them. Every assignment is `x = x || {...}`
 * and every enhance* is idempotent, so re-running it costs nothing.
 */
(function martenStudioBrowserHelpers() {
    // As `export function afterWebStarted()` this file was an ES module, and therefore strict. A classic
    // script is not, so strictness is asked for rather than inherited: nothing in here behaves
    // differently under it today, and a future typo should keep throwing rather than quietly minting a
    // global.
    "use strict";

    window.martenStudio = window.martenStudio || {};

    window.martenStudio.prefs = window.martenStudio.prefs || {
        get: function (key) {
            try {
                const localStorageValue = window.localStorage.getItem(key);
                if (localStorageValue) {
                    return localStorageValue;
                }
            }
            catch {
            }

            const encodedKey = encodeURIComponent(key) + "=";
            const cookieParts = document.cookie ? document.cookie.split("; ") : [];
            for (const cookiePart of cookieParts) {
                if (!cookiePart.startsWith(encodedKey)) {
                    continue;
                }

                return decodeURIComponent(cookiePart.substring(encodedKey.length));
            }

            return null;
        },
        set: function (key, value) {
            try {
                window.localStorage.setItem(key, value);
            }
            catch {
            }

            const basePath = new URL(document.baseURI).pathname;
            const encodedKey = encodeURIComponent(key);
            const encodedValue = encodeURIComponent(value);
            document.cookie = encodedKey + "=" + encodedValue + "; path=" + basePath + "; max-age=31536000; samesite=lax";
        }
    };

    // The theme, applied before anything else this module does.
    //
    // StudioState is scoped and the circuit's scope is not the request's: it has no HttpContext, so the
    // cookie that themed the prerendered page is invisible to the circuit and the studio flashed from
    // dark to light the moment the circuit attached. The server side of that is fixed by carrying the
    // prerendered theme across as persisted component state; this is the half that covers a preference
    // stored only in localStorage, and it runs here - synchronously, on the prerendered DOM - so the
    // flash is over before the first interactive render arrives.
    (function applyRememberedTheme() {
        let theme = null;
        try {
            theme = window.martenStudio.prefs.get("ms_theme");
        }
        catch {
            return;
        }

        if (theme !== "light" && theme !== "dark" && theme !== "system") {
            return;
        }

        for (const element of document.querySelectorAll(".ms-studio")) {
            element.setAttribute("data-theme", theme);
        }
    })();

    window.martenStudio.clipboard = window.martenStudio.clipboard || {
        copyText: async function (text) {
            const normalized = text ?? "";
            try {
                if (navigator.clipboard && navigator.clipboard.writeText) {
                    await navigator.clipboard.writeText(normalized);
                    return true;
                }
            }
            catch {
            }

            try {
                const textArea = document.createElement("textarea");
                textArea.value = normalized;
                textArea.setAttribute("readonly", "");
                textArea.style.position = "fixed";
                textArea.style.opacity = "0";
                textArea.style.pointerEvents = "none";
                document.body.appendChild(textArea);
                textArea.select();
                textArea.setSelectionRange(0, normalized.length);
                const copied = document.execCommand("copy");
                document.body.removeChild(textArea);
                return copied;
            }
            catch {
                return false;
            }
        }
    };

    // Page visibility, so a page that polls can stop while nobody is looking at it. One listener for the
    // whole document however many components ask, removed again once the last watcher goes, so a circuit
    // that closes leaves nothing behind.
    //
    // Watchers are keyed on a string token the .NET side generates, NOT on the DotNetObjectReference.
    // Blazor marshals a DotNetObjectReference as an id and materialises a *new* JS wrapper object for
    // every call, so the object that arrives at unwatch is never the object that arrived at watch: a Set
    // keyed on it could add but never remove, the visibilitychange listener stayed attached for the life
    // of the page, and every closed circuit left another dead reference in it.
    //
    // invokeMethodAsync returns a promise, so a call on a disposed reference rejects rather than throwing:
    // the synchronous catch below never saw it and the dead watcher was never dropped. Hence the .catch().
    window.martenStudio.visibility = window.martenStudio.visibility || (function () {
        const watchers = new Map();
        let listening = false;

        function drop(token) {
            if (watchers.delete(token) && watchers.size === 0) {
                stop();
            }
        }

        function notify() {
            const hidden = document.hidden === true;
            for (const entry of Array.from(watchers)) {
                const token = entry[0];
                const watcher = entry[1];
                try {
                    const pending = watcher.invokeMethodAsync("OnVisibilityChanged", hidden);
                    if (pending && typeof pending.catch === "function") {
                        // The circuit went away without unwatching. Drop it rather than rejecting on
                        // every visibility change for the rest of the page's life.
                        pending.catch(function () { drop(token); });
                    }
                }
                catch {
                    drop(token);
                }
            }

            if (watchers.size === 0) {
                stop();
            }
        }

        function start() {
            if (listening) {
                return;
            }

            document.addEventListener("visibilitychange", notify);
            listening = true;
        }

        function stop() {
            if (!listening) {
                return;
            }

            document.removeEventListener("visibilitychange", notify);
            listening = false;
        }

        return {
            /*
             * watch(token, dotNetRef) - the token is what unwatch(token) is called with later. There is
             * no one-argument form: a DotNetObjectReference cannot be matched back to the one handed in,
             * so a caller that passes only a reference has no way to unregister itself.
             */
            watch: function (token, dotNetRef) {
                if (!dotNetRef || typeof token !== "string" || watchers.has(token)) {
                    return document.hidden === true;
                }

                watchers.set(token, dotNetRef);
                start();
                return document.hidden === true;
            },
            unwatch: function (token) {
                if (typeof token !== "string") {
                    return;
                }

                drop(token);
            }
        };
    })();

    // Native <dialog>, driven from C# because showModal() has no HTML attribute equivalent. Both calls
    // are no-ops when the element is missing or already in the requested state, so a component may call
    // either one twice without checking.
    window.martenStudio.dialog = window.martenStudio.dialog || {
        showModal: function (id) {
            const dialog = document.getElementById(id);
            if (!dialog || typeof dialog.showModal !== "function" || dialog.open) {
                return false;
            }

            dialog.showModal();
            return true;
        },
        close: function (id) {
            const dialog = document.getElementById(id);
            if (!dialog || typeof dialog.close !== "function" || !dialog.open) {
                return false;
            }

            dialog.close();
            return true;
        }
    };

    // JSON toolkit helpers (folded in from the W1 packet). Every C# call site degrades when these
    // are missing (prerender), so each function is defensive and idempotent.
    window.martenStudio.json = window.martenStudio.json || {
        /*
         * Opens the shared copy menu from the keyboard. A mouse press goes through the row button's
         * `popovertarget` and never reaches this.
         */
        openMenu: function (element) {
            let shown = false;
            try {
                if (element && typeof element.showPopover === "function") {
                    element.showPopover();
                    shown = true;
                }
            }
            catch {
                // Already open, or no popover support. Either way the focus move below is still right.
            }

            try {
                // A role="menu" is one tab stop with its own roving tabindex, so opening it has to put
                // DOM focus on the first item; without that the keyboard lands nowhere and the arrow
                // keys have nothing to move.
                const first = element && element.querySelector
                    ? element.querySelector("[role=\"menuitem\"]")
                    : null;
                if (first && typeof first.focus === "function") {
                    first.focus();
                }
            }
            catch {
            }

            return shown;
        },

        /*
         * Moves DOM focus onto one element by id, for the roving tabindex in the copy menu. Focus is a
         * courtesy here: a missing element is an answer, never an exception.
         */
        focusElement: function (elementId) {
            try {
                const element = document.getElementById(elementId);
                if (element && typeof element.focus === "function") {
                    element.focus();
                    return true;
                }
            }
            catch {
            }

            return false;
        },

        /*
         * Stops the page scrolling under the JSON tree's own keys.
         *
         * Razor decides `@onkeydown:preventDefault` when the component renders, so it can only be
         * "always" or "never" - and "always" would swallow Tab and take the tree out of the page's focus
         * order. The real condition is per key and per target, which is a runtime question, so it is
         * answered here: the arrows, Home, End and Space are prevented only when the tree row itself is
         * the thing with focus. A button or an input inside a row keeps every key it needs.
         *
         * Idempotent: calling it twice on the same element attaches nothing twice.
         */
        captureTreeKeys: function (element) {
            if (!element || element.dataset.msTreeKeys === "true") {
                return false;
            }

            const captured = ["ArrowUp", "ArrowDown", "Home", "End", " ", "Spacebar"];

            const onKeyDown = function (event) {
                if (event.ctrlKey || event.altKey || event.metaKey) {
                    return;
                }

                if (captured.indexOf(event.key) < 0) {
                    return;
                }

                const target = event.target;
                if (!target || !target.getAttribute) {
                    return;
                }

                const role = target.getAttribute("role");
                if (role !== "treeitem" && role !== "tree") {
                    return;
                }

                event.preventDefault();
            };

            element.addEventListener("keydown", onKeyDown);
            element.dataset.msTreeKeys = "true";
            element.msTreeKeyHandler = onKeyDown;
            return true;
        },

        releaseTreeKeys: function (element) {
            if (!element || !element.msTreeKeyHandler) {
                return false;
            }

            element.removeEventListener("keydown", element.msTreeKeyHandler);
            delete element.msTreeKeyHandler;
            delete element.dataset.msTreeKeys;
            return true;
        },

        closeMenu: function (element) {
            try {
                if (element && typeof element.hidePopover === "function") {
                    element.hidePopover();
                    return true;
                }
            }
            catch {
            }

            return false;
        },

        /*
         * The textarea's live value. The editor binds on `change`, which has not fired when Ctrl+Enter
         * arrives, so the keyboard save path asks for the value directly rather than saving stale text.
         */
        readValue: function (element) {
            try {
                return element && typeof element.value === "string" ? element.value : null;
            }
            catch {
                return null;
            }
        },

        /*
         * Soft tabs, a gutter that scrolls with the text, and the editor's two shortcuts.
         *
         * The shortcuts live here rather than on a Blazor `@onkeydown` because that handler would send
         * every keystroke over the circuit and re-render the whole editor - gutter included - on the way
         * back. Here nothing crosses the wire until Ctrl+Enter or Esc actually happens, and when it does
         * it carries `textarea.value`, so the save can never be one round trip behind what is on screen.
         *
         * Idempotent: calling it twice on the same textarea attaches nothing twice.
         */
        enhanceTextarea: function (textarea, gutter, dotNetRef) {
            if (!textarea || textarea.dataset.msEnhanced === "true") {
                return false;
            }

            const onKeyDown = function (event) {
                if (event.key === "Enter" && (event.ctrlKey || event.metaKey) && !event.altKey) {
                    event.preventDefault();
                    if (dotNetRef) {
                        try {
                            dotNetRef.invokeMethodAsync("OnEditorSaveAsync", textarea.value);
                        }
                        catch {
                            // The circuit has gone; the page is already being torn down.
                        }
                    }

                    return;
                }

                if (event.key === "Escape" || event.key === "Esc") {
                    event.preventDefault();
                    if (dotNetRef) {
                        try {
                            dotNetRef.invokeMethodAsync("OnEditorCancelAsync", textarea.value);
                        }
                        catch {
                        }
                    }

                    return;
                }

                if (event.key !== "Tab" || event.ctrlKey || event.altKey || event.metaKey) {
                    return;
                }

                // Shift+Tab keeps its meaning: it is how a keyboard user leaves the textarea.
                if (event.shiftKey) {
                    return;
                }

                event.preventDefault();
                const start = textarea.selectionStart;
                const end = textarea.selectionEnd;
                const value = textarea.value;
                textarea.value = value.substring(0, start) + "  " + value.substring(end);
                textarea.selectionStart = start + 2;
                textarea.selectionEnd = start + 2;
            };

            const onScroll = function () {
                if (gutter) {
                    gutter.scrollTop = textarea.scrollTop;
                }
            };

            textarea.addEventListener("keydown", onKeyDown);
            textarea.addEventListener("scroll", onScroll);
            textarea.dataset.msEnhanced = "true";
            textarea.msJsonHandlers = { keydown: onKeyDown, scroll: onScroll };
            return true;
        },

        releaseTextarea: function (textarea) {
            if (!textarea || !textarea.msJsonHandlers) {
                return false;
            }

            textarea.removeEventListener("keydown", textarea.msJsonHandlers.keydown);
            textarea.removeEventListener("scroll", textarea.msJsonHandlers.scroll);
            delete textarea.msJsonHandlers;
            delete textarea.dataset.msEnhanced;
            return true;
        }
    };

    // The Query page's editor key handler (folded in from the P6 packet).
    //
    // Why JavaScript at all, when Blazor has @onkeydown: a keydown binding on a textarea sends one round
    // trip per keystroke over the circuit and re-renders the component each time, which turns typing a
    // query into a latency test. This listener runs in the browser and only calls back on the three
    // chords that mean something - and it carries the textarea's live value with it, because the C# side
    // binds on `change`, which has not fired yet when Ctrl+Enter arrives.
    window.martenStudio.query = window.martenStudio.query || {
        /*
         * Attaches the key handler to one editor. Idempotent: a second call on the same textarea attaches
         * nothing and answers false, so a component that re-renders does not end up running a query twice
         * per keypress.
         */
        enhanceEditor: function (textarea, dotNetRef) {
            if (!textarea || !dotNetRef || textarea.dataset.msQueryEnhanced === "true") {
                return false;
            }

            const invoke = function (name, argument) {
                try {
                    const pending = argument === undefined
                        ? dotNetRef.invokeMethodAsync(name)
                        : dotNetRef.invokeMethodAsync(name, argument);

                    if (pending && typeof pending.catch === "function") {
                        // The circuit went away between the keypress and the call; there is nobody to tell.
                        pending.catch(function () { });
                    }
                }
                catch {
                }
            };

            const onKeyDown = function (event) {
                if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
                    event.preventDefault();
                    invoke("RunAsync", textarea.value);
                    return;
                }

                if (event.key === "Escape") {
                    event.preventDefault();
                    invoke("CancelAsync");
                    return;
                }

                if ((event.key === "s" || event.key === "S") && (event.ctrlKey || event.metaKey)) {
                    // Ctrl+S in a browser is Save Page As, which is never what someone means while they
                    // are typing SQL into an editor.
                    event.preventDefault();
                    invoke("SaveAsync", textarea.value);
                }
            };

            textarea.addEventListener("keydown", onKeyDown);
            textarea.dataset.msQueryEnhanced = "true";
            textarea.msQueryHandlers = { keydown: onKeyDown };
            return true;
        },

        releaseEditor: function (textarea) {
            if (!textarea || !textarea.msQueryHandlers) {
                return false;
            }

            textarea.removeEventListener("keydown", textarea.msQueryHandlers.keydown);
            delete textarea.msQueryHandlers;
            delete textarea.dataset.msQueryEnhanced;
            return true;
        },

        /*
         * The textarea's live value, for the paths that do not come through the key handler.
         */
        readValue: function (textarea) {
            try {
                return textarea && typeof textarea.value === "string" ? textarea.value : null;
            }
            catch {
                return null;
            }
        }
    };

    // Hands the viewer a file to save. A data: URL fallback is what the caller uses when this is
    // unavailable, so returning false is a valid answer, never an exception.
    window.martenStudio.download = window.martenStudio.download || {
        text: function (fileName, mime, content) {
            try {
                const blob = new Blob([content], { type: mime || "text/plain" });
                const url = URL.createObjectURL(blob);
                const anchor = document.createElement("a");
                anchor.href = url;
                anchor.download = fileName;
                document.body.appendChild(anchor);
                anchor.click();
                document.body.removeChild(anchor);
                setTimeout(function () { URL.revokeObjectURL(url); }, 0);
                return true;
            } catch (e) {
                return false;
            }
        }
    };

    window.martenStudio.scroll = window.martenStudio.scroll || {};

    if (typeof window.martenStudio.scroll.intoView !== "function") {
        window.martenStudio.scroll.intoView = function (elementId, block) {
            try {
                const element = document.getElementById(elementId);
                if (element) {
                    element.scrollIntoView({ block: block || "nearest" });
                    return true;
                }
            } catch (e) {
                // Nothing to do: scrolling is a courtesy.
            }
            return false;
        };
    }

    /*
     * Brings the first element inside `container` that matches `selector` into view within that container's
     * own scroll box, and moves nothing else - the database browser's rail marks the open table this way.
     * `scrollIntoView` is not used because it scrolls every scrolling ancestor too, the page among them. A
     * container that does not scroll (a phone's rail, which is a plain block), an element that is not drawn
     * (inside a closed <details>), or one already in view is left alone, so a second call changes nothing.
     *
     * As far as is needed and no further - `block: 'nearest'` - with a few pixels to spare, so the entry is
     * not flush with the edge. It used to centre the entry, which scrolled the rail's own top - the SCHEMAS
     * header and "All schemas" - out of view for any table low in the list, even one that would have fitted
     * with the header still showing (UX-7).
     */
    if (typeof window.martenStudio.scroll.revealWithin !== "function") {
        window.martenStudio.scroll.revealWithin = function (container, selector) {
            try {
                if (!container || typeof container.querySelector !== "function") {
                    return false;
                }

                const element = container.querySelector(selector);
                if (!element || element.getClientRects().length === 0 || container.scrollHeight <= container.clientHeight) {
                    return false;
                }

                // The scroll box's own visible area: inside the border, above a horizontal scrollbar.
                const box = container.getBoundingClientRect();
                const top = box.top + container.clientTop;
                const bottom = top + container.clientHeight;
                const item = element.getBoundingClientRect();
                const spare = 8;

                if (item.top >= top && item.bottom <= bottom) {
                    return true;
                }

                if (item.top < top) {
                    container.scrollTop -= (top - item.top) + spare;
                } else {
                    container.scrollTop += (item.bottom - bottom) + spare;
                }

                return true;
            } catch (e) {
                return false;
            }
        };
    }

    // Whether the visitor is using the keyboard, for the page heading's focus ring.
    //
    // `<FocusOnNavigate Selector="h1" />` puts focus on the page's heading after every navigation, which is
    // right for a screen reader, and the stylesheet draws a ring round a heading only for a keyboard focus.
    // But whether a *script's* focus counts as a keyboard one is the browser's guess, and Chromium guesses
    // yes whenever the page has seen no pointer input yet - which is every page opened from the address bar,
    // a bookmark or a reload. So every such page drew a 2 px box round the whole title row for a mouse user.
    // This records what the visitor last actually did - a key, or a pointer - as `data-ms-keyboard` on the
    // document element, and the stylesheet only draws the heading's ring while it is there. A keyboard user
    // who presses Enter on a link still gets the ring on the heading of the page it opened.
    //
    // The state lives here and the attribute is only its reflection, because a navigation inside the studio
    // brings the document element's attributes back in line with the page the server sent - which has none
    // of this - measured: an attribute set on <html> was gone after every link. So an observer on exactly
    // that one attribute puts it back whenever it goes while the visitor is still on the keyboard; it
    // changes nothing when the value is already right, so its own writes do not wake it. Idempotent: the
    // listeners are added once however often the script runs.
    window.martenStudio.inputModality = window.martenStudio.inputModality || (function () {
        const root = document.documentElement;
        const name = "data-ms-keyboard";
        let keyboard = false;

        function reflect() {
            if (keyboard) {
                if (root.getAttribute(name) !== "true") {
                    root.setAttribute(name, "true");
                }
            }
            else if (root.hasAttribute(name)) {
                root.removeAttribute(name);
            }
        }

        document.addEventListener("keydown", function (event) {
            if (event.ctrlKey || event.altKey || event.metaKey) {
                return;
            }

            keyboard = true;
            reflect();
        }, { capture: true, passive: true });

        document.addEventListener("pointerdown", function () {
            keyboard = false;
            reflect();
        }, { capture: true, passive: true });

        if (typeof window.MutationObserver === "function") {
            new window.MutationObserver(reflect).observe(root, { attributes: true, attributeFilter: [name] });
        }

        return {
            /* Whether the visitor's last input was a key - for a caller that needs to know rather than style. */
            isKeyboard: function () {
                return keyboard;
            }
        };
    })();

    // Closes a <details> without drawing it again, for the database browser's rail on a phone: a page it
    // leads to opens with it shut. Blazor cannot say "closed" to an element a visitor opened - it only writes
    // an attribute whose rendered value changed - and re-creating the element to close it took the keyboard
    // focus away with the link that had it. A <details> that is already shut is left alone.
    window.martenStudio.disclosure = window.martenStudio.disclosure || {
        close: function (element) {
            try {
                if (element && element.open === true) {
                    element.open = false;
                    return true;
                }
            }
            catch {
            }

            return false;
        }
    };

    // The edge cue on a table that scrolls sideways (UX-1).
    //
    // The scrolling itself is `overflow-x: auto` in marten-studio.css and needs nothing from here. What
    // does need a script is knowing *whether* a given region can still be scrolled, which is the only
    // thing CSS cannot answer today: `animation-timeline: scroll()` would, and it is Chromium-only. So
    // this writes `data-scroll-start` / `data-scroll-end` on each region and the stylesheet fades the
    // matching edge. With no script at all the tables still scroll; only the cue is missing.
    //
    // It is one document-wide observer rather than one interop registration per table, for two reasons.
    // The first is `.ms-table-wrap`, the documents list's own scroll container, which lives in a
    // component this packet does not own and therefore cannot be asked to register itself - a selector
    // is the only way to reach it. The second is cost: the studio renders up to four scrollable tables
    // on one screen, and a registration each would be four round trips over the circuit per render to
    // learn something the browser already knows. Nothing here talks to .NET, so nothing here can hold a
    // DotNetObjectReference open after a circuit closes.
    window.martenStudio.tableScroll = window.martenStudio.tableScroll || (function () {
        // The database browser's two tab rows are here too: on a phone they are one row that scrolls
        // sideways (UX-6), and the same edge cue is what says there are more tabs past the edge.
        const selector = ".ms-table-scroll, .ms-table-wrap, .ms-db-kind-tabs, .ms-db-object-tabs";

        // region -> the child element observed with it (a table's own width is what changes when rows
        // arrive, and the region's box does not move when it does).
        const tracked = new Map();

        let resizeObserver = null;
        let mutationObserver = null;
        let scanQueued = false;

        function flag(element, name, on) {
            if (on) {
                if (element.getAttribute(name) !== "true") {
                    element.setAttribute(name, "true");
                }
            }
            else if (element.hasAttribute(name)) {
                element.removeAttribute(name);
            }
        }

        function update(region) {
            let slack;
            let offset;
            try {
                slack = region.scrollWidth - region.clientWidth;
                offset = region.scrollLeft;
            }
            catch {
                return;
            }

            // A pixel of tolerance: sub-pixel layout widths make a region that cannot scroll at all
            // report a fraction of slack, and a fraction is not "there is more to see".
            const scrollable = slack > 1;
            flag(region, "data-scroll-start", scrollable && offset > 1);
            flag(region, "data-scroll-end", scrollable && offset < slack - 1);
        }

        function observeChild(region, child) {
            const previous = tracked.get(region);
            if (previous === child) {
                return;
            }

            if (previous && resizeObserver) {
                resizeObserver.unobserve(previous);
            }

            if (child && resizeObserver) {
                resizeObserver.observe(child);
            }

            tracked.set(region, child);
        }

        function scan() {
            scanQueued = false;

            const seen = new Set();
            for (const region of document.querySelectorAll(selector)) {
                seen.add(region);

                if (!tracked.has(region)) {
                    tracked.set(region, null);
                    if (resizeObserver) {
                        resizeObserver.observe(region);
                    }
                }

                observeChild(region, region.firstElementChild || null);
                update(region);
            }

            for (const region of Array.from(tracked.keys())) {
                if (seen.has(region)) {
                    continue;
                }

                const child = tracked.get(region);
                if (resizeObserver) {
                    resizeObserver.unobserve(region);
                    if (child) {
                        resizeObserver.unobserve(child);
                    }
                }

                tracked.delete(region);
            }
        }

        function schedule() {
            if (scanQueued) {
                return;
            }

            scanQueued = true;
            if (typeof window.requestAnimationFrame === "function") {
                window.requestAnimationFrame(scan);
            }
            else {
                window.setTimeout(scan, 0);
            }
        }

        function updateAll() {
            for (const region of tracked.keys()) {
                update(region);
            }
        }

        function onScroll(event) {
            // `scroll` does not bubble, so this listens in the capture phase: one listener for every
            // scrollable region on the page, however many appear and disappear.
            const target = event.target;
            if (target && target.nodeType === 1 && typeof target.matches === "function" && target.matches(selector)) {
                update(target);
            }
        }

        function start() {
            if (typeof window.ResizeObserver === "function") {
                resizeObserver = new window.ResizeObserver(updateAll);
            }

            if (typeof window.MutationObserver === "function") {
                // Blazor applies a render batch as one set of DOM mutations, so this fires once per
                // render rather than once per row. The scan is deferred to the next frame so a batch
                // that adds a table and then fills it is measured once, after the layout it produced.
                mutationObserver = new window.MutationObserver(schedule);
                mutationObserver.observe(document.documentElement, { childList: true, subtree: true });
            }

            document.addEventListener("scroll", onScroll, { capture: true, passive: true });
            window.addEventListener("resize", schedule, { passive: true });
        }

        start();
        schedule();

        return {
            /*
             * Re-measures every scrollable region, for a caller that changed one in a way the observers
             * above cannot see. Nothing needs it today; it is here so that something can ask rather than
             * reaching into the module.
             */
            refresh: schedule
        };
    })();

    // The database browser's row grid (DB-5): a focused row opens in place on Space, which the circuit
    // hears through the row's own @onkeydown. What it cannot do is stop the browser's own Space - scrolling
    // the page by a screen - because Razor decides `@onkeydown:preventDefault` when the component renders,
    // not per key, and preventing every key would take Tab away from the row. So this stops Space and
    // nothing else, and only when the row itself has the focus: a link, a button or a field inside a row
    // keeps every key it had. One capture-free listener on the document, idempotent like everything here.
    window.martenStudio.rowKeys = window.martenStudio.rowKeys || (function () {
        document.addEventListener("keydown", function (event) {
            if (event.key !== " " && event.key !== "Spacebar") {
                return;
            }

            const target = event.target;
            if (target && target.nodeType === 1 && typeof target.matches === "function" && target.matches("tr[data-ms-row]")) {
                event.preventDefault();
            }
        });

        return {};
    })();
})();
