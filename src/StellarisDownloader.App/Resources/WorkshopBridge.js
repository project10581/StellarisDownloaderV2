(function () {
    "use strict";

    const integrationKey = "__stellarisDownloaderWorkshopBridge";
    const buttonClass = "stellaris-downloader-queue-button";
    const numericIdPattern = /^[0-9]+$/;

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

    function addButton(link, workshopId) {
        const card = findCard(link);
        if (!(card instanceof HTMLElement) || card.querySelector(`:scope > .${buttonClass}`)) {
            return;
        }

        if (window.getComputedStyle(card).position === "static") {
            card.style.position = "relative";
        }

        const button = document.createElement("button");
        button.type = "button";
        button.className = buttonClass;
        button.textContent = "+";
        button.title = "Add to Stellaris Downloader queue";
        button.setAttribute("aria-label", button.title);
        Object.assign(button.style, {
            position: "absolute",
            top: "8px",
            right: "8px",
            width: "34px",
            height: "34px",
            border: "0",
            borderRadius: "6px",
            background: "#2f8ef3",
            color: "#ffffff",
            fontSize: "22px",
            fontWeight: "700",
            cursor: "pointer",
            zIndex: "2147483647",
            boxShadow: "0 4px 12px rgba(0, 0, 0, 0.35)"
        });
        button.addEventListener("click", function (event) {
            event.preventDefault();
            event.stopPropagation();
            sendWorkshopId(workshopId);
        }, true);
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

    window[integrationKey] = { observer: observer };
    injectButtons();
}());
