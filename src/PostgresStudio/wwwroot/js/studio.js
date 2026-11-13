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

    // Native modal dialogs (1.2-A11Y). Every studio dialog is a <dialog class="ms-modal"> that the server renders with the `open`
    // attribute - which is today's overlay, drawn by the stylesheet - and that this script upgrades to a real modal with showModal():
    // the browser then makes the rest of the page inert and keeps Tab inside, and the script moves focus to the element marked
    // `data-ms-dialog-focus` (Cancel, or "Back to the editor", for an action that cannot be undone; the typed-confirmation box when there
    // is one; never a button that saves or deletes), turns Esc into the component's own Cancel, and puts focus back on the control that opened the dialog when
    // the dialog goes. A page where this script (or the browser's showModal) is missing keeps the overlay it always had: the
    // upgrade is the only thing that is lost, which is why the server never waits for it.
    //
    // present(id, ref)   upgrades one server-rendered dialog; `ref` is the component's DotNetObjectReference (Esc calls its
    //                    OnEscapeAsync), or null for a dialog that is only ever shown and closed here (the shortcut sheet).
    // release(id)        the component is done with the dialog (closed, or disposed): closes it if it is still in the page and
    //                    restores focus. Safe to call twice, or for a dialog that was never presented.
    // openStatic(id)     shows a dialog that is always in the page, for `?`.
    window.martenStudio.dialog = window.martenStudio.dialog || (function () {
        const live = new Map();
        const focusable = "a[href], button:not([disabled]), input:not([disabled]):not([type='hidden']), select:not([disabled]), "
            + "textarea:not([disabled]), summary, [tabindex]:not([tabindex='-1'])";
        let lastActivator = null;

        // Safari does not focus a button on click, so the control that opened a dialog is not always document.activeElement.
        document.addEventListener("click", function (event) {
            const target = event.target;
            if (!target || typeof target.closest !== "function") {
                return;
            }

            const activator = target.closest("button, a[href], summary, [role='button']");
            if (activator && !activator.closest("dialog.ms-modal")) {
                lastActivator = activator;
            }
        }, true);

        function invokerNow() {
            const active = document.activeElement;
            if (active && active !== document.body && active !== document.documentElement && !active.closest("dialog.ms-modal")) {
                return active;
            }

            return lastActivator && lastActivator.isConnected ? lastActivator : null;
        }

        function nobodyHasFocus() {
            const active = document.activeElement;
            return !active || active === document.body || active === document.documentElement;
        }

        function usable(element) {
            return !!element && element.isConnected && !element.disabled && typeof element.focus === "function"
                && element.getClientRects().length > 0;
        }

        function focusElement(element) {
            // A row or a heading is not focusable by nature; tabindex="-1" makes it a target for script and keeps it out of the Tab order.
            if (!element.matches(focusable) && !element.hasAttribute("tabindex")) {
                element.setAttribute("tabindex", "-1");
            }

            try {
                element.focus({ preventScroll: true });
            }
            catch {
            }
        }

        // Where focus goes when the control that opened the dialog is gone or disabled (a Rebuild button is both while it rebuilds), so
        // that it is never left on <body> (1.2-FINAL-FIX): the same row's first usable control, else the row itself, else the page's
        // main heading, else the main region.
        function fallbackTarget(state) {
            let row = state.row && state.row.isConnected ? state.row : null;
            if (!row && state.rowId) {
                row = document.getElementById(state.rowId);
            }

            if (row) {
                return Array.prototype.find.call(row.querySelectorAll(focusable), usable) || row;
            }

            return document.querySelector("main h1, h1, [role='main'] h1")
                || document.querySelector("main, [role='main'], .ms-main");
        }

        // The page re-renders after a confirmation (the Rebuild button is replaced, or disabled, once the operation starts), and a browser
        // moves focus to <body> when the element that has it goes. So for a few seconds after the dialog closed, a focus that is nowhere
        // is put on the fallback; the first key or pointer press of the visitor ends it, and focus that is somewhere is never touched.
        function settle(state) {
            if (state.settling) {
                return;
            }

            state.settling = true;
            let ticks = 0;
            let timer = 0;
            const recover = function () {
                if (nobodyHasFocus()) {
                    const target = fallbackTarget(state);
                    if (target) {
                        focusElement(target);
                    }
                }
            };

            // After every DOM change as well as on the clock: a re-render that replaces the focused control is put right before the browser
            // paints or anything samples focus, not up to a tick later.
            const watcher = typeof MutationObserver === "function" && document.body ? new MutationObserver(recover) : null;
            if (watcher) {
                watcher.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["disabled"] });
            }

            const stop = function () {
                clearInterval(timer);
                if (watcher) {
                    watcher.disconnect();
                }

                document.removeEventListener("keydown", stop, true);
                document.removeEventListener("pointerdown", stop, true);
            };

            document.addEventListener("keydown", stop, true);
            document.addEventListener("pointerdown", stop, true);
            timer = setInterval(function () {
                ticks++;
                recover();

                if (ticks >= 60) {
                    stop();
                }
            }, 50);
        }

        // Only when focus is nowhere: a page that has already moved it on (FocusOnNavigate puts it on the new heading) keeps it.
        function restore(state) {
            if (!state) {
                return;
            }

            if (nobodyHasFocus()) {
                if (usable(state.invoker)) {
                    focusElement(state.invoker);
                }
                else {
                    const target = fallbackTarget(state);
                    if (target) {
                        focusElement(target);
                    }
                }
            }

            settle(state);
        }

        function tabbable(dialog) {
            return Array.prototype.filter.call(dialog.querySelectorAll(focusable), function (element) {
                return element.getClientRects().length > 0 && getComputedStyle(element).visibility !== "hidden";
            });
        }

        // The browser already keeps Tab out of the inert page; this keeps it from leaving through the browser's own chrome too, so
        // the dialog is a loop in every browser.
        function onKeyDown(event) {
            if (event.key !== "Tab" || event.ctrlKey || event.altKey || event.metaKey) {
                return;
            }

            const dialog = event.currentTarget;
            const items = tabbable(dialog);
            if (items.length === 0) {
                event.preventDefault();
                return;
            }

            const first = items[0];
            const last = items[items.length - 1];
            const active = document.activeElement;

            if (event.shiftKey && (active === first || active === dialog)) {
                event.preventDefault();
                last.focus();
            }
            else if (!event.shiftKey && active === last) {
                event.preventDefault();
                first.focus();
            }
        }

        function tell(state) {
            if (!state || !state.ref || state.released) {
                return;
            }

            try {
                const pending = state.ref.invokeMethodAsync("OnEscapeAsync");
                if (pending && typeof pending.catch === "function") {
                    pending.catch(function () { });
                }
            }
            catch {
                // The circuit has gone; the dialog goes with the page.
            }
        }

        function wire(dialog) {
            if (dialog.dataset.msDialogWired === "true") {
                return;
            }

            dialog.dataset.msDialogWired = "true";
            dialog.addEventListener("keydown", onKeyDown);

            // Esc. A component-driven dialog runs its own Cancel and leaves the closing to the server; a static one closes itself.
            dialog.addEventListener("cancel", function (event) {
                const state = live.get(dialog.id);
                if (state && state.ref) {
                    event.preventDefault();
                    tell(state);
                }
            });

            // The browser closed the dialog by itself (a second Esc is allowed through whatever cancel did): a component-driven one
            // is told, so the server's idea of "open" does not outlive the browser's; a static one has only focus to give back.
            dialog.addEventListener("close", function () {
                // The event can be delivered late - after the same dialog has been shown again - and then it is about the earlier
                // closing, which was dealt with when it happened.
                const state = live.get(dialog.id);
                if (!state || state.released || dialog.open) {
                    return;
                }

                if (state.ref) {
                    tell(state);
                }
                else {
                    live.delete(dialog.id);
                    restore(state);
                }
            });
        }

        function moveFocus(dialog) {
            const target = dialog.querySelector("[data-ms-dialog-focus]");
            if (target && !target.disabled && typeof target.focus === "function") {
                target.focus();
            }
        }

        function present(dialog, ref) {
            if (!dialog || dialog.tagName !== "DIALOG" || typeof dialog.showModal !== "function" || !dialog.id) {
                return false;
            }

            const known = live.get(dialog.id);
            if (known && known.dialog === dialog && dialog.isConnected && dialog.open) {
                return true;
            }

            const invoker = invokerNow();

            try {
                // showModal() throws on a dialog that is already open, and the server renders it open.
                if (dialog.hasAttribute("open")) {
                    dialog.removeAttribute("open");
                }

                dialog.showModal();
            }
            catch {
                dialog.setAttribute("open", "");
                return false;
            }

            const row = invoker && typeof invoker.closest === "function" ? invoker.closest("tr") : null;
            const state = { dialog: dialog, invoker: invoker, row: row, rowId: row ? row.id : "", ref: ref || null, released: false, settling: false, watcher: null };
            live.set(dialog.id, state);

            // The server removes the dialog from the page and only afterwards tells us (release, one round trip later), and the browser
            // moves focus to <body> the moment the element that has it goes. So the removal itself is watched, and focus is put back at
            // once (1.2-FINAL-FIX) - release finds it already there.
            if (ref && typeof MutationObserver === "function" && document.body) {
                state.watcher = new MutationObserver(function () {
                    if (!dialog.isConnected) {
                        state.watcher.disconnect();
                        state.watcher = null;
                        if (!state.released) {
                            restore(state);
                        }
                    }
                });
                state.watcher.observe(document.body, { childList: true, subtree: true });
            }

            wire(dialog);
            moveFocus(dialog);
            return true;
        }

        return {
            present: function (id, ref) {
                return present(typeof id === "string" ? document.getElementById(id) : null, ref);
            },
            release: function (id) {
                const state = live.get(id);
                if (!state) {
                    return false;
                }

                state.released = true;
                live.delete(id);
                if (state.watcher) {
                    state.watcher.disconnect();
                    state.watcher = null;
                }

                // Disposed with the page still showing the dialog: the page is inert until it is closed, and focus cannot go back to
                // a control that is inert.
                const dialog = document.getElementById(id);
                if (dialog && dialog.tagName === "DIALOG" && dialog.open && typeof dialog.close === "function") {
                    dialog.close();
                }

                restore(state);
                return true;
            },
            openStatic: function (id) {
                const dialog = typeof id === "string" ? document.getElementById(id) : null;
                return present(dialog, null);
            },
            /* Whether any studio dialog is open as a modal right now - the shortcut sheet does not stack on one. */
            isOpen: function () {
                for (const state of live.values()) {
                    if (state.dialog.isConnected && state.dialog.open) {
                        return true;
                    }
                }

                return false;
            }
        };
    })();

    // The shortcut sheet's `?`, and the Close button of any dialog that carries one (1.2-A11Y). Both are plain markup hooks: the
    // sheet is part of the shell, so it works before the circuit connects and after it goes. `?` is ignored where it is a character
    // being typed - a text field, a textarea, a select, anything contenteditable, an ARIA textbox - and while a dialog is open.
    window.martenStudio.shortcuts = window.martenStudio.shortcuts || (function () {
        const nonText = ["button", "checkbox", "color", "file", "image", "radio", "range", "reset", "submit"];

        function isTyping(target) {
            if (!target || target.nodeType !== 1) {
                return false;
            }

            if (target.isContentEditable) {
                return true;
            }

            const field = target.closest("input, textarea, select, [contenteditable=''], [contenteditable='true'], [role='textbox'], [role='combobox'], [role='searchbox']");
            if (!field) {
                return false;
            }

            return field.tagName !== "INPUT" || nonText.indexOf((field.getAttribute("type") || "text").toLowerCase()) < 0;
        }

        // Shared with Find's `/` and Ctrl+K (below): the same question, so the two never disagree about where a key is a character.
        window.martenStudio.isTypingTarget = isTyping;

        document.addEventListener("keydown", function (event) {
            if (event.key !== "?" || event.ctrlKey || event.altKey || event.metaKey || event.defaultPrevented || event.isComposing) {
                return;
            }

            if (isTyping(event.target) || window.martenStudio.dialog.isOpen() || document.querySelector("dialog.ms-modal[open]")) {
                return;
            }

            if (window.martenStudio.dialog.openStatic("ms-shortcuts")) {
                event.preventDefault();
            }
        });

        document.addEventListener("click", function (event) {
            const target = event.target;
            const close = target && typeof target.closest === "function" ? target.closest("[data-ms-dialog-close]") : null;
            const dialog = close ? close.closest("dialog") : null;
            if (dialog && typeof dialog.close === "function") {
                dialog.close();
            }
        });

        return {};
    })();

    // Find by id (1.2). `/` and Ctrl+K (Cmd+K) focus the header's box, opening it first when it is the narrow screens' popover; Enter in
    // the box, Escape, and the arrow keys through the results are answered here too, so typing costs no round trip and a key is never
    // routed through the circuit. One document-level listener, registered once at load: it needs no .NET. Like `?` it is ignored where
    // the key is a character being typed (a text field, a textarea, a select, anything contenteditable, an ARIA textbox, the JSON
    // editor) and while a dialog is open.
    window.martenStudio.find = window.martenStudio.find || (function () {
        const hitSelector = ".ms-find-panel a.ms-find-hit";

        function input() {
            return document.getElementById("ms-find-input");
        }

        function popover() {
            return document.getElementById("ms-find-popover");
        }

        function isShown(element) {
            return !!element && element.getClientRects().length > 0;
        }

        function popoverIsOpen(element) {
            try {
                return !!element && element.matches(":popover-open");
            } catch (e) {
                return false;
            }
        }

        function call(ref, method, argument) {
            try {
                const pending = argument === undefined ? ref.invokeMethodAsync(method) : ref.invokeMethodAsync(method, argument);
                if (pending && typeof pending.catch === "function") {
                    pending.catch(function () { });
                }
            } catch (e) {
                // The circuit is gone; there is nothing to tell.
            }
        }

        function focusBox() {
            const field = input();
            if (!field) {
                return false;
            }

            const pop = popover();
            if (pop && !isShown(field) && typeof pop.showPopover === "function" && !popoverIsOpen(pop)) {
                try {
                    pop.showPopover();
                } catch (e) {
                    // A browser without popovers has no collapsed box either.
                }
            }

            if (!isShown(field)) {
                return false;
            }

            field.focus();
            try {
                field.select();
            } catch (e) {
                // A field that cannot select is still focused.
            }

            return true;
        }

        function hits() {
            return Array.prototype.slice.call(document.querySelectorAll(hitSelector));
        }

        function panelOpen() {
            return !!document.querySelector("[data-ms-find-panel], .ms-find-busy, .ms-find-error");
        }

        document.addEventListener("keydown", function (event) {
            if (event.defaultPrevented || event.isComposing || event.altKey) {
                return;
            }

            const slash = event.key === "/" && !event.ctrlKey && !event.metaKey;
            // `code` as well as `key`: on a layout whose letters are not Latin the K key reports another character.
            const chord = (event.key === "k" || event.key === "K" || event.code === "KeyK") && (event.ctrlKey || event.metaKey) && !event.shiftKey;
            if (!slash && !chord) {
                return;
            }

            if (window.martenStudio.isTypingTarget(event.target) || document.querySelector("dialog[open]")) {
                return;
            }

            // In the JSON viewer a row that has focus answers `/` itself (to its search box); Ctrl+K still reaches Find from there.
            if (slash && event.target && typeof event.target.closest === "function" && event.target.closest("[role='tree']")) {
                return;
            }

            if (focusBox()) {
                event.preventDefault();
            }
        });

        // Arrow keys through the results; Escape on a result puts the focus back in the box and closes the panel.
        document.addEventListener("keydown", function (event) {
            const target = event.target;
            const link = target && typeof target.closest === "function" ? target.closest(hitSelector) : null;
            if (!link || event.isComposing || event.ctrlKey || event.metaKey || event.altKey) {
                return;
            }

            const all = hits();
            const at = all.indexOf(link);
            const field = input();

            if (event.key === "ArrowDown") {
                event.preventDefault();
                if (at >= 0 && at < all.length - 1) {
                    all[at + 1].focus();
                }
            } else if (event.key === "ArrowUp") {
                event.preventDefault();
                if (at > 0) {
                    all[at - 1].focus();
                } else if (field) {
                    field.focus();
                }
            } else if (event.key === "Escape" && field) {
                event.preventDefault();
                field.focus();
                if (field.msFindRef) {
                    call(field.msFindRef, "CloseAsync");
                }
            }
        });

        // Following a result inside the narrow screens' popover closes it; the click itself is left alone for the router.
        document.addEventListener("click", function (event) {
            const target = event.target;
            if (target && typeof target.closest === "function" && target.closest(hitSelector)) {
                const pop = popover();
                if (popoverIsOpen(pop) && typeof pop.hidePopover === "function") {
                    try {
                        pop.hidePopover();
                    } catch (e) {
                        // Already closed.
                    }
                }
            }
        });

        return {
            /*
             * Enter in the box sends its live text to the component's [JSInvokable] FindAsync; Escape, while a panel is showing,
             * closes it with CloseAsync and keeps the text and the focus (a second Escape is the browser's own: it closes the
             * popover); ArrowDown moves to the first result. Idempotent per element, like enhanceRunField.
             */
            enhance: function (field, dotNetRef) {
                if (!field || !dotNetRef) {
                    return false;
                }

                field.msFindRef = dotNetRef;
                if (field.msFindHandlers) {
                    return true;
                }

                const onKeyDown = function (event) {
                    if (event.isComposing) {
                        return;
                    }

                    if (event.key === "Enter") {
                        event.preventDefault();
                        call(field.msFindRef, "FindAsync", field.value);
                    } else if (event.key === "Escape" && panelOpen()) {
                        event.preventDefault();
                        call(field.msFindRef, "CloseAsync");
                    } else if (event.key === "ArrowDown") {
                        const first = hits()[0];
                        if (first) {
                            event.preventDefault();
                            first.focus();
                        }
                    }
                };

                field.addEventListener("keydown", onKeyDown);
                field.msFindHandlers = { keydown: onKeyDown };
                return true;
            },

            release: function (field) {
                if (!field || !field.msFindHandlers) {
                    return false;
                }

                field.removeEventListener("keydown", field.msFindHandlers.keydown);
                delete field.msFindHandlers;
                delete field.msFindRef;
                return true;
            },

            /* Closes the narrow screens' popover after the box opened a result by itself. */
            dismiss: function () {
                const pop = popover();
                if (popoverIsOpen(pop) && typeof pop.hidePopover === "function") {
                    try {
                        pop.hidePopover();
                    } catch (e) {
                        // Already closed.
                    }
                }

                return true;
            },

            focus: focusBox
        };
    })();

    // The JSON viewer has one copy menu for every row, so the stylesheet cannot name its anchor the way a
    // MenuButton's own markup does: the anchor is whichever key button asked for the menu last. It is given the
    // name the menu's rule (`.ms-copy-menu:popover-open`) anchors to and taken off the one before, so there is
    // never more than one. A press on a key button gets here before the popover opens, because the capture
    // listener runs ahead of the button's default action.
    const menuAnchorName = "--ms-menu-invoker";
    let menuAnchored = null;

    function anchorMenuTo(element) {
        if (menuAnchored && menuAnchored !== element) {
            menuAnchored.style.removeProperty("anchor-name");
        }

        if (element && element.style) {
            element.style.setProperty("anchor-name", menuAnchorName);
        }

        menuAnchored = element || null;
    }

    // Both document-level click listeners below are registered once however many times this script is evaluated, like everything
    // else here (1.2-A11Y-FU): a second evaluation would otherwise run the key anchoring and the skip link twice per click.
    window.martenStudio.clickHooks = window.martenStudio.clickHooks || (function () {
        document.addEventListener("click", function (event) {
            const key = event.target && event.target.closest ? event.target.closest(".ms-json-key") : null;
            if (key) {
                anchorMenuTo(key);
            }
        }, true);

        // The skip link (1.1). Its href is a bare fragment, and the studio's document has a <base href> at its own root, so the
        // browser would resolve `#ms-main-content` against that base - the studio's front page - and Blazor would navigate there.
        // The click is handled here instead, in the capture phase and before Blazor's own listener sees it: the content region
        // takes focus (it has tabindex="-1" for exactly this) and scrolls into view, and nothing navigates.
        document.addEventListener("click", function (event) {
            const link = event.target && event.target.closest ? event.target.closest("a.ms-skip-link") : null;
            if (!link) {
                return;
            }

            const id = (link.getAttribute("href") || "").replace(/^#/, "");
            const target = id ? document.getElementById(id) : null;

            event.preventDefault();

            if (target) {
                target.focus({ preventScroll: true });
                target.scrollIntoView({ block: "start" });
            }
        }, true);

        return {};
    })();

    // A read-only text box that exists to be copied from (the header's copy-link fallback, 1.2) selects its text when it takes focus,
    // so the visitor's next key is Ctrl+C. Delegated, because the box is added to the page by the circuit long after this script ran,
    // and in the capture phase for the same reason as the click handlers above. Nothing happens to a box without the attribute.
    document.addEventListener("focusin", function (event) {
        const field = event.target;
        if (field && field.tagName === "INPUT" && field.getAttribute("data-ms-select-on-focus") === "true") {
            try {
                field.select();
            } catch (e) {
                // A field that cannot select (a type that does not support it) is still on the page to be selected by hand.
            }
        }
    }, true);

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
                    // Opened from the keyboard there is no key button being pressed, so the row that has focus
                    // is what the menu is placed against (see anchorMenuTo).
                    const row = document.activeElement && document.activeElement.closest
                        ? document.activeElement.closest("[role=\"treeitem\"]")
                        : null;
                    if (row) {
                        anchorMenuTo(row.querySelector(".ms-json-key") || row);
                    }

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

        /*
         * Ctrl+Enter in a one-line field that sits beside an editor (1.1): the same chord the editor answers, sent to the
         * component's [JSInvokable] method named by `methodName` with the field's live text, because the field binds on
         * `change`, which has not fired when the chord arrives. Idempotent per element, like enhanceEditor.
         */
        enhanceRunField: function (field, dotNetRef, methodName) {
            if (!field || !dotNetRef || !methodName || field.dataset.msRunFieldEnhanced === "true") {
                return false;
            }

            const onKeyDown = function (event) {
                if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
                    event.preventDefault();

                    try {
                        const pending = dotNetRef.invokeMethodAsync(methodName, field.value);

                        if (pending && typeof pending.catch === "function") {
                            pending.catch(function () { });
                        }
                    }
                    catch {
                    }
                }
            };

            field.addEventListener("keydown", onKeyDown);
            field.dataset.msRunFieldEnhanced = "true";
            field.msRunFieldHandlers = { keydown: onKeyDown };
            return true;
        },

        releaseRunField: function (field) {
            if (!field || !field.msRunFieldHandlers) {
                return false;
            }

            field.removeEventListener("keydown", field.msRunFieldHandlers.keydown);
            delete field.msRunFieldHandlers;
            delete field.dataset.msRunFieldEnhanced;
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
        },

        /*
         * The textarea's value, handed to .NET in chunks (the Plan page's pasted plan). A Blazor Server circuit accepts 32 KB a
         * message by default, a plan of thirty nodes is more than that, and a change event carrying one would end the circuit;
         * the studio must not raise a host's limit, so the text stays in the browser until the visitor asks and goes over as
         * BeginPaste, then PasteChunk for each chunk, in order. Answers the number of characters sent, -1 for text longer
         * than `maxCharacters` (nothing is sent), and 0 when there is nothing or the circuit went away mid-way.
         */
        sendInChunks: async function (textarea, dotNetRef, chunkCharacters, maxCharacters) {
            try {
                const value = textarea && typeof textarea.value === "string" ? textarea.value : "";

                if (value.length > maxCharacters) {
                    return -1;
                }

                if (value.length === 0) {
                    return 0;
                }

                const size = Math.max(1000, chunkCharacters | 0);
                await dotNetRef.invokeMethodAsync("BeginPaste");

                for (let start = 0; start < value.length;) {
                    let end = Math.min(value.length, start + size);

                    // Never cut a surrogate pair in two: a lone half is not text a message can carry.
                    if (end < value.length) {
                        const last = value.charCodeAt(end - 1);

                        if (last >= 0xD800 && last <= 0xDBFF) {
                            end++;
                        }
                    }

                    await dotNetRef.invokeMethodAsync("PasteChunk", value.substring(start, end));
                    start = end;
                }

                return value.length;
            }
            catch {
                return 0;
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

    // Beside download.text; additive. A stream from .NET (DotNetStreamReference, sent in chunks over the circuit) saved as a file: for a file
    // too large to pass as one interop argument. Returning false is a valid answer, never an exception.
    if (typeof window.martenStudio.download.stream !== "function") {
        window.martenStudio.download.stream = async function (fileName, mime, streamRef) {
            try {
                const buffer = await streamRef.arrayBuffer();
                const blob = new Blob([buffer], { type: mime || "application/octet-stream" });
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
        };
    }

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

    // Whether a <details> is open. Its `toggle` event carries no state, and the browser opens one without being asked (a click, the
    // keyboard, find-in-page showing a match), so a component that remembers the state asks the element when the event arrives.
    if (typeof window.martenStudio.disclosure.isOpen !== "function") {
        window.martenStudio.disclosure.isOpen = function (element) {
            try {
                return !!element && element.open === true;
            }
            catch {
                return false;
            }
        };
    }

    // The edge cue on a table that scrolls sideways (UX-1).
    //
    // The scrolling itself is `overflow-x: auto` in studio.css and needs nothing from here. What
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

    // The plan page's icicle view (1.0-P3B): a focused frame moves on the arrow keys and selects on Enter or Space, which the circuit
    // hears through the frame's own @onkeydown. What it cannot do is stop the browser's own scrolling under those keys - Razor
    // decides `@onkeydown:preventDefault` when the component renders, not per key, and preventing every key would take Tab away. So
    // this stops the arrows and Space and nothing else, and only when a frame itself has the focus. One listener on the document,
    // idempotent like everything here.
    window.martenStudio.icicleKeys = window.martenStudio.icicleKeys || (function () {
        const captured = ["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", " ", "Spacebar"];

        document.addEventListener("keydown", function (event) {
            if (event.ctrlKey || event.altKey || event.metaKey || captured.indexOf(event.key) < 0) {
                return;
            }

            const target = event.target;
            if (target && target.nodeType === 1 && typeof target.matches === "function" && target.matches(".ms-plan-icicle-frame")) {
                event.preventDefault();
            }
        });

        return {};
    })();

    // The health advisor's "mute in this browser" (H1): one JSON object in localStorage, and nowhere else - not
    // the cookie martenStudio.prefs falls back to, because a mute is nothing the server needs on every request.
    // It is {key, muted} (wave 6): "key" is this browser's random HMAC key as the host's Data Protection protected
    // it, and "muted" the HMAC-SHA256 (lowercase hex) of each mute key under it - never a mute key itself, which can
    // name a tenant or a stream, and never the HMAC key in a form this side could use, because every script on
    // this origin can read localStorage. The keying and hashing are the circuit's (HealthMutes, HealthMuteKeys);
    // this side only keeps what it is handed. Every access is in a try: a private window or blocked storage
    // answers as "nothing muted", and the page is correct either way.
    window.martenStudio.health = window.martenStudio.health || {
        getMuted: function () {
            try {
                return window.localStorage.getItem("ms_health_muted");
            }
            catch {
                return null;
            }
        },
        setMuted: function (json) {
            try {
                window.localStorage.setItem("ms_health_muted", typeof json === "string" ? json : "[]");
            }
            catch {
            }
        }
    };

    // A finding's address ends in its card's fragment (`health?store=x#finding-c08-temp-files`, issue #33), and the
    // browser looks for that element when the document arrives, before the circuit has read the sweep and drawn the
    // cards, so it finds nothing and leaves the page at the top. The page calls this once its report is on screen:
    // it opens whatever <details> holds the card (a muted finding sits in a closed one), scrolls the card to the top of
    // the view and marks it with `ms-health-finding-linked` for two seconds, so the eye finds it. Answers whether the
    // card was there, so the page can say when it is not.
    //
    // A promise, not a value: the render batch that draws the cards and this call travel the same connection in order,
    // so the card is normally already in the document, but a call that raced the batch should not be a missing finding.
    // It looks again for a second and a half, on a timer rather than a frame (a background tab throttles frames), and
    // then gives up. Every step is in a try: scrolling and marking are courtesies, and the page is correct without them.
    if (typeof window.martenStudio.health.revealFinding !== "function") {
        let markedCard = null;
        let markTimer = 0;

        const findingCard = function (id) {
            try {
                return typeof id === "string" && id.length > 0 ? document.getElementById(id) : null;
            } catch (e) {
                return null;
            }
        };

        const revealCard = function (card) {
            try {
                for (let details = card.closest("details"); details; details = details.parentElement ? details.parentElement.closest("details") : null) {
                    details.open = true;
                }

                card.scrollIntoView({ block: "start", behavior: "auto" });

                if (markedCard) {
                    markedCard.classList.remove("ms-health-finding-linked");
                }

                window.clearTimeout(markTimer);
                markedCard = card;
                card.classList.add("ms-health-finding-linked");
                markTimer = window.setTimeout(function () {
                    card.classList.remove("ms-health-finding-linked");
                    if (markedCard === card) {
                        markedCard = null;
                    }
                }, 2000);
            } catch (e) {
                // Nothing to do: the card is on the page, which is what the answer says.
            }

            return true;
        };

        window.martenStudio.health.revealFinding = function (id) {
            return new Promise(function (resolve) {
                const started = Date.now();

                const look = function () {
                    const card = findingCard(id);
                    if (card) {
                        resolve(revealCard(card));
                    } else if (Date.now() - started >= 1500) {
                        resolve(false);
                    } else {
                        window.setTimeout(look, 50);
                    }
                };

                look();
            });
        };
    }
})();
