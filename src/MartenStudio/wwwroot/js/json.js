/*
 * Marten Studio - JSON toolkit browser helpers (packet W1).
 *
 * These four functions are the only things the JSON toolkit cannot do from the server, and every C#
 * call site wraps them in try/catch so that the toolkit works without this file at all: the copy menu is
 * still reachable with the mouse through its popovertarget button, Tab still moves focus, the gutter
 * still lines up for anything that fits without scrolling, and Save still reads the bound value.
 *
 * This packet does not own wwwroot/MartenStudio.lib.module.js, so the functions live here. The
 * orchestrator should fold the body of registerJsonHelpers() into that module's afterWebStarted() - the
 * same idempotent `window.martenStudio.x = window.martenStudio.x || {...}` shape it already uses - and
 * then delete this file.
 */

export function registerJsonHelpers() {
    window.martenStudio = window.martenStudio || {};

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
}

if (typeof window !== "undefined" && window.document) {
    registerJsonHelpers();
}
