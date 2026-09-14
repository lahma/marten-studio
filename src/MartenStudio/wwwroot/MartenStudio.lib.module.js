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
}
