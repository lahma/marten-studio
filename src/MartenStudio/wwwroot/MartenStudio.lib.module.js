export function afterWebStarted() {
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

    window.martenStudio.scroll = window.martenStudio.scroll || {
        intoView: function (elementId, block) {
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
        }
    };
}
