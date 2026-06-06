(function () {
    const liveContainer = document.querySelector("[data-auto-refresh-url]");
    if (!liveContainer) {
        return;
    }

    const refreshUrl = liveContainer.getAttribute("data-auto-refresh-url");
    const seconds = Number.parseInt(liveContainer.getAttribute("data-auto-refresh-seconds") || "20", 10);
    if (!refreshUrl || Number.isNaN(seconds) || seconds < 10) {
        return;
    }

    const isInteractionBusy = () => {
        if (document.body.classList.contains("modal-open") || document.querySelector("[data-checkout-modal]:not([hidden])")) {
            return true;
        }

        const activeElement = document.activeElement;
        if (activeElement && ["INPUT", "TEXTAREA", "SELECT"].includes(activeElement.tagName)) {
            return true;
        }

        return false;
    };

    const copyAttributes = (target, source) => {
        for (const attr of target.getAttributeNames()) {
            target.removeAttribute(attr);
        }

        for (const attr of source.getAttributeNames()) {
            target.setAttribute(attr, source.getAttribute(attr) || "");
        }
    };

    const replaceElement = (container, nextContainer, selector) => {
        const current = container.querySelector(selector);
        const next = nextContainer.querySelector(selector);
        if (current instanceof HTMLElement && next instanceof HTMLElement) {
            current.replaceWith(next);
        }
    };

    const syncFeaturePanels = () => {
        if (typeof window.__applyFeatureTarget === "function") {
            window.__applyFeatureTarget(window.location.hash.replace(/^#/, "").trim());
        }
    };

    const getRestaurantSignature = (container) => {
        const cards = Array.from(container.querySelectorAll("[data-table-card]"))
            .map((card) => {
                if (!(card instanceof HTMLElement)) {
                    return "";
                }

                return [
                    card.dataset.tableCode || "",
                    card.dataset.tableState || "",
                    card.dataset.tableTotalRaw || "",
                    card.dataset.hasActiveOrder || ""
                ].join(":");
            })
            .join("|");

        const details = Array.from(container.querySelectorAll("[data-table-details]"))
            .map((detail) => {
                if (!(detail instanceof HTMLElement)) {
                    return "";
                }

                return `${detail.getAttribute("data-table-details") || ""}:${detail.textContent?.replace(/\s+/g, " ").trim() || ""}`;
            })
            .join("|");

        return `${cards}::${details}`;
    };

    const getCustomerSignature = (container) => {
        const side = container.querySelector(".customer-side");
        const fab = container.querySelector(".customer-cart-fab");
        return [
            fab instanceof HTMLElement ? fab.textContent?.replace(/\s+/g, " ").trim() || "" : "",
            side instanceof HTMLElement ? side.textContent?.replace(/\s+/g, " ").trim() || "" : ""
        ].join("::");
    };

    const refreshLivePage = async () => {
        if (isInteractionBusy()) {
            return;
        }

        const response = await fetch(refreshUrl, {
            credentials: "same-origin",
            headers: { "X-Requested-With": "XMLHttpRequest" }
        });

        if (!response.ok) {
            return;
        }

        const html = await response.text();
        const doc = new DOMParser().parseFromString(html, "text/html");
        const nextContainer = doc.querySelector("[data-live-page]");
        if (!(nextContainer instanceof HTMLElement)) {
            return;
        }

        copyAttributes(liveContainer, nextContainer);

        const pageName = liveContainer.getAttribute("data-live-page") || "";
        if (pageName === "customer") {
            if (getCustomerSignature(liveContainer) !== getCustomerSignature(nextContainer)) {
                [
                    ".customer-cart-fab",
                    ".customer-side"
                ].forEach((selector) => replaceElement(liveContainer, nextContainer, selector));
            }

            return;
        }

        if (pageName === "restaurant") {
            if (getRestaurantSignature(liveContainer) !== getRestaurantSignature(nextContainer)) {
                window.location.reload();
            }

            return;
        }

        if (pageName === "staff") {
            [
                ".staff-metrics",
                "[data-feature-panel='orders']",
                "[data-feature-panel='kitchen']",
                "[data-feature-panel='payments']",
                "[data-feature-panel='tables']"
            ].forEach((selector) => replaceElement(liveContainer, nextContainer, selector));

            syncFeaturePanels();
        }
    };

    if (liveContainer.hasAttribute("data-live-page")) {
        const intervalMs = Number.parseInt(liveContainer.getAttribute("data-live-refresh-ms") || `${seconds * 1000}`, 10);
        if (!Number.isNaN(intervalMs) && intervalMs >= 1000) {
            window.setInterval(() => refreshLivePage().catch(() => {}), intervalMs);
        }

        return;
    }

    // Non-live pages should not force a full reload. Live pages handle their
    // own background refresh above.
})();

(function () {
    const workspaces = Array.from(document.querySelectorAll("[data-feature-workspace]"));
    if (workspaces.length === 0) {
        return;
    }

    const escapeSelectorValue = (value) => window.CSS && typeof window.CSS.escape === "function"
        ? window.CSS.escape(value)
        : value.replace(/["\\]/g, "\\$&");

    const getCurrentTarget = (workspace) => {
        const hashTarget = window.location.hash.replace(/^#/, "").trim();
        if (hashTarget && workspace.querySelector(`[data-feature-panel="${escapeSelectorValue(hashTarget)}"]`)) {
            return hashTarget;
        }

        return workspace.getAttribute("data-feature-default") || "";
    };

    const applyTarget = (target) => {
        for (const workspace of workspaces) {
            if (!(workspace instanceof HTMLElement)) {
                continue;
            }

            const resolvedTarget = target && workspace.querySelector(`[data-feature-panel="${escapeSelectorValue(target)}"]`)
                ? target
                : getCurrentTarget(workspace);

            workspace.querySelectorAll("[data-feature-panel]").forEach((panel) => {
                if (panel instanceof HTMLElement) {
                    panel.hidden = panel.getAttribute("data-feature-panel") !== resolvedTarget;
                }
            });

            const nav = workspace.previousElementSibling;
            if (nav instanceof HTMLElement && nav.hasAttribute("data-feature-nav")) {
                nav.querySelectorAll("[data-feature-target]").forEach((button) => {
                    if (!(button instanceof HTMLButtonElement)) {
                        return;
                    }

                    const isActive = button.getAttribute("data-feature-target") === resolvedTarget;
                    button.classList.toggle("is-active", isActive);
                    button.setAttribute("aria-pressed", isActive ? "true" : "false");
                });
            }
        }
    };

    document.addEventListener("click", (event) => {
        const target = event.target;
        if (!(target instanceof Element)) {
            return;
        }

        const button = target.closest("[data-feature-target]");
        if (!(button instanceof HTMLButtonElement)) {
            return;
        }

        const featureTarget = button.getAttribute("data-feature-target") || "";
        if (!featureTarget) {
            return;
        }

        event.preventDefault();
        window.location.hash = featureTarget;
        applyTarget(featureTarget);
    });

    window.addEventListener("hashchange", () => {
        applyTarget(window.location.hash.replace(/^#/, "").trim());
    });

    window.__applyFeatureTarget = applyTarget;
    applyTarget(window.location.hash.replace(/^#/, "").trim());
})();

(function () {
    const customerContainer = document.querySelector("[data-auto-refresh-url]");
    if (!customerContainer) {
        return;
    }

    document.addEventListener("submit", async (event) => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) ||
            (!form.classList.contains("dish-order-form") && !form.classList.contains("cart-remove-form"))) {
            return;
        }

        event.preventDefault();

        const submitButton = form.querySelector("button[type='submit']");
            if (submitButton instanceof HTMLButtonElement) {
                submitButton.disabled = true;
                submitButton.dataset.originalText = submitButton.textContent || "";
                submitButton.textContent = form.classList.contains("cart-remove-form") ? "..." : "Đang thêm...";
            }

        try {
            const response = await fetch(form.action, {
                method: "POST",
                body: new FormData(form),
                credentials: "same-origin",
                headers: {
                    "X-Requested-With": "XMLHttpRequest"
                }
            });

            const html = await response.text();
            const parser = new DOMParser();
            const doc = parser.parseFromString(html, "text/html");
            const nextContainer = doc.querySelector("[data-auto-refresh-url]");

            if (!nextContainer) {
                window.location.reload();
                return;
            }

            for (const attr of customerContainer.getAttributeNames()) {
                customerContainer.removeAttribute(attr);
            }

            for (const attr of nextContainer.getAttributeNames()) {
                customerContainer.setAttribute(attr, nextContainer.getAttribute(attr) || "");
            }

            customerContainer.innerHTML = nextContainer.innerHTML;
        } catch (_error) {
            window.location.reload();
        } finally {
            if (submitButton instanceof HTMLButtonElement) {
                submitButton.disabled = false;
                submitButton.textContent = submitButton.dataset.originalText ||
                    (form.classList.contains("cart-remove-form") ? "x" : "Thêm vào giỏ chung");
            }
        }
    });
})();

(function () {
    const chatPollMs = 2000;

    const createMessageBubble = (message, viewerRole) => {
        const senderRole = (message.senderRole || "").toLowerCase();
        const viewer = (viewerRole || "").toLowerCase();
        const side = senderRole === viewer ? "mine" : "theirs";
        const bubble = document.createElement("div");
        bubble.className = `chat-bubble chat-bubble--${message.direction || "system"} chat-message chat-message--${side}`;
        if (message.messageId) {
            bubble.dataset.messageId = message.messageId;
        }

        if (side === "theirs") {
            const name = document.createElement("strong");
            name.className = "chat-message__name";
            name.textContent = message.senderName || "Tin nhan";
            bubble.appendChild(name);
        }

        const body = document.createElement("p");
        body.textContent = message.message || "";
        bubble.appendChild(body);

        const time = document.createElement("small");
        time.className = "chat-message__time";
        time.textContent = message.timeLabel || "";
        bubble.appendChild(time);

        return bubble;
    };

    const renderEmpty = (container, title, subtitle) => {
        container.innerHTML = "";
        const empty = document.createElement("div");
        empty.className = "chat-empty staff-empty-state";
        const strong = document.createElement("strong");
        strong.textContent = title;
        const span = document.createElement("span");
        span.textContent = subtitle;
        empty.append(strong, span);
        container.appendChild(empty);
    };

    const scrollChatToBottom = (container) => {
        container.scrollTop = container.scrollHeight;
    };

    const renderMessages = (thread, messages, viewerRole) => {
        if (!(thread instanceof HTMLElement)) {
            return;
        }

        const previousLast = thread.querySelector(".chat-message:last-child")?.getAttribute("data-message-id") || "";
        const shouldStickToBottom = thread.scrollHeight - thread.scrollTop - thread.clientHeight < 80;
        thread.innerHTML = "";

        if (!Array.isArray(messages) || messages.length === 0) {
            renderEmpty(thread, "Chua co tin nhan.", "Tin moi se hien o day ngay khi co nguoi gui.");
            return;
        }

        for (const message of messages) {
            thread.appendChild(createMessageBubble(message, viewerRole));
        }

        const nextLast = messages[messages.length - 1]?.messageId || "";
        if (shouldStickToBottom || previousLast !== nextLast) {
            scrollChatToBottom(thread);
        }
    };

    const fetchJson = async (url, options) => {
        const response = await fetch(url, {
            credentials: "same-origin",
            headers: { "X-Requested-With": "XMLHttpRequest" },
            ...options
        });

        if (!response.ok) {
            throw new Error(`Request failed: ${response.status}`);
        }

        return response.json();
    };

    const refreshCustomerChat = async () => {
        const panel = document.querySelector("[data-chat-panel='customer']");
        if (!(panel instanceof HTMLElement)) {
            return;
        }

        const url = panel.dataset.chatUrl || "";
        const thread = panel.querySelector("[data-chat-thread]");
        const count = panel.querySelector("[data-chat-count]");
        if (!url || !(thread instanceof HTMLElement)) {
            return;
        }

        const data = await fetchJson(url);
        renderMessages(thread, data.messages || [], "customer");
        if (count instanceof HTMLElement) {
            count.textContent = `${data.count || 0} tin`;
        }
    };

    const getAntiForgeryToken = () => {
        const token = document.querySelector("input[name='__RequestVerificationToken']");
        return token instanceof HTMLInputElement ? token.value : "";
    };

    const createHiddenInput = (name, value) => {
        const input = document.createElement("input");
        input.type = "hidden";
        input.name = name;
        input.value = value;
        return input;
    };

    const renderStaffThreads = (threads, pendingCount) => {
        const list = document.querySelector("[data-staff-chat-list]");
        const pending = document.querySelector("[data-staff-chat-pending]");
        if (!(list instanceof HTMLElement)) {
            return;
        }

        if (pending instanceof HTMLElement) {
            pending.textContent = `${pendingCount || 0} tin cho`;
        }

        const openTables = new Set(
            Array.from(list.querySelectorAll("[data-staff-chat-thread][open]"))
                .map((item) => item instanceof HTMLElement ? item.dataset.tableCode || "" : "")
                .filter(Boolean)
        );
        const token = getAntiForgeryToken();
        list.innerHTML = "";

        if (!Array.isArray(threads) || threads.length === 0) {
            const empty = document.createElement("div");
            empty.className = "staff-empty-state";
            const strong = document.createElement("strong");
            strong.textContent = "Chua co tin nhan";
            empty.appendChild(strong);
            list.appendChild(empty);
            return;
        }

        for (const thread of threads) {
            const tableCode = thread.tableCode || "";
            const details = document.createElement("details");
            details.className = "staff-chat-card staff-chat-card--collapsible";
            details.dataset.staffChatThread = "";
            details.dataset.tableCode = tableCode;
            details.open = (thread.pendingCount || 0) > 0 || openTables.has(tableCode);

            const summary = document.createElement("summary");
            summary.className = "staff-chat-summary";

            const title = document.createElement("strong");
            title.textContent = `Ban ${tableCode}`;

            const badge = document.createElement("span");
            badge.className = (thread.pendingCount || 0) > 0
                ? "status-badge staff-unread-badge"
                : "status-badge muted";
            badge.textContent = (thread.pendingCount || 0) > 0 ? "Co tin nhan chua doc" : "Da doc";

            const last = document.createElement("small");
            last.dataset.staffChatLast = "";
            last.textContent = thread.lastMessageLabel || "";

            const deleteForm = document.createElement("form");
            deleteForm.className = "staff-chat-delete-form";
            deleteForm.method = "post";
            deleteForm.action = "/Home/DeleteTableChat";
            deleteForm.addEventListener("click", (event) => event.stopPropagation());
            if (token) {
                deleteForm.appendChild(createHiddenInput("__RequestVerificationToken", token));
            }
            deleteForm.appendChild(createHiddenInput("tableCode", tableCode));
            const deleteButton = document.createElement("button");
            deleteButton.className = "btn btn-ghost btn-small";
            deleteButton.type = "submit";
            deleteButton.textContent = "Xoa";
            deleteButton.addEventListener("click", (event) => {
                if (!window.confirm(`Xoa hoi thoai ban ${tableCode}?`)) {
                    event.preventDefault();
                }
            });
            deleteForm.appendChild(deleteButton);

            summary.append(title, badge, last, deleteForm);

            const body = document.createElement("div");
            body.className = "staff-chat-card__body";
            const messages = document.createElement("div");
            messages.className = "chat-thread compact-stack";
            messages.dataset.chatThread = "";
            renderMessages(messages, thread.messages || [], "staff");

            const replyForm = document.createElement("form");
            replyForm.className = "staff-reply-form";
            replyForm.method = "post";
            replyForm.action = "/Home/SendStaffMessage";
            replyForm.dataset.chatForm = "";
            if (token) {
                replyForm.appendChild(createHiddenInput("__RequestVerificationToken", token));
            }
            replyForm.appendChild(createHiddenInput("TableCode", tableCode));
            const input = document.createElement("input");
            input.name = "Message";
            input.placeholder = "Nhap phan hoi nhanh...";
            const send = document.createElement("button");
            send.className = "btn btn-accent btn-small";
            send.type = "submit";
            send.textContent = "Gui";
            replyForm.append(input, send);

            body.append(messages, replyForm);
            details.append(summary, body);
            list.appendChild(details);
        }
    };

    const refreshStaffChat = async () => {
        const page = document.querySelector("[data-staff-chat-url]");
        if (!(page instanceof HTMLElement) || !page.dataset.staffChatUrl) {
            return;
        }

        const activeElement = document.activeElement;
        if (activeElement && ["INPUT", "TEXTAREA", "SELECT"].includes(activeElement.tagName)) {
            return;
        }

        const data = await fetchJson(page.dataset.staffChatUrl);
        renderStaffThreads(data.threads || [], data.pendingCount || 0);
    };

    document.addEventListener("submit", async (event) => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.hasAttribute("data-chat-form")) {
            return;
        }

        event.preventDefault();
        const input = form.querySelector("input[name='Message']");
        const button = form.querySelector("button[type='submit']");
        if (!(input instanceof HTMLInputElement) || input.value.trim().length === 0) {
            return;
        }

        const originalText = button instanceof HTMLButtonElement ? button.textContent || "" : "";
        if (button instanceof HTMLButtonElement) {
            button.disabled = true;
            button.textContent = "...";
        }

        try {
            const data = await fetchJson(form.action, {
                method: "POST",
                body: new FormData(form)
            });

            if (data.succeeded === false) {
                window.alert(data.errorMessage || "Không gửi được tin nhắn.");
                return;
            }

            input.value = "";
            if (form.classList.contains("customer-chat-form")) {
                const panel = form.closest("[data-chat-panel='customer']");
                const thread = panel?.querySelector("[data-chat-thread]");
                const count = panel?.querySelector("[data-chat-count]");
                if (thread instanceof HTMLElement) {
                    renderMessages(thread, data.messages || [], "customer");
                }
                if (count instanceof HTMLElement) {
                    count.textContent = `${(data.messages || []).length} tin`;
                }
            } else {
                renderStaffThreads(data.threads || [], data.pendingCount || 0);
            }
        } catch (_error) {
            window.location.reload();
        } finally {
            if (button instanceof HTMLButtonElement) {
                button.disabled = false;
                button.textContent = originalText || "Gui";
            }
        }
    });

    if (document.querySelector("[data-chat-panel='customer']")) {
        refreshCustomerChat().catch(() => {});
        window.setInterval(() => refreshCustomerChat().catch(() => {}), chatPollMs);
    }

    if (document.querySelector("[data-staff-chat-url]")) {
        refreshStaffChat().catch(() => {});
        window.setInterval(() => refreshStaffChat().catch(() => {}), chatPollMs);
    }
})();

(function () {
    const customerContainer = document.querySelector(".customer-page");
    if (!(customerContainer instanceof HTMLElement)) {
        return;
    }

    document.addEventListener("click", (event) => {
        const target = event.target;
        if (!(target instanceof Element)) {
            return;
        }

        const button = target.closest("[data-menu-toolbar-toggle]");
        if (!(button instanceof HTMLButtonElement)) {
            return;
        }

        const toolbar = button.closest("[data-menu-sticky-toolbar]");
        if (!(toolbar instanceof HTMLElement)) {
            return;
        }

        const isCollapsed = toolbar.classList.toggle("is-collapsed");
        button.textContent = isCollapsed ? "Mở lọc" : "Thu nhỏ";
        button.setAttribute("aria-expanded", isCollapsed ? "false" : "true");
        window.requestAnimationFrame(updateStickyMenu);
    });

    const resetToolbar = (toolbar, spacer, menuGrid) => {
        toolbar.classList.remove("is-fixed");
        toolbar.style.left = "";
        toolbar.style.top = "";
        toolbar.style.width = "";
        spacer.classList.remove("is-active");
        spacer.style.height = "";
        if (menuGrid instanceof HTMLElement) {
            menuGrid.style.paddingTop = "";
        }
    };

    const updateStickyMenu = () => {
        const toolbar = document.querySelector("[data-menu-sticky-toolbar]");
        const spacer = document.querySelector("[data-menu-sticky-spacer]");
        const menuCard = document.querySelector("#menu-list");
        const menuGrid = menuCard instanceof HTMLElement ? menuCard.querySelector(".menu-grid") : null;
        if (!(toolbar instanceof HTMLElement) ||
            !(spacer instanceof HTMLElement) ||
            !(menuCard instanceof HTMLElement)) {
            return;
        }

        resetToolbar(toolbar, spacer, menuGrid);

        const header = document.querySelector(".app-header");
        const headerBottom = header instanceof HTMLElement
            ? Math.max(0, Math.round(header.getBoundingClientRect().bottom))
            : 0;
        const topGap = Math.max(0, headerBottom - 2);
        const cardRect = menuCard.getBoundingClientRect();
        const toolbarRect = toolbar.getBoundingClientRect();
        const shouldFix = cardRect.top < topGap && cardRect.bottom > topGap + toolbarRect.height + 40;

        if (!shouldFix) {
            return;
        }

        spacer.classList.add("is-active");
        spacer.style.height = `${toolbarRect.height}px`;
        if (menuGrid instanceof HTMLElement) {
            menuGrid.style.paddingTop = toolbar.classList.contains("is-collapsed")
                ? (window.innerWidth <= 640 ? "72px" : "58px")
                : (window.innerWidth <= 640 ? "42px" : "34px");
        }
        toolbar.classList.add("is-fixed");
        toolbar.style.left = `${cardRect.left}px`;
        toolbar.style.top = `${topGap}px`;
        toolbar.style.width = `${cardRect.width}px`;
    };

    let ticking = false;
    const requestUpdate = () => {
        if (ticking) {
            return;
        }

        ticking = true;
        window.requestAnimationFrame(() => {
            ticking = false;
            updateStickyMenu();
        });
    };

    window.addEventListener("scroll", requestUpdate, { passive: true });
    window.addEventListener("resize", requestUpdate);
    new MutationObserver(requestUpdate).observe(customerContainer, { childList: true, subtree: true });
    requestUpdate();
})();

(function () {
    const input = document.querySelector("[data-menu-image-input]");
    const preview = document.querySelector("[data-menu-image-preview]");
    if (!(input instanceof HTMLInputElement) || !(preview instanceof HTMLElement)) {
        return;
    }

    input.addEventListener("change", () => {
        const file = input.files && input.files[0];
        if (!file) {
            return;
        }

        const url = URL.createObjectURL(file);
        preview.innerHTML = "";
        const image = document.createElement("img");
        image.src = url;
        image.alt = "Ảnh món vừa chọn";
        image.onload = () => URL.revokeObjectURL(url);
        preview.appendChild(image);
    });
})();

(function () {
    const customerContainer = document.querySelector("[data-auto-refresh-url]");
    if (!customerContainer) {
        return;
    }

    const closeCart = () => {
        document.body.classList.remove("customer-cart-open", "modal-open");
    };

    const openCart = () => {
        document.body.classList.add("customer-cart-open", "modal-open");
    };

    document.addEventListener("click", (event) => {
        const target = event.target;
        if (!(target instanceof Element)) {
            return;
        }

        if (target.closest("[data-cart-open]")) {
            event.preventDefault();
            openCart();
            return;
        }

        if (target.closest("[data-cart-close]")) {
            event.preventDefault();
            closeCart();
        }
    });

    document.addEventListener("keydown", (event) => {
        if (event.key === "Escape" && document.body.classList.contains("customer-cart-open")) {
            closeCart();
        }
    });

    window.addEventListener("resize", () => {
        if (window.innerWidth > 640) {
            closeCart();
        }
    });
})();

(function () {
    const board = document.querySelector("[data-restaurant-board]");
    const actionBar = document.querySelector("[data-restaurant-action-bar]");
    const checkoutModal = document.querySelector("[data-checkout-modal]");
    if (!board || !actionBar || !checkoutModal) {
        return;
    }

    const cards = Array.from(board.querySelectorAll("[data-table-card]"));
    const detailSources = new Map(
        Array.from(board.querySelectorAll("[data-table-details]")).map((item) => [item.getAttribute("data-table-details") || "", item])
    );
    const selectedTableNames = Array.from(board.querySelectorAll("[data-selected-table-name]"));
    const selectedTableStates = Array.from(board.querySelectorAll("[data-selected-table-state]"));
    const selectedTableAmounts = Array.from(board.querySelectorAll("[data-selected-table-amount]"));
    const selectedTableItems = board.querySelector("[data-selected-table-items]");
    const addDishLink = actionBar.querySelector("[data-add-dish-link]");
    const paymentButton = actionBar.querySelector("[data-payment-button]");
    const transferButton = actionBar.querySelector("[data-transfer-button]");
    const splitButton = actionBar.querySelector("[data-split-button]");
    const checkoutTableName = checkoutModal.querySelector("[data-checkout-table-name]");
    const checkoutTableState = checkoutModal.querySelector("[data-checkout-table-state]");
    const checkoutTotal = checkoutModal.querySelector("[data-checkout-total]");
    const checkoutTotalReadonly = checkoutModal.querySelector("[data-checkout-total-readonly]");
    const checkoutItems = checkoutModal.querySelector("[data-checkout-items]");
    const paymentTableCode = checkoutModal.querySelector("[data-payment-table-code]");
    const paymentRows = checkoutModal.querySelector("[data-payment-rows]");
    const addPaymentRowButton = checkoutModal.querySelector("[data-add-payment-row]");
    const paidTotalDisplay = checkoutModal.querySelector("[data-paid-total-display]");
    const balanceLabel = checkoutModal.querySelector("[data-balance-label]");
    const changeDisplay = checkoutModal.querySelector("[data-change-display]");
    const checkoutSubmit = checkoutModal.querySelector("[data-checkout-submit]");
    const closeButtons = Array.from(checkoutModal.querySelectorAll("[data-checkout-close]"));
    const transferModal = document.querySelector("[data-transfer-modal]");
    const splitModal = document.querySelector("[data-split-modal]");
    const transferCloseButtons = transferModal instanceof HTMLElement ? Array.from(transferModal.querySelectorAll("[data-transfer-close]")) : [];
    const splitCloseButtons = splitModal instanceof HTMLElement ? Array.from(splitModal.querySelectorAll("[data-split-close]")) : [];
    const transferFromTable = transferModal instanceof HTMLElement ? transferModal.querySelector("[data-transfer-from-table]") : null;
    const transferToTable = transferModal instanceof HTMLElement ? transferModal.querySelector("[data-transfer-to-table]") : null;
    const transferTableName = transferModal instanceof HTMLElement ? transferModal.querySelector("[data-transfer-table-name]") : null;
    const splitFromTable = splitModal instanceof HTMLElement ? splitModal.querySelector("[data-split-from-table]") : null;
    const splitToTable = splitModal instanceof HTMLElement ? splitModal.querySelector("[data-split-to-table]") : null;
    const splitTableName = splitModal instanceof HTMLElement ? splitModal.querySelector("[data-split-table-name]") : null;
    const splitItems = splitModal instanceof HTMLElement ? splitModal.querySelector("[data-split-items]") : null;
    const splitQuantity = splitModal instanceof HTMLElement ? splitModal.querySelector("[data-split-quantity]") : null;
    if (cards.length === 0 ||
        selectedTableNames.length === 0 ||
        selectedTableStates.length === 0 ||
        selectedTableAmounts.length === 0 ||
        !selectedTableNames.every((item) => item instanceof HTMLElement) ||
        !selectedTableStates.every((item) => item instanceof HTMLElement) ||
        !selectedTableAmounts.every((item) => item instanceof HTMLElement) ||
        !(selectedTableItems instanceof HTMLElement) ||
        !(addDishLink instanceof HTMLAnchorElement) ||
        !(paymentTableCode instanceof HTMLInputElement) ||
        !(paymentButton instanceof HTMLButtonElement) ||
        !(transferButton instanceof HTMLButtonElement) ||
        !(splitButton instanceof HTMLButtonElement) ||
        !(checkoutTableName instanceof HTMLElement) ||
        !(checkoutTableState instanceof HTMLElement) ||
        !(checkoutTotal instanceof HTMLElement) ||
        !(checkoutTotalReadonly instanceof HTMLInputElement) ||
        !(checkoutItems instanceof HTMLElement) ||
        !(paymentRows instanceof HTMLElement) ||
        !(addPaymentRowButton instanceof HTMLButtonElement) ||
        !(paidTotalDisplay instanceof HTMLInputElement) ||
        !(balanceLabel instanceof HTMLElement) ||
        !(changeDisplay instanceof HTMLInputElement) ||
        !(checkoutSubmit instanceof HTMLButtonElement) ||
        !(transferModal instanceof HTMLElement) ||
        !(splitModal instanceof HTMLElement) ||
        !(transferFromTable instanceof HTMLInputElement) ||
        !(transferToTable instanceof HTMLSelectElement) ||
        !(transferTableName instanceof HTMLElement) ||
        !(splitFromTable instanceof HTMLInputElement) ||
        !(splitToTable instanceof HTMLSelectElement) ||
        !(splitTableName instanceof HTMLElement) ||
        !(splitItems instanceof HTMLElement) ||
        !(splitQuantity instanceof HTMLInputElement)) {
        return;
    }

    const baseAddDishUrl = addDishLink.getAttribute("href") || "";
    let selectedCard = cards[0];
    transferButton.textContent = "Chuyển bàn";
    splitButton.textContent = "Tách món";

    const formatCurrency = (value) => `${new Intl.NumberFormat("vi-VN").format(Math.max(0, value))}d`;
    const parseNumber = (value) => {
        const parsed = Number.parseFloat(value);
        return Number.isFinite(parsed) ? parsed : 0;
    };

    const createPaymentRow = (index) => {
        const row = document.createElement("div");
        row.className = "payment-row";
        row.setAttribute("data-payment-row", "");
        row.innerHTML = `
            <select name="PaymentMethods[${index}]" data-payment-method>
                <option value="cash">Tiền mặt</option>
                <option value="bankqr">Chuyển khoản QR</option>
                <option value="ewallet">Ví điện tử</option>
            </select>
            <input name="PaymentAmounts[${index}]" type="number" min="0" step="any" data-payment-amount />
            <button class="btn btn-ghost btn-small" type="button" data-accept-payment-row>Chấp nhận</button>
            <button class="btn btn-ghost btn-small" type="button" data-remove-payment-row>Xóa</button>
        `;
        return row;
    };

    const getPaymentRows = () => Array.from(paymentRows.querySelectorAll("[data-payment-row]"));

    const syncPaymentRowIndexes = () => {
        const rows = getPaymentRows();
        rows.forEach((row, index) => {
            const methodSelect = row.querySelector("[data-payment-method]");
            const amountInput = row.querySelector("[data-payment-amount]");
            const acceptButton = row.querySelector("[data-accept-payment-row]");
            const removeButton = row.querySelector("[data-remove-payment-row]");

            if (methodSelect instanceof HTMLSelectElement) {
                methodSelect.name = `PaymentMethods[${index}]`;
            }

            if (amountInput instanceof HTMLInputElement) {
                amountInput.name = `PaymentAmounts[${index}]`;
            }

            if (acceptButton instanceof HTMLButtonElement) {
                const isAccepted = row.dataset.paymentAccepted === "true";
                acceptButton.textContent = isAccepted ? "Đã chấp nhận" : "Chấp nhận";
                acceptButton.classList.toggle("is-accepted", isAccepted);
            }

            if (removeButton instanceof HTMLButtonElement) {
                removeButton.disabled = rows.length === 1;
            }
        });
    };

    const syncChangeDisplay = () => {
        const totalRaw = parseNumber(selectedCard?.dataset.tableTotalRaw || "0");
        const paidRaw = getPaymentRows().reduce((sum, row) => {
            if (row.dataset.paymentAccepted !== "true") {
                return sum;
            }

            const amountInput = row.querySelector("[data-payment-amount]");
            return sum + (amountInput instanceof HTMLInputElement ? parseNumber(amountInput.value) : 0);
        }, 0);
        const delta = paidRaw - totalRaw;

        paidTotalDisplay.value = formatCurrency(paidRaw);
        if (delta >= 0) {
            balanceLabel.textContent = "Tiền thừa";
            changeDisplay.value = formatCurrency(delta);
        } else {
            balanceLabel.textContent = "Còn thiếu";
            changeDisplay.value = formatCurrency(Math.abs(delta));
        }

        checkoutSubmit.disabled = selectedCard?.dataset.hasActiveOrder !== "true" || paidRaw < totalRaw;
    };

    const syncPaymentInputs = () => {
        syncChangeDisplay();
    };

    const renderCheckoutDetails = (card) => {
        const tableCode = card.dataset.tableCode || "";
        const totalText = card.dataset.tableAmount || "0đ";
        const totalRaw = parseNumber(card.dataset.tableTotalRaw || "0");
        const source = detailSources.get(tableCode);

        checkoutTableName.textContent = card.dataset.tableName || tableCode;
        checkoutTableState.textContent = card.dataset.tableState || "";
        checkoutTotal.textContent = totalText;
        checkoutTotalReadonly.value = totalText;
        paymentTableCode.value = tableCode;
        paymentRows.innerHTML = "";
        paymentRows.appendChild(createPaymentRow(0));
        const firstPaymentRow = paymentRows.querySelector("[data-payment-row]");
        if (firstPaymentRow instanceof HTMLElement) {
            firstPaymentRow.dataset.paymentAccepted = "false";
        }
        const firstAmountInput = paymentRows.querySelector("[data-payment-amount]");
        if (firstAmountInput instanceof HTMLInputElement) {
            firstAmountInput.placeholder = totalText;
        }
        paidTotalDisplay.value = "0đ";
        checkoutItems.innerHTML = source?.innerHTML || "<div class=\"checkout-item checkout-item--empty\"></div>";
        syncPaymentRowIndexes();
        syncPaymentInputs();
    };

    const escapeSelectorValue = (value) => window.CSS && typeof window.CSS.escape === "function"
        ? window.CSS.escape(value)
        : value.replace(/["\\]/g, "\\$&");

    const chooseDifferentTable = (select, tableCode) => {
        const options = Array.from(select.options);
        const preferred = options.find((option) => option.value !== tableCode);
        select.value = preferred?.value || tableCode;
    };

    const renderTransferDetails = (card) => {
        const tableCode = card.dataset.tableCode || "";
        transferFromTable.value = tableCode;
        transferTableName.textContent = card.dataset.tableName || `Ban ${tableCode}`;
        chooseDifferentTable(transferToTable, tableCode);
    };

    const renderSplitDetails = (card) => {
        const tableCode = card.dataset.tableCode || "";
        splitFromTable.value = tableCode;
        splitTableName.textContent = card.dataset.tableName || `Ban ${tableCode}`;
        chooseDifferentTable(splitToTable, tableCode);
        splitQuantity.value = "1";

        const source = document.querySelector(`[data-table-transfer-items="${escapeSelectorValue(tableCode)}"]`);
        splitItems.innerHTML = source?.innerHTML || "<div class=\"staff-empty-state\"><strong>Không có món để tách.</strong></div>";
    };

    const openCheckoutModal = () => {
        if (selectedCard?.dataset.hasActiveOrder !== "true") {
            return;
        }

        renderCheckoutDetails(selectedCard);
        checkoutModal.hidden = false;
        document.body.classList.add("modal-open");
    };

    const openTransferModal = () => {
        if (selectedCard?.dataset.hasActiveOrder !== "true") {
            return;
        }

        renderTransferDetails(selectedCard);
        transferModal.hidden = false;
        document.body.classList.add("modal-open");
    };

    const openSplitModal = () => {
        if (selectedCard?.dataset.hasActiveOrder !== "true") {
            return;
        }

        renderSplitDetails(selectedCard);
        splitModal.hidden = false;
        document.body.classList.add("modal-open");
    };

    const closeCheckoutModal = () => {
        checkoutModal.hidden = true;
        document.body.classList.remove("modal-open");
    };

    const closeTransferModal = () => {
        transferModal.hidden = true;
        document.body.classList.remove("modal-open");
    };

    const closeSplitModal = () => {
        splitModal.hidden = true;
        document.body.classList.remove("modal-open");
    };

    const applySelection = (card) => {
        cards.forEach((item) => item.classList.toggle("is-selected", item === card));
        selectedCard = card;

        const tableCode = card.dataset.tableCode || "";
        const tableName = card.dataset.tableName || tableCode;
        const tableState = card.dataset.tableState || "";
        const tableAmount = card.dataset.tableAmount || "0đ";
        const hasActiveOrder = card.dataset.hasActiveOrder === "true";
        const source = detailSources.get(tableCode);

        selectedTableNames.forEach((item) => { item.textContent = tableName; });
        selectedTableStates.forEach((item) => { item.textContent = tableState; });
        selectedTableAmounts.forEach((item) => { item.textContent = tableAmount; });
        selectedTableItems.innerHTML = source?.innerHTML || "<div class=\"checkout-item checkout-item--empty\"></div>";
        paymentTableCode.value = tableCode;
        addDishLink.href = `${baseAddDishUrl.split("?")[0]}?tableCode=${encodeURIComponent(tableCode)}`;
        paymentButton.disabled = !hasActiveOrder;
        transferButton.disabled = !hasActiveOrder;
        splitButton.disabled = !hasActiveOrder;
        paymentButton.textContent = hasActiveOrder ? "Thanh toán" : "Bàn này chưa có order";

        if (!checkoutModal.hidden) {
            renderCheckoutDetails(card);
        }

        if (!transferModal.hidden) {
            renderTransferDetails(card);
        }

        if (!splitModal.hidden) {
            renderSplitDetails(card);
        }
    };

    for (const card of cards) {
        card.addEventListener("click", () => applySelection(card));
        card.addEventListener("keydown", (event) => {
            if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                applySelection(card);
            }
        });
    }

    paymentButton.addEventListener("click", openCheckoutModal);
    transferButton.addEventListener("click", openTransferModal);
    splitButton.addEventListener("click", openSplitModal);
    addPaymentRowButton.addEventListener("click", () => {
        const nextIndex = getPaymentRows().length;
        const row = createPaymentRow(nextIndex);
        row.dataset.paymentAccepted = "false";
        paymentRows.appendChild(row);
        const amountInput = row.querySelector("[data-payment-amount]");
        if (amountInput instanceof HTMLInputElement) {
            amountInput.placeholder = selectedCard?.dataset.tableAmount || "0đ";
        }

        syncPaymentRowIndexes();
        syncPaymentInputs();
    });
    paymentRows.addEventListener("input", (event) => {
        const target = event.target;
        if (target instanceof HTMLInputElement || target instanceof HTMLSelectElement) {
            const row = target.closest("[data-payment-row]");
            if (row instanceof HTMLElement) {
                row.dataset.paymentAccepted = "false";
            }
            syncPaymentRowIndexes();
            syncChangeDisplay();
        }
    });
    paymentRows.addEventListener("change", (event) => {
        const target = event.target;
        if (target instanceof HTMLInputElement || target instanceof HTMLSelectElement) {
            const row = target.closest("[data-payment-row]");
            if (row instanceof HTMLElement) {
                row.dataset.paymentAccepted = "false";
            }
            syncPaymentRowIndexes();
            syncChangeDisplay();
        }
    });
    paymentRows.addEventListener("click", (event) => {
        const target = event.target;
        if (!(target instanceof Element)) {
            return;
        }

        if (target.hasAttribute("data-accept-payment-row")) {
            const row = target.closest("[data-payment-row]");
            const amountInput = row instanceof HTMLElement ? row.querySelector("[data-payment-amount]") : null;
            if (!(row instanceof HTMLElement) || !(amountInput instanceof HTMLInputElement) || parseNumber(amountInput.value) <= 0) {
                return;
            }

            row.dataset.paymentAccepted = "true";
            syncPaymentRowIndexes();
            syncPaymentInputs();
            return;
        }

        if (target.hasAttribute("data-remove-payment-row")) {
            const row = target.closest("[data-payment-row]");
            if (!(row instanceof HTMLElement) || getPaymentRows().length === 1) {
                return;
            }

            row.remove();
            syncPaymentRowIndexes();
            syncPaymentInputs();
        }
    });
    closeButtons.forEach((button) => button.addEventListener("click", closeCheckoutModal));
    transferCloseButtons.forEach((button) => button.addEventListener("click", closeTransferModal));
    splitCloseButtons.forEach((button) => button.addEventListener("click", closeSplitModal));
    checkoutModal.addEventListener("click", (event) => {
        if (event.target === checkoutModal) {
            closeCheckoutModal();
        }
    });
    transferModal.addEventListener("click", (event) => {
        if (event.target === transferModal) {
            closeTransferModal();
        }
    });
    splitModal.addEventListener("click", (event) => {
        if (event.target === splitModal) {
            closeSplitModal();
        }
    });
    document.addEventListener("keydown", (event) => {
        if (event.key === "Escape" && !checkoutModal.hidden) {
            closeCheckoutModal();
        }
        if (event.key === "Escape" && !transferModal.hidden) {
            closeTransferModal();
        }
        if (event.key === "Escape" && !splitModal.hidden) {
            closeSplitModal();
        }
    });

    const transferForm = transferModal.querySelector("form");
    if (transferForm instanceof HTMLFormElement) {
        transferForm.addEventListener("submit", (event) => {
            if (transferFromTable.value === transferToTable.value) {
                event.preventDefault();
                window.alert("Hãy chọn bàn nhận khác bàn hiện tại.");
            }
        });
    }

    const splitForm = splitModal.querySelector("form");
    if (splitForm instanceof HTMLFormElement) {
        splitForm.addEventListener("submit", (event) => {
            const selectedLine = splitForm.querySelector("input[name='LineKey']:checked");
            if (splitFromTable.value === splitToTable.value) {
                event.preventDefault();
                window.alert("Hãy chọn bàn nhận khác bàn hiện tại.");
                return;
            }

            if (!(selectedLine instanceof HTMLInputElement)) {
                event.preventDefault();
                window.alert("Hãy chọn món cần tách.");
            }
        });
    }

    const checkoutForm = checkoutModal.querySelector("[data-payment-form]");
    if (checkoutForm instanceof HTMLFormElement) {
        checkoutForm.addEventListener("submit", (event) => {
            syncChangeDisplay();
            if (checkoutSubmit.disabled) {
                event.preventDefault();
                return;
            }

            getPaymentRows().forEach((row) => {
                if (row.dataset.paymentAccepted === "true") {
                    return;
                }

                row.querySelectorAll("select, input").forEach((field) => {
                    if (field instanceof HTMLSelectElement || field instanceof HTMLInputElement) {
                        field.disabled = true;
                    }
                });
            });
        });
    }

    applySelection(cards[0]);
})();
