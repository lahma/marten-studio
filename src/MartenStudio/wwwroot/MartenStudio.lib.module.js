export function afterWebStarted() {
    window.martenStudio = window.martenStudio || {};

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

    // Page visibility, so a page that polls can stop while nobody is looking at it. One listener for the
    // whole document however many components ask; the .NET references are held in a set and the listener
    // is removed again once the last one goes, so a circuit that closes leaves nothing behind.
    window.martenStudio.visibility = window.martenStudio.visibility || (function () {
        const watchers = new Set();
        let listening = false;

        function notify() {
            const hidden = document.hidden === true;
            for (const watcher of watchers) {
                try {
                    watcher.invokeMethodAsync("OnVisibilityChanged", hidden);
                }
                catch {
                    // The circuit went away without unwatching; drop it rather than throwing on every
                    // visibility change for the rest of the page's life.
                    watchers.delete(watcher);
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
            watch: function (dotNetRef) {
                if (!dotNetRef || watchers.has(dotNetRef)) {
                    return document.hidden === true;
                }

                watchers.add(dotNetRef);
                start();
                return document.hidden === true;
            },
            unwatch: function (dotNetRef) {
                watchers.delete(dotNetRef);
                if (watchers.size === 0) {
                    stop();
                }
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
            try {
                if (element && typeof element.showPopover === "function") {
                    element.showPopover();
                    return true;
                }
            }
            catch {
            }

            return false;
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
         * Soft tabs and a gutter that scrolls with the text. Idempotent: calling it twice on the same
         * textarea attaches nothing twice.
         */
        enhanceTextarea: function (textarea, gutter) {
            if (!textarea || textarea.dataset.msEnhanced === "true") {
                return false;
            }

            const onKeyDown = function (event) {
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
