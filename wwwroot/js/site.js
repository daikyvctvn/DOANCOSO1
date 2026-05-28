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

    if (liveContainer.classList.contains("customer-page")) {
        return;
    }

    window.setInterval(() => {
        if (document.body.classList.contains("modal-open") || document.querySelector("[data-checkout-modal]:not([hidden])")) {
            return;
        }

        const activeElement = document.activeElement;
        if (activeElement && ["INPUT", "TEXTAREA", "SELECT"].includes(activeElement.tagName)) {
            return;
        }

        window.location.replace(refreshUrl);
    }, seconds * 1000);
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
        !(checkoutSubmit instanceof HTMLButtonElement)) {
        return;
    }

    const baseAddDishUrl = addDishLink.getAttribute("href") || "";
    let selectedCard = cards[0];

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

    const openCheckoutModal = () => {
        if (selectedCard?.dataset.hasActiveOrder !== "true") {
            return;
        }

        renderCheckoutDetails(selectedCard);
        checkoutModal.hidden = false;
        document.body.classList.add("modal-open");
    };

    const closeCheckoutModal = () => {
        checkoutModal.hidden = true;
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
        paymentButton.textContent = hasActiveOrder ? "Thanh toán" : "Bàn này chưa có order";

        if (!checkoutModal.hidden) {
            renderCheckoutDetails(card);
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
    checkoutModal.addEventListener("click", (event) => {
        if (event.target === checkoutModal) {
            closeCheckoutModal();
        }
    });
    document.addEventListener("keydown", (event) => {
        if (event.key === "Escape" && !checkoutModal.hidden) {
            closeCheckoutModal();
        }
    });

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
