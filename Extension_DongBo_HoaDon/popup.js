document.addEventListener("DOMContentLoaded", async () => {
    // Form elements
    const periodSelect = document.getElementById("periodSelect");
    const yearInput = document.getElementById("yearInput");
    const fromDateInput = document.getElementById("fromDate");
    const toDateInput = document.getElementById("toDate");
    const chkSyncXml = document.getElementById("chkSyncXml");
    const syncMstInput = document.getElementById("syncMst");
    const labelCurrentMst = document.getElementById("labelCurrentMst");
    const serverUrlInput = document.getElementById("serverUrl");
    const btnTestConn = document.getElementById("btnTestConn");
    const connResult = document.getElementById("connResult");
    const gdtHeaderStatus = document.getElementById("gdtHeaderStatus");
    const btnRefreshTab = document.getElementById("btnRefreshTab");

    // Action buttons
    const btnSyncIn = document.getElementById("btnSyncIn");
    const btnSyncOut = document.getElementById("btnSyncOut");
    const btnSyncBoth = document.getElementById("btnSyncBoth");
    const btnSyncAllGov = document.getElementById("btnSyncAllGov");

    // Status UI
    const syncStatusBadge = document.getElementById("syncStatusBadge");
    const processTimerText = document.getElementById("processTimerText");
    const step1Text = document.getElementById("step1Text");
    const step2Text = document.getElementById("step2Text");
    const step3Text = document.getElementById("step3Text");
    const summaryPeriodText = document.getElementById("summaryPeriodText");
    const resultsTableBody = document.getElementById("resultsTableBody");
    const progressBarWrap = document.getElementById("progressBarWrap");
    const progressBarInner = document.getElementById("progressBarInner");

    let currentToken = null;
    const JWT_REGEX = /eyJh[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/;

    // 1. Tính toán ngày theo Quý/Năm/Toàn bộ thời gian
    function updateDateRange() {
        const period = periodSelect.value;
        const year = parseInt(yearInput.value) || new Date().getFullYear();
        const pad = (n) => String(n).padStart(2, "0");

        if (period === "ALL_TIME") {
            const today = new Date();
            fromDateInput.value = "2022-01-01";
            toDateInput.value = `${today.getFullYear()}-${pad(today.getMonth() + 1)}-${pad(today.getDate())}`;
        } else if (period === "Q1") {
            fromDateInput.value = `${year}-01-01`;
            toDateInput.value = `${year}-03-31`;
        } else if (period === "Q2") {
            fromDateInput.value = `${year}-04-01`;
            toDateInput.value = `${year}-06-30`;
        } else if (period === "Q3") {
            fromDateInput.value = `${year}-07-01`;
            toDateInput.value = `${year}-09-30`;
        } else if (period === "Q4") {
            fromDateInput.value = `${year}-10-01`;
            toDateInput.value = `${year}-12-31`;
        } else if (period.startsWith("M")) {
            const m = parseInt(period.substring(1));
            const lastDay = new Date(year, m, 0).getDate();
            fromDateInput.value = `${year}-${pad(m)}-01`;
            toDateInput.value = `${year}-${pad(m)}-${pad(lastDay)}`;
        } else if (period === "YEAR") {
            fromDateInput.value = `${year}-01-01`;
            toDateInput.value = `${year}-12-31`;
        }

        const [fy, fm, fd] = fromDateInput.value.split("-");
        const [ty, tm, td] = toDateInput.value.split("-");
        summaryPeriodText.innerText = `${fd}/${fm}/${fy} đến ${td}/${tm}/${ty}`;
    }

    periodSelect.addEventListener("change", updateDateRange);
    yearInput.addEventListener("input", updateDateRange);
    fromDateInput.addEventListener("change", updateDateRange);
    toDateInput.addEventListener("change", updateDateRange);
    updateDateRange();

    // 2. Tải cấu hình đã lưu
    chrome.storage.local.get(["serverUrl", "gdtToken", "taxCode"], (data) => {
        if (data.serverUrl) serverUrlInput.value = data.serverUrl;
        if (data.taxCode) {
            syncMstInput.value = data.taxCode;
            labelCurrentMst.innerText = data.taxCode;
        }
        if (data.gdtToken) {
            currentToken = data.gdtToken;
            updateGdtStatus(true, data.taxCode);
        } else {
            detectTokenFromTab();
        }
    });

    serverUrlInput.addEventListener("change", () => {
        chrome.storage.local.set({ serverUrl: serverUrlInput.value.trim().replace(/\/+$/, "") });
    });

    syncMstInput.addEventListener("input", () => {
        labelCurrentMst.innerText = syncMstInput.value.trim() || "MST DOANH NGHIỆP";
    });

    // 3. Quét Token tự động từ Tab Cổng Thuế
    async function detectTokenFromTab() {
        gdtHeaderStatus.className = "status-badge badge-working";
        gdtHeaderStatus.innerText = "Đang quét tab Cổng Thuế...";

        try {
            const tabs = await chrome.tabs.query({ url: "*://hoadondientu.gdt.gov.vn/*" });
            if (!tabs || tabs.length === 0) {
                chrome.storage.local.get(["gdtToken", "taxCode"], (d) => {
                    if (d.gdtToken) {
                        currentToken = d.gdtToken;
                        updateGdtStatus(true, d.taxCode);
                    } else {
                        updateGdtStatus(false);
                    }
                });
                return;
            }

            const targetTab = tabs[0];
            const injectionResults = await chrome.scripting.executeScript({
                target: { tabId: targetTab.id },
                func: () => {
                    const regex = /eyJh[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/;
                    let foundToken = null;

                    for (let i = 0; i < sessionStorage.length; i++) {
                        const val = sessionStorage.getItem(sessionStorage.key(i));
                        const m = (val || "").match(regex);
                        if (m) { foundToken = m[0]; break; }
                    }

                    if (!foundToken) {
                        for (let i = 0; i < localStorage.length; i++) {
                            const val = localStorage.getItem(localStorage.key(i));
                            const m = (val || "").match(regex);
                            if (m) { foundToken = m[0]; break; }
                        }
                    }

                    let mst = "";
                    const text = document.body ? document.body.innerText : "";
                    const mstMatch = text.match(/(?:MST|Mã số thuế)[:\s]+([0-9]{10}(?:-[0-9]{3})?)/i);
                    if (mstMatch) mst = mstMatch[1];

                    return { token: foundToken, mst: mst };
                }
            });

            if (injectionResults && injectionResults[0] && injectionResults[0].result) {
                const { token, mst } = injectionResults[0].result;
                if (token) {
                    currentToken = token;
                    if (mst) {
                        syncMstInput.value = mst;
                        labelCurrentMst.innerText = mst;
                    }
                    chrome.storage.local.set({ gdtToken: token, taxCode: mst || undefined });
                    updateGdtStatus(true, mst);
                    return;
                }
            }

            chrome.storage.local.get(["gdtToken", "taxCode"], (d) => {
                if (d.gdtToken) {
                    currentToken = d.gdtToken;
                    updateGdtStatus(true, d.taxCode);
                } else {
                    updateGdtStatus(false);
                }
            });
        } catch (e) {
            updateGdtStatus(false);
        }
    }

    function updateGdtStatus(isReady, mst) {
        if (isReady) {
            gdtHeaderStatus.className = "status-badge badge-done";
            gdtHeaderStatus.innerText = mst ? `🟢 Đã kết nối MST: ${mst}` : "🟢 Đã kết nối Cổng Thuế";
            step1Text.innerHTML = `1. Chứng thực tài khoản: <span style="color:#16a34a; font-weight:700;">thành công</span>`;
        } else {
            gdtHeaderStatus.className = "status-badge badge-error";
            gdtHeaderStatus.innerText = "🔴 Chưa có phiên Cổng Thuế";
            step1Text.innerHTML = `1. Chứng thực tài khoản: <span style="color:#dc2626; font-weight:700;">chưa đăng nhập (mở hoadondientu.gdt.gov.vn)</span>`;
        }
    }

    btnRefreshTab.addEventListener("click", detectTokenFromTab);

    // 4. Kiểm tra kết nối tới máy chủ phần mềm
    btnTestConn.addEventListener("click", async () => {
        const url = (serverUrlInput.value || "").trim().replace(/\/+$/, "");
        if (!url) {
            connResult.innerHTML = "<span style='color:red;'>Vui lòng nhập địa chỉ máy chủ.</span>";
            return;
        }

        connResult.innerHTML = "<span>Đang kiểm tra...</span>";
        try {
            let res = await fetch(`${url}/api/sync/companies`);
            if (!res.ok) res = await fetch(`${url}/api/SyncApi/companies`);

            const data = await res.json();
            if (data.success) {
                connResult.innerHTML = `<span style='color:#16a34a; font-weight:600;'>✅ Kết nối tốt! (${data.companies.length} công ty)</span>`;
                if (!syncMstInput.value && data.companies.length > 0) {
                    syncMstInput.value = data.companies[0].taxCode;
                    labelCurrentMst.innerText = data.companies[0].taxCode;
                }
            } else {
                connResult.innerHTML = `<span style='color:red;'>❌ ${data.message}</span>`;
            }
        } catch (err) {
            connResult.innerHTML = `<span style='color:red;'>❌ Không thể kết nối tới máy chủ (${err.message})</span>`;
        }
    });

    // Chia khoảng ngày thành các đoạn nhỏ (mỗi đoạn tối đa 60 ngày) để tránh bị Cổng Thuế chặn hoặc quá tải
    function splitDateRange(startDateStr, endDateStr, maxDays = 60) {
        const chunks = [];
        const [sY, sM, sD] = startDateStr.split("-").map(Number);
        const [eY, eM, eD] = endDateStr.split("-").map(Number);
        let start = new Date(sY, sM - 1, sD);
        let end = new Date(eY, eM - 1, eD);

        if (start > end) {
            const t = start; start = end; end = t;
        }

        let curr = new Date(start);
        const pad = (n) => String(n).padStart(2, "0");

        while (curr <= end) {
            let next = new Date(curr);
            next.setDate(next.getDate() + maxDays - 1);
            if (next > end) next = new Date(end);

            chunks.push({
                fromFormatted: `${pad(curr.getDate())}/${pad(curr.getMonth() + 1)}/${curr.getFullYear()}`,
                toFormatted: `${pad(next.getDate())}/${pad(next.getMonth() + 1)}/${next.getFullYear()}`
            });

            curr = new Date(next);
            curr.setDate(curr.getDate() + 1);
        }
        return chunks;
    }

    // 5. Thuật toán đồng bộ tất cả hóa đơn từ Cổng Thuế
    async function executeSync(typeMode) {
        const targetServer = (serverUrlInput.value || "").trim().replace(/\/+$/, "");
        const taxCode = (syncMstInput.value || "").trim();
        const fromDate = fromDateInput.value;
        const toDate = toDateInput.value;
        const syncXml = chkSyncXml.checked;

        if (!targetServer) {
            alert("Vui lòng nhập địa chỉ máy chủ phần mềm.");
            return;
        }
        if (!taxCode) {
            alert("Vui lòng nhập Mã số thuế doanh nghiệp.");
            return;
        }
        if (!currentToken) {
            alert("Chưa có phiên Cổng Thuế! Hãy đăng nhập trên tab hoadondientu.gdt.gov.vn trước rồi bấm làm mới.");
            return;
        }

        const dateChunks = splitDateRange(fromDate, toDate, 60);

        // Reset UI
        setButtonsDisabled(true);
        progressBarWrap.style.display = "block";
        progressBarInner.style.width = "5%";
        syncStatusBadge.className = "status-badge badge-working";
        syncStatusBadge.innerText = "Đang đồng bộ...";
        resultsTableBody.innerHTML = "";

        const startTime = performance.now();
        let totalMuaVao = 0;
        let totalBanRa = 0;
        const types = typeMode === "BOTH" ? ["MuaVao", "BanRa"] : [typeMode];

        step2Text.innerHTML = `2. Quét ${dateChunks.length} chu kỳ từ ${fromDate} đến ${toDate}...`;

        try {
            let totalSteps = dateChunks.length * types.length;
            let currentStep = 0;

            for (let cIdx = 0; cIdx < dateChunks.length; cIdx++) {
                const chunk = dateChunks[cIdx];

                for (const t of types) {
                    currentStep++;
                    const isBanRa = t === "BanRa";
                    const baseApi = isBanRa 
                        ? "https://hoadondientu.gdt.gov.vn/api/query/invoices/sold" 
                        : "https://hoadondientu.gdt.gov.vn/api/query/invoices/purchase";

                    let state = null;
                    let hasMore = true;
                    let page = 1;

                    while (hasMore) {
                        const pct = Math.min(95, Math.round((currentStep / totalSteps) * 90));
                        progressBarInner.style.width = `${pct}%`;

                        let queryUrl = `${baseApi}?sort=tdlap:desc&size=50&search=tdlap=ge=${chunk.fromFormatted}T00:00:00;tdlap=le=${chunk.toFormatted}T23:59:59`;
                        if (state) {
                            queryUrl += `&state=${encodeURIComponent(state)}`;
                        }

                        step3Text.innerHTML = `3. Đang quét chu kỳ ${cIdx + 1}/${dateChunks.length} (${chunk.fromFormatted} - ${chunk.toFormatted}) - ${isBanRa ? 'Bán ra' : 'Mua vào'} (Trang ${page})...`;

                        const gdtRes = await fetch(queryUrl, {
                            headers: {
                                "Authorization": `Bearer ${currentToken}`,
                                "Accept": "application/json",
                                "request-id": "nibot-" + Math.random().toString(36).substring(2, 10),
                                "End-Point": "/tra-cuu/tra-cuu-hoa-don",
                                "Action": ""
                            }
                        });

                        if (!gdtRes.ok) {
                            if (gdtRes.status === 401 || gdtRes.status === 403) {
                                throw new Error(`Phiên làm việc Cổng Thuế đã hết hạn (Mã ${gdtRes.status}). Vui lòng tải lại trang hoadondientu.gdt.gov.vn.`);
                            }
                            console.warn(`Đoạn ngày ${chunk.fromFormatted} - ${chunk.toFormatted} trả về mã ${gdtRes.status}, tiếp tục đoạn tiếp.`);
                            break;
                        }

                        const gdtData = await gdtRes.json();
                        const rawItems = gdtData.datas || gdtData.data || (Array.isArray(gdtData) ? gdtData : []);

                        if (isBanRa) totalBanRa += rawItems.length;
                        else totalMuaVao += rawItems.length;

                        // Cập nhật thống kê bước 2
                        step2Text.innerHTML = `2. Đã tìm thấy trên Cổng Thuế:<br>` +
                            `&nbsp;&nbsp;&nbsp;&nbsp;- <b>${totalMuaVao}</b> hoá đơn mua vào<br>` +
                            `&nbsp;&nbsp;&nbsp;&nbsp;- <b>${totalBanRa}</b> hoá đơn bán ra`;

                        // Xử lý và hiển thị từng hóa đơn
                        const mappedInvoices = [];
                        for (let i = 0; i < rawItems.length; i++) {
                            const item = rawItems[i];
                            let details = [];
                            let syncTypeLabel = "-";

                            // Nếu người dùng chọn [x] Đồng bộ luôn XML (LX)
                            if (syncXml) {
                                syncTypeLabel = "LX";
                                try {
                                    const xmlUrl = `https://hoadondientu.gdt.gov.vn/api/query/invoices/export-xml?nbmst=${encodeURIComponent(item.nbmst || "")}&khhdon=${encodeURIComponent(item.khhdon || "")}&shdon=${encodeURIComponent(item.shdon || "")}`;
                                    const xmlRes = await fetch(xmlUrl, {
                                        headers: { 
                                            "Authorization": `Bearer ${currentToken}`,
                                            "request-id": "nibot-" + Math.random().toString(36).substring(2, 10)
                                        }
                                    });

                                    if (xmlRes.ok) {
                                        const xmlText = await xmlRes.text();
                                        details = parseDetailsFromXml(xmlText);
                                    }
                                } catch (errX) {
                                    // Bỏ qua lỗi tải XML đơn lẻ
                                }
                            }

                            const mapped = {
                                providerKey: item.id || `${item.nbmst}_${item.khhdon}_${item.shdon}`,
                                invoiceSymbol: item.khhdon || "",
                                invoiceNumber: item.shdon ? String(item.shdon).padStart(7, "0") : "",
                                issueDate: item.tdlap || new Date().toISOString(),
                                sellerTaxCode: item.nbmst || "",
                                sellerName: item.nbten || "",
                                sellerAddress: item.nbdchi || "",
                                buyerTaxCode: item.nmdmst || "",
                                buyerName: item.nmten || "",
                                buyerAddress: item.nmdchi || "",
                                amountBeforeTax: Number(item.tgtcthue) || 0,
                                taxAmount: Number(item.tgtthue) || 0,
                                totalAmount: Number(item.tgtttbso) || 0,
                                cqtCode: item.mcqt || "",
                                status: item.ttxly || "Hóa đơn mới",
                                details: details
                            };

                            mappedInvoices.push(mapped);

                            // Thêm dòng hiển thị bảng trực tiếp
                            const mauSo = item.khhdon ? item.khhdon.charAt(0) : "1";
                            const rowHtml = `
                                <tr>
                                    <td class="${isBanRa ? 'type-banra' : 'type-muavao'}">${isBanRa ? 'BAN_RA' : 'MUA_VAO'}</td>
                                    <td>${(item.tdlap || '').split('T')[0]}</td>
                                    <td>${mauSo}</td>
                                    <td>${item.khhdon || ''}</td>
                                    <td>${item.shdon || ''}</td>
                                    <td>${syncTypeLabel}</td>
                                    <td class="result-ok">OK</td>
                                </tr>
                            `;
                            resultsTableBody.insertAdjacentHTML("afterbegin", rowHtml);
                        }

                        // Đẩy từng lô hóa đơn về máy chủ ngay lập tức (Idempotent)
                        if (mappedInvoices.length > 0) {
                            try {
                                await fetch(`${targetServer}/api/sync/push-invoices`, {
                                    method: "POST",
                                    headers: { "Content-Type": "application/json" },
                                    body: JSON.stringify({
                                        taxCode: taxCode,
                                        invoiceType: isBanRa ? "BanRa" : "MuaVao",
                                        invoices: mappedInvoices
                                    })
                                });
                            } catch (e) {
                                console.warn("Lỗi khi đẩy lô hóa đơn về server:", e);
                            }
                        }

                        // Kiểm tra phân trang Cursor (state)
                        if (gdtData.state && gdtData.state !== state && rawItems.length > 0) {
                            state = gdtData.state;
                            page++;
                        } else {
                            hasMore = false;
                        }
                    }
                }
            }

            const endTime = performance.now();
            const elapsedSec = ((endTime - startTime) / 1000).toFixed(1);
            const totalInvoices = totalMuaVao + totalBanRa;

            progressBarInner.style.width = "100%";
            syncStatusBadge.className = "status-badge badge-done";
            syncStatusBadge.innerText = "đồng bộ xong";

            step3Text.innerHTML = `3. Đồng bộ hóa đơn: <b>hoàn tất</b><br>` +
                `&nbsp;&nbsp;&nbsp;&nbsp;- Tổng số HD đã tải: <b>${totalInvoices}</b> hóa đơn<br>` +
                `&nbsp;&nbsp;&nbsp;&nbsp;- Thời gian xử lý: <b>${elapsedSec}</b> giây`;

        } catch (error) {
            syncStatusBadge.className = "status-badge badge-error";
            syncStatusBadge.innerText = "Lỗi đồng bộ";
            alert(`Lỗi trong quá trình đồng bộ: ${error.message}`);
        } finally {
            setButtonsDisabled(false);
            setTimeout(() => { progressBarWrap.style.display = "none"; }, 1500);
        }
    }

    // Helper: Bóc tách XML bằng DOMParser
    function parseDetailsFromXml(xmlString) {
        const details = [];
        try {
            const parser = new DOMParser();
            const xmlDoc = parser.parseFromString(xmlString, "text/xml");
            const hhdvuList = xmlDoc.getElementsByTagName("HHDVu");

            for (let i = 0; i < hhdvuList.length; i++) {
                const node = hhdvuList[i];
                const getVal = (tag) => {
                    const el = node.getElementsByTagName(tag)[0];
                    return el ? el.textContent.trim() : "";
                };

                const itemCode = getVal("MHHDVu");
                const itemName = getVal("THHDVu");
                const unit = getVal("DVTinh");
                const quantity = parseFloat(getVal("SLuong")) || 1;
                const unitPrice = parseFloat(getVal("DGia")) || 0;
                const amountBeforeTax = parseFloat(getVal("ThTien")) || 0;
                const taxRateStr = getVal("TSuat").replace("%", "");
                const taxRate = parseFloat(taxRateStr) || 10;
                const taxAmount = parseFloat(getVal("TThue")) || 0;

                if (itemName) {
                    details.push({
                        lineNumber: i + 1,
                        itemCode: itemCode,
                        itemName: itemName,
                        unit: unit,
                        quantity: quantity,
                        unitPrice: unitPrice,
                        amountBeforeTax: amountBeforeTax,
                        taxRate: taxRate,
                        taxAmount: taxAmount,
                        totalAmount: amountBeforeTax + taxAmount
                    });
                }
            }
        } catch (e) {
            console.warn("Lỗi phân tích cú pháp XML:", e);
        }
        return details;
    }

    function setButtonsDisabled(disabled) {
        btnSyncIn.disabled = disabled;
        btnSyncOut.disabled = disabled;
        btnSyncBoth.disabled = disabled;
        if (btnSyncAllGov) btnSyncAllGov.disabled = disabled;
    }

    // Gán sự kiện cho các nút
    btnSyncIn.addEventListener("click", () => executeSync("MuaVao"));
    btnSyncOut.addEventListener("click", () => executeSync("BanRa"));
    btnSyncBoth.addEventListener("click", () => executeSync("BOTH"));
    if (btnSyncAllGov) {
        btnSyncAllGov.addEventListener("click", () => {
            periodSelect.value = "ALL_TIME";
            updateDateRange();
            executeSync("BOTH");
        });
    }
});
