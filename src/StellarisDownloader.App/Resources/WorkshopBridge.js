(function () {
    "use strict";

    const integrationKey = "__stellarisDownloaderWorkshopBridge";
    const buttonClass = "stellaris-downloader-queue-button";
    const numericIdPattern = /^[0-9]+$/;
    const queuedIds = new Set();
    const installedIds = new Set();

    function isTrustedHost(hostname) {
        const normalized = String(hostname || "").toLowerCase().replace(/\.$/, "");
        return normalized === "steamcommunity.com" || normalized.endsWith(".steamcommunity.com");
    }

    function workshopIdFromUrl(rawUrl) {
        try {
            const parsed = new URL(rawUrl, window.location.href);
            if (parsed.protocol !== "https:" || !isTrustedHost(parsed.hostname)) {
                return null;
            }

            const values = parsed.searchParams.getAll("id");
            return values.length === 1 && numericIdPattern.test(values[0]) ? values[0] : null;
        } catch {
            return null;
        }
    }

    function sendWorkshopId(workshopId) {
        if (!numericIdPattern.test(workshopId) || !window.chrome || !window.chrome.webview) {
            return;
        }

        window.chrome.webview.postMessage({
            type: "enqueueWorkshopIds",
            ids: [workshopId]
        });
    }

    function findCard(link) {
        return link.closest("[data-publishedfileid], .workshopItem, .workshopBrowseItem, .workshopItemSubscription")
            || link.parentElement;
    }

    function applyButtonState(button, workshopId) {
        button.dataset.workshopId = workshopId;
        if (installedIds.has(workshopId)) {
            button.textContent = "✓";
            button.title = "Already installed";
            button.style.background = "#48b64a";
            button.style.cursor = "default";
            button.disabled = true;
            button.setAttribute("aria-label", button.title);
            return;
        }

        if (queuedIds.has(workshopId)) {
            button.textContent = "✓";
            button.title = "Already in the Stellaris Downloader queue";
            button.style.background = "#d98412";
            button.style.cursor = "default";
            button.disabled = true;
            button.setAttribute("aria-label", button.title);
            return;
        }

        button.textContent = "+";
        button.title = "Add to Stellaris Downloader queue";
        button.style.background = "#2f8ef3";
        button.style.cursor = "pointer";
        button.disabled = false;
        button.setAttribute("aria-label", button.title);
    }

    function refreshButtonStates() {
        document.querySelectorAll(`.${buttonClass}`).forEach(function (button) {
            const workshopId = button.dataset.workshopId;
            if (workshopId && numericIdPattern.test(workshopId)) {
                applyButtonState(button, workshopId);
            }
        });
    }

    function replaceIds(target, values) {
        target.clear();
        if (!Array.isArray(values)) {
            return;
        }

        values.forEach(function (value) {
            if (typeof value === "string" && numericIdPattern.test(value)) {
                target.add(value);
            }
        });
    }

    function addButton(link, workshopId) {
        const card = findCard(link);
        if (!(card instanceof HTMLElement)) {
            return;
        }

        const existing = card.querySelector(`:scope > .${buttonClass}`);
        if (existing instanceof HTMLButtonElement) {
            applyButtonState(existing, workshopId);
            return;
        }

        if (window.getComputedStyle(card).position === "static") {
            card.style.position = "relative";
        }

        const button = document.createElement("button");
        button.type = "button";
        button.className = buttonClass;
        Object.assign(button.style, {
            position: "absolute",
            top: "8px",
            right: "8px",
            width: "34px",
            height: "34px",
            border: "0",
            borderRadius: "6px",
            color: "#ffffff",
            fontSize: "22px",
            fontWeight: "700",
            zIndex: "2147483647",
            boxShadow: "0 4px 12px rgba(0, 0, 0, 0.35)"
        });
        button.addEventListener("click", function (event) {
            event.preventDefault();
            event.stopPropagation();
            if (installedIds.has(workshopId) || queuedIds.has(workshopId)) {
                return;
            }

            queuedIds.add(workshopId);
            refreshButtonStates();
            sendWorkshopId(workshopId);
        }, true);
        applyButtonState(button, workshopId);
        card.appendChild(button);
    }

    function injectButtons() {
        if (window.location.protocol !== "https:" || !isTrustedHost(window.location.hostname)) {
            return;
        }

        const links = document.querySelectorAll(
            'a[href*="sharedfiles/filedetails"], a[href*="/workshop/filedetails"]'
        );
        links.forEach(function (link) {
            const workshopId = workshopIdFromUrl(link.href);
            if (workshopId) {
                addButton(link, workshopId);
            }
        });
    }

    const previous = window[integrationKey];
    if (previous && previous.observer) {
        previous.observer.disconnect();
    }

    let timer = null;
    const observer = new MutationObserver(function () {
        if (timer !== null) {
            return;
        }

        timer = window.setTimeout(function () {
            timer = null;
            injectButtons();
        }, 150);
    });

    if (document.body) {
        observer.observe(document.body, { childList: true, subtree: true });
    }

    window[integrationKey] = {
        observer: observer,
        syncState: function (state) {
            const value = state && typeof state === "object" ? state : {};
            replaceIds(queuedIds, value.queuedIds);
            replaceIds(installedIds, value.installedIds);
            refreshButtonStates();
            injectButtons();
        }
    };
    injectButtons();
}());
