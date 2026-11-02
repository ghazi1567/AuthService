// Progressive enhancement for the server-rendered pages. Every page works without this file: it only adds a show/hide password
// button, a busy button while a form is sent, and the live countdown on a rate-limited button.

(function () {
    // Show or hide a password. The button is rendered hidden and only appears once this script has loaded.
    function revealToggles() {
        document.querySelectorAll("[data-toggle-password]").forEach(function (button) {
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

    function init() {
        revealToggles();
        startCountdowns();
        startRules();
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
