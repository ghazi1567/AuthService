// Progressive enhancement for the server-rendered pages. Every page works without this file: it only adds a show/hide password
// button, a busy button while a form is sent, and the live countdown on a rate-limited button.

(function () {
    // Show or hide a password. The button is rendered hidden and only appears once this script has loaded.
    function revealToggles() {
        document.querySelectorAll("[data-toggle-password], [data-needs-script]").forEach(function (button) {
            button.hidden = false;
        });
    }

    document.addEventListener("click", function (event) {
        var button = event.target.closest("[data-toggle-password]");
        if (!button) {
            return;
        }

        var input = document.getElementById(button.getAttribute("data-toggle-password"));
        if (!input) {
            return;
        }

        var show = input.type === "password";
        input.type = show ? "text" : "password";
        button.setAttribute("aria-pressed", show ? "true" : "false");
        button.setAttribute("aria-label", show ? "Hide password" : "Show password");
        button.querySelectorAll("[data-when]").forEach(function (icon) {
            icon.hidden = icon.getAttribute("data-when") !== (show ? "shown" : "hidden");
        });
    });

    // Busy button: the first send of a form shows the loading label and blocks a second click.
    document.addEventListener("submit", function (event) {
        var button = event.target.querySelector("[data-busy-label]");
        if (!button || event.defaultPrevented) {
            return;
        }

        if (button.getAttribute("aria-busy") === "true") {
            event.preventDefault();
            return;
        }

        button.setAttribute("aria-busy", "true");
        button.textContent = button.getAttribute("data-busy-label");
    });

    // Countdown: a button that waits out a rate limit. It is only disabled here, so without this script the person can still try.
    function startCountdowns() {
        document.querySelectorAll("[data-countdown]").forEach(function (button) {
            if (button.getAttribute("data-countdown-started")) {
                return;
            }

            button.setAttribute("data-countdown-started", "true");
            var remaining = parseInt(button.getAttribute("data-countdown"), 10);
            var label = button.getAttribute("data-countdown-label") || "Try again in {0}s";
            var done = button.getAttribute("data-countdown-done") || "Try again";
            var announce = document.getElementById(button.getAttribute("data-countdown-announce"));
            if (!(remaining > 0)) {
                return;
            }

            button.disabled = true;
            button.textContent = label.replace("{0}", remaining);

            var timer = setInterval(function () {
                remaining -= 1;
                if (remaining > 0) {
                    button.textContent = label.replace("{0}", remaining);
                    return;
                }

                clearInterval(timer);
                button.disabled = false;
                button.textContent = done;
                if (announce) {
                    // Announced once, politely, when the wait is over; not every second.
                    announce.textContent = button.getAttribute("data-countdown-message") || "You can try again now.";
                }
            }, 1000);
        });
    }

    // Password rules: each row ticks over as the password field changes. The text says met or not, so colour is never the only signal.
    function updateRules(input) {
        document.querySelectorAll('[data-rules-for="' + input.id + '"] [data-min-length]').forEach(function (rule) {
            var met = input.value.length >= parseInt(rule.getAttribute("data-min-length"), 10);
            rule.classList.toggle("is-met", met);
            rule.querySelector('[data-state="unmet"]').hidden = met;
            rule.querySelector('[data-state="met"]').hidden = !met;
            rule.querySelector("[data-status]").textContent = met ? "(met)" : "(not met yet)";
        });
    }

    document.addEventListener("input", function (event) {
        if (event.target.id) {
            updateRules(event.target);
        }
    });

    function startRules() {
        document.querySelectorAll("[data-rules-for]").forEach(function (list) {
            var input = document.getElementById(list.getAttribute("data-rules-for"));
            if (input) {
                updateRules(input);
            }
        });
    }

    // A page opened from an email link holds its token only in a form. The token is taken out of the address bar and the history entry
    // first, then (for a form with nothing to fill in) the form is sent: until then nothing else sees it, and the link cannot be bookmarked or shared by accident.
    function startLinkForms() {
        document.querySelectorAll("form[data-link-form]").forEach(function (form) {
            if (form.getAttribute("data-link-started")) {
                return;
            }

            form.setAttribute("data-link-started", "true");
            window.history.replaceState(null, "", window.location.pathname);

            // "auto" forms hold nothing for the person to fill in, so they are sent at once; "clean" ones wait for the person.
            if (form.getAttribute("data-link-form") === "auto") {
                form.requestSubmit();
            }
        });
    }

    // Unsaved-changes guard: a form marked data-unsaved-guard warns before the page is left once something was changed, but not when it is sent.
    function startUnsavedGuards() {
        document.querySelectorAll("form[data-unsaved-guard]").forEach(function (form) {
            if (form.getAttribute("data-guard-started")) {
                return;
            }

            form.setAttribute("data-guard-started", "true");
            var changed = false;
            var sending = false;
            form.addEventListener("input", function () { changed = true; });
            form.addEventListener("change", function () { changed = true; });
            form.addEventListener("submit", function () { sending = true; });
            window.addEventListener("beforeunload", function (event) {
                if (changed && !sending) {
                    event.preventDefault();
                    event.returnValue = "";
                }
            });
        });
    }

    // Copy a value to the clipboard. The button is rendered hidden and only appears once this script has loaded.
    document.addEventListener("click", function (event) {
        var button = event.target.closest("[data-copy-from]");
        if (!button) {
            return;
        }

        var source = document.getElementById(button.getAttribute("data-copy-from"));
        var status = document.querySelector("[data-copy-status]");
        if (!source || !navigator.clipboard) {
            if (source) {
                source.select();
            }

            return;
        }

        navigator.clipboard.writeText(source.value).then(function () {
            if (status) {
                status.textContent = "Copied to clipboard";
            }
        });
    });

    // Load a text file into the box named by data-load-into (the permission list when registering a product). Without this script the
    // person pastes the text instead; the file never leaves the browser until the form is sent.
    document.addEventListener("change", function (event) {
        var input = event.target.closest("[data-load-into]");
        if (!input || !input.files || input.files.length === 0) {
            return;
        }

        var target = document.getElementById(input.getAttribute("data-load-into"));
        if (!target) {
            return;
        }

        var reader = new FileReader();
        reader.onload = function () {
            target.value = String(reader.result || "");
            target.dispatchEvent(new Event("input", { bubbles: true }));
        };
        reader.readAsText(input.files[0]);
    });

    function init() {
        revealToggles();
        startCountdowns();

        // A form that carries on by itself (the product picker with one product, and the hand-off). Without scripts it keeps its Continue button.
        document.querySelectorAll("form[data-autosubmit]").forEach(function (form) {
            if (!form.getAttribute("data-autosubmit-done")) {
                form.setAttribute("data-autosubmit-done", "true");
                form.submit();
            }
        });
        startRules();
        startLinkForms();
        startUnsavedGuards();
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }

    // Blazor's enhanced navigation swaps the page body without a reload.
    if (window.Blazor && window.Blazor.addEventListener) {
        window.Blazor.addEventListener("enhancedload", init);
    }
})();
