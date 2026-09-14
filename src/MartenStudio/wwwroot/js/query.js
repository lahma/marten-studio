/*
 * Marten Studio - the Query page's editor key handler.
 *
 * This file exists to be folded into wwwroot/MartenStudio.lib.module.js: the body of
 * installQueryModule() goes inside afterWebStarted(), exactly as the JSON toolkit's helpers were. It is
 * written in the same idempotent shape, so folding it is a copy rather than an edit, and so it can be
 * loaded on its own in the meantime without installing anything twice.
 *
 * Why JavaScript at all, when Blazor has @onkeydown: a keydown binding on a textarea sends one round
 * trip per keystroke over the circuit and re-renders the component each time, which turns typing a query
 * into a latency test. This listener runs in the browser and only calls back on the three chords that
 * mean something - and it carries the textarea's live value with it, because the C# side binds on
 * `change`, which has not fired yet when Ctrl+Enter arrives.
 */
export function afterWebStarted() {
    installQueryModule();
}

export function installQueryModule() {
    window.martenStudio = window.martenStudio || {};

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
}
