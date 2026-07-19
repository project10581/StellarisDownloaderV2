(function () {
    "use strict";

    const integrationKey = "__stellarisDownloaderWorkshopBridge";
    const buttonClass = "stellaris-downloader-queue-button";
    const overlayHostId = "stellaris-downloader-workshop-overlay";
    const detailLinkSelector =
        'a[href*="sharedfiles/filedetails"], a[href*="/workshop/filedetails"]';
    const numericIdPattern = /^[0-9]+$/;
    const queuedIds = new Set();
    const installedIds = new Set();
    const recordsByTarget = new Map();
    const buttonSize = 30;
    const buttonInset = 6;
    let overlayHost = null;
    let overlayRoot = null;
    let observer = null;
    let reconcileTimer = null;
    let positionFrame = null;
    let positionInterval = null;
    let disposed = false;

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

    function elementArea(element) {
        const rectangle = element.getBoundingClientRect();
        const renderedArea = rectangle.width * rectangle.height;
        const naturalArea = element instanceof HTMLImageElement
            ? element.naturalWidth * element.naturalHeight
            : 0;
        return Math.max(renderedArea, naturalArea);
    }

    function isLargeEnoughForOverlay(element) {
        const rectangle = element.getBoundingClientRect();
        if (rectangle.width >= 48 && rectangle.height >= 48) {
            return true;
        }

        return element instanceof HTMLImageElement
            && element.naturalWidth >= 48
            && element.naturalHeight >= 48;
    }

    function findPreviewTarget(link) {
        const media = Array.from(link.querySelectorAll("img, video"))
            .filter(isLargeEnoughForOverlay)
            .sort(function (left, right) {
                return elementArea(right) - elementArea(left);
            });
        if (media.length > 0) {
            return media[0];
        }

        const hintedPreview = link.querySelector(
            '[class*="preview"], [class*="Preview"], '
            + '[class*="thumbnail"], [class*="Thumbnail"], '
            + '[class*="thumb"], [class*="Thumb"]'
        );
        if (hintedPreview instanceof HTMLElement && isLargeEnoughForOverlay(hintedPreview)) {
            return hintedPreview;
        }

        const linkHint = `${link.className || ""} ${link.getAttribute("data-tooltip-text") || ""}`;
        return /(preview|thumbnail|thumb)/i.test(linkHint) && isLargeEnoughForOverlay(link)
            ? link
            : null;
    }

    function collectPreviewTargets() {
        const targets = new Map();
        document.querySelectorAll(detailLinkSelector).forEach(function (link) {
            if (!(link instanceof HTMLAnchorElement)) {
                return;
            }

            const workshopId = workshopIdFromUrl(link.href);
            const target = findPreviewTarget(link);
            if (workshopId && target instanceof HTMLElement && target.isConnected) {
                targets.set(target, workshopId);
            }
        });
        return targets;
    }

    function removeLegacyButtons() {
        document.querySelectorAll(`.${buttonClass}`).forEach(function (button) {
            button.remove();
        });
    }

    function ensureOverlayRoot() {
        if (overlayRoot || !document.body) {
            return overlayRoot;
        }

        const existingHost = document.getElementById(overlayHostId);
        if (existingHost) {
            existingHost.remove();
        }

        overlayHost = document.createElement("div");
        overlayHost.id = overlayHostId;
        overlayHost.setAttribute("aria-hidden", "false");
        Object.assign(overlayHost.style, {
            position: "fixed",
            inset: "0",
            width: "100vw",
            height: "100vh",
            overflow: "visible",
            pointerEvents: "none",
            zIndex: "2147483647"
        });
        overlayRoot = overlayHost.attachShadow({ mode: "open" });

        const style = document.createElement("style");
        style.textContent = `
            .${buttonClass} {
                all: initial;
                position: absolute;
                display: inline-flex;
                align-items: center;
                justify-content: center;
                box-sizing: border-box;
                width: ${buttonSize}px;
                min-width: ${buttonSize}px;
                height: ${buttonSize}px;
                min-height: ${buttonSize}px;
                margin: 0;
                padding: 0;
                border: 1px solid rgba(255, 255, 255, 0.38);
                border-radius: 7px;
                color: #ffffff;
                font-family: "Segoe UI", sans-serif;
                font-size: 20px;
                font-weight: 700;
                line-height: 1;
                text-align: center;
                text-decoration: none;
                cursor: pointer;
                pointer-events: auto;
                user-select: none;
                box-shadow: 0 3px 10px rgba(0, 0, 0, 0.42);
            }

            .${buttonClass}:focus-visible {
                outline: 2px solid #ffffff;
                outline-offset: 2px;
            }

            .${buttonClass}:disabled {
                opacity: 0.96;
            }
        `;
        overlayRoot.appendChild(style);
        document.body.appendChild(overlayHost);
        return overlayRoot;
    }

    function getButtonState(workshopId) {
        if (installedIds.has(workshopId)) {
            return {
                key: "installed",
                text: "\u2713",
                title: "Already installed",
                background: "#48b64a",
                disabled: true
            };
        }

        if (queuedIds.has(workshopId)) {
            return {
                key: "queued",
                text: "\u2713",
                title: "Already in the Stellaris Downloader queue",
                background: "#d98412",
                disabled: true
            };
        }

        return {
            key: "available",
            text: "+",
            title: "Add to Stellaris Downloader queue",
            background: "#2f8ef3",
            disabled: false
        };
    }

    function applyButtonState(button, workshopId) {
        const state = getButtonState(workshopId);
        if (button.dataset.workshopId === workshopId && button.dataset.state === state.key) {
            return;
        }

        button.dataset.workshopId = workshopId;
        button.dataset.state = state.key;
        button.textContent = state.text;
        button.title = state.title;
        button.style.background = state.background;
        button.style.cursor = state.disabled ? "default" : "pointer";
        button.disabled = state.disabled;
        button.setAttribute("aria-label", state.title);
    }

    function createButton(target, workshopId) {
        const root = ensureOverlayRoot();
        if (!root) {
            return;
        }

        const button = document.createElement("button");
        button.type = "button";
        button.className = buttonClass;
        button.addEventListener("click", function (event) {
            event.preventDefault();
            event.stopImmediatePropagation();
            const currentId = button.dataset.workshopId || "";
            if (!numericIdPattern.test(currentId)
                || installedIds.has(currentId)
                || queuedIds.has(currentId)) {
                return;
            }

            queuedIds.add(currentId);
            refreshButtonStates();
            sendWorkshopId(currentId);
        }, true);
        applyButtonState(button, workshopId);
        root.appendChild(button);
        recordsByTarget.set(target, { target: target, button: button });
    }

    function refreshButtonStates() {
        recordsByTarget.forEach(function (record) {
            const workshopId = record.button.dataset.workshopId;
            if (workshopId && numericIdPattern.test(workshopId)) {
                applyButtonState(record.button, workshopId);
            }
        });
    }

    function intersectRect(rectangle, bounds) {
        return {
            left: Math.max(rectangle.left, bounds.left),
            top: Math.max(rectangle.top, bounds.top),
            right: Math.min(rectangle.right, bounds.right),
            bottom: Math.min(rectangle.bottom, bounds.bottom)
        };
    }

    function visibleRectangle(target) {
        let visible = intersectRect(target.getBoundingClientRect(), {
            left: 0,
            top: 0,
            right: window.innerWidth,
            bottom: window.innerHeight
        });
        let ancestor = target.parentElement;
        while (ancestor && ancestor !== document.body) {
            const style = window.getComputedStyle(ancestor);
            const clipsHorizontally = /(auto|scroll|hidden|clip)/.test(style.overflowX);
            const clipsVertically = /(auto|scroll|hidden|clip)/.test(style.overflowY);
            if (clipsHorizontally || clipsVertically) {
                const bounds = ancestor.getBoundingClientRect();
                visible = {
                    left: clipsHorizontally ? Math.max(visible.left, bounds.left) : visible.left,
                    top: clipsVertically ? Math.max(visible.top, bounds.top) : visible.top,
                    right: clipsHorizontally ? Math.min(visible.right, bounds.right) : visible.right,
                    bottom: clipsVertically ? Math.min(visible.bottom, bounds.bottom) : visible.bottom
                };
            }
            ancestor = ancestor.parentElement;
        }
        return visible;
    }

    function positionButtons() {
        recordsByTarget.forEach(function (record) {
            const visible = record.target.isConnected ? visibleRectangle(record.target) : null;
            if (!visible
                || visible.right - visible.left < buttonSize + buttonInset
                || visible.bottom - visible.top < buttonSize + buttonInset) {
                record.button.style.display = "none";
                return;
            }

            record.button.style.display = "inline-flex";
            record.button.style.left = `${Math.round(visible.right - buttonSize - buttonInset)}px`;
            record.button.style.top = `${Math.round(visible.top + buttonInset)}px`;
        });
    }

    function schedulePosition() {
        if (disposed || positionFrame !== null) {
            return;
        }

        positionFrame = window.requestAnimationFrame(function () {
            positionFrame = null;
            positionButtons();
        });
    }

    function reconcileTargets() {
        if (disposed) {
            return;
        }

        removeLegacyButtons();
        const targets = collectPreviewTargets();
        recordsByTarget.forEach(function (record, target) {
            if (!targets.has(target)) {
                record.button.remove();
                recordsByTarget.delete(target);
            }
        });
        targets.forEach(function (workshopId, target) {
            const existing = recordsByTarget.get(target);
            if (existing) {
                applyButtonState(existing.button, workshopId);
            } else {
                createButton(target, workshopId);
            }
        });
        schedulePosition();
    }

    function scheduleReconcile() {
        if (disposed || reconcileTimer !== null) {
            return;
        }

        reconcileTimer = window.setTimeout(function () {
            reconcileTimer = null;
            reconcileTargets();
        }, 120);
    }

    function dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        if (observer) {
            observer.disconnect();
        }
        if (reconcileTimer !== null) {
            window.clearTimeout(reconcileTimer);
        }
        if (positionFrame !== null) {
            window.cancelAnimationFrame(positionFrame);
        }
        if (positionInterval !== null) {
            window.clearInterval(positionInterval);
        }
        window.removeEventListener("scroll", schedulePosition, true);
        window.removeEventListener("resize", schedulePosition);
        document.removeEventListener("load", scheduleReconcile, true);
        document.removeEventListener("DOMContentLoaded", start);
        recordsByTarget.clear();
        if (overlayHost) {
            overlayHost.remove();
        }
        overlayHost = null;
        overlayRoot = null;
        removeLegacyButtons();
    }

    function start() {
        if (disposed || !document.body) {
            return;
        }

        observer = new MutationObserver(scheduleReconcile);
        observer.observe(document.body, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ["href", "src", "class"]
        });
        window.addEventListener("scroll", schedulePosition, true);
        window.addEventListener("resize", schedulePosition);
        document.addEventListener("load", scheduleReconcile, true);
        positionInterval = window.setInterval(schedulePosition, 500);
        reconcileTargets();
    }

    const previous = window[integrationKey];
    let previousState = null;
    if (previous && typeof previous.snapshot === "function") {
        previousState = previous.snapshot();
    }
    if (previous && typeof previous.dispose === "function") {
        previous.dispose();
    } else if (previous && previous.observer) {
        previous.observer.disconnect();
    }
    removeLegacyButtons();
    replaceIds(queuedIds, previousState && previousState.queuedIds);
    replaceIds(installedIds, previousState && previousState.installedIds);

    window[integrationKey] = {
        get observer() {
            return observer;
        },
        syncState: function (state) {
            const value = state && typeof state === "object" ? state : {};
            replaceIds(queuedIds, value.queuedIds);
            replaceIds(installedIds, value.installedIds);
            refreshButtonStates();
            scheduleReconcile();
        },
        snapshot: function () {
            return {
                queuedIds: Array.from(queuedIds),
                installedIds: Array.from(installedIds)
            };
        },
        dispose: dispose
    };

    if (window.location.protocol !== "https:" || !isTrustedHost(window.location.hostname)) {
        return;
    }
    if (document.body) {
        start();
    } else {
        document.addEventListener("DOMContentLoaded", start, { once: true });
    }
}());
