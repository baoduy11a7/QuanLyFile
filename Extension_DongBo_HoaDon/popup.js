document.addEventListener("DOMContentLoaded", async () => {
    const serverUrlInput = document.getElementById("serverUrl");
    const syncMstInput = document.getElementById("syncMst");
    const syncTypeSelect = document.getElementById("syncType");
    const fromDateInput = document.getElementById("fromDate");
    const toDateInput = document.getElementById("toDate");
    const btnTestConn = document.getElementById("btnTestConn");
    const connResult = document.getElementById("connResult");
    const btnStartSync = document.getElementById("btnStartSync");
    const btnCopyToken = document.getElementById("btnCopyToken");
    const badgeGdtStatus = document.getElementById("badgeGdtStatus");
    const btnRefreshTab = document.getElementById("btnRefreshTab");
    const btnToggleManualToken = document.getElementById("btnToggleManualToken");
    const manualTokenBox = document.getElementById("manualTokenBox");
    const manualTokenInput = document.getElementById("manualTokenInput");
    const btnSaveManualToken = document.getElementById("btnSaveManualToken");
    const progressBox = document.getElementById("progressBox");
    const progressBar = document.getElementById("progressBar");
    const progressText = document.getElementById("progressText");
    const progressPercent = document.getElementById("progressPercent");
    const logBox = document.getElementById("logBox");

    let currentToken = null;
    const JWT_REGEX = /eyJh[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/;

    // 1. Khởi tạo ngày tháng mặc định (30 ngày gần nhất)
    const today = new Date();
    const thirtyDaysAgo = new Date();
    thirtyDaysAgo.setDate(today.getDate() - 30);
    toDateInput.value = today.toISOString().split("T")[0];
    fromDateInput.value = thirtyDaysAgo.toISOString().split("T")[0];

    // 2. Tải cấu hình đã lưu
    chrome.storage.local.get(["serverUrl", "gdtToken", "taxCode"], (data) => {
        serverUrlInput.value = data.serverUrl || "https://quanlihoadondientu.onrender.com";
        if (data.taxCode) syncMstInput.value = data.taxCode;
        if (data.gdtToken) {
            currentToken = data.gdtToken;
            manualTokenInput.value = data.gdtToken;
            updateGdtStatus(true, data.taxCode);
        } else {
            detectTokenFromTab();
        }
    });

    serverUrlInput.addEventListener("change", () => {
        chrome.storage.local.set({ serverUrl: serverUrlInput.value.trim().replace(/\/+$/, "") });
    });

    // 3. Quét Token từ Tab Cổng Thuế
    async function detectTokenFromTab() {
        badgeGdtStatus.className = "status-badge badge-warning";
        badgeGdtStatus.innerText = "Đang quét tab...";

        try {
            const tabs = await chrome.tabs.query({ url: "*://hoadondientu.gdt.gov.vn/*" });
            if (!tabs || tabs.length === 0) {
                // Kiểm tra tab đang mở bất kỳ
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

                    // Quét sessionStorage
                    for (let i = 0; i < sessionStorage.length; i++) {
                        const val = sessionStorage.getItem(sessionStorage.key(i));
                        const m = (val || "").match(regex);
                        if (m) { foundToken = m[0]; break; }
                    }

                    // Quét localStorage
                    if (!foundToken) {
                        for (let i = 0; i < localStorage.length; i++) {
                            const val = localStorage.getItem(localStorage.key(i));
                            const m = (val || "").match(regex);
                            if (m) { foundToken = m[0]; break; }
                        }
                    }

                    // Tìm MST
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
                    manualTokenInput.value = token;
                    if (mst && !syncMstInput.value) syncMstInput.value = mst;
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
            console.error(e);
            chrome.storage.local.get(["gdtToken", "taxCode"], (d) => {
                if (d.gdtToken) {
                    currentToken = d.gdtToken;
                    updateGdtStatus(true, d.taxCode);
                } else {
                    updateGdtStatus(false);
                }
            });
        }
    }

    function updateGdtStatus(isReady, mst) {
        if (isReady) {
            badgeGdtStatus.className = "status-badge badge-success";
            badgeGdtStatus.innerText = mst ? `🟢 Đã kết nối MST: ${mst}` : "🟢 Đã có phiên Cổng Thuế";
        } else {
            badgeGdtStatus.className = "status-badge badge-danger";
            badgeGdtStatus.innerText = "🔴 Chưa đăng nhập Cổng Thuế";
        }
    }

    btnRefreshTab.addEventListener("click", detectTokenFromTab);

    // Toggle nhập Token thủ công
    btnToggleManualToken.addEventListener("click", () => {
        manualTokenBox.style.display = manualTokenBox.style.display === "block" ? "none" : "block";
    });

    btnSaveManualToken.addEventListener("click", () => {
        const val = manualTokenInput.value.trim();
        const m = val.match(JWT_REGEX);
        if (m) {
            currentToken = m[0];
            chrome.storage.local.set({ gdtToken: currentToken });
            updateGdtStatus(true);
            alert("Đã lưu Token thành công!");
            manualTokenBox.style.display = "none";
        } else {
            alert("Mã Token không hợp lệ. Chuỗi Token thường bắt đầu bằng 'eyJh...'.");
        }
    });

    function appendLog(msg, type = "info") {
        const time = new Date().toLocaleTimeString();
        const line = document.createElement("div");
        line.className = `log-${type}`;
        line.innerText = `[${time}] ${msg}`;
        logBox.appendChild(line);
        logBox.scrollTop = logBox.scrollHeight;
    }

    function setProgress(percent, text) {
        progressBar.style.width = `${percent}%`;
        progressPercent.innerText = `${percent}%`;
        if (text) progressText.innerText = text;
    }

    // 4. Kiểm tra kết nối máy chủ phần mềm (Hỗ trợ cả /api/sync và /api/SyncApi)
    btnTestConn.addEventListener("click", async () => {
        const url = (serverUrlInput.value || "").trim().replace(/\/+$/, "");
        if (!url) {
            connResult.innerHTML = "<span style='color:red;'>Vui lòng nhập địa chỉ máy chủ.</span>";
            return;
        }

        connResult.innerHTML = "<span>Đang kết nối...</span>";
        try {
            // Thử cả 2 đường dẫn để tương thích 100%
            let res;
            try {
                res = await fetch(`${url}/api/SyncApi/companies`);
                if (!res.ok) res = await fetch(`${url}/api/sync/companies`);
            } catch (err1) {
                res = await fetch(`${url}/api/sync/companies`);
            }

            if (!res.ok) {
                throw new Error(`Mã phản hồi HTTP ${res.status}`);
            }

            const data = await res.json();
            if (data.success) {
                connResult.innerHTML = `<span style='color:green;'>✅ Kết nối tốt! (${data.companies.length} công ty trên hệ thống)</span>`;
                if (!syncMstInput.value && data.companies.length > 0) {
                    syncMstInput.value = data.companies[0].taxCode;
                }
            } else {
                connResult.innerHTML = `<span style='color:red;'>❌ Máy chủ phản hồi: ${data.message}</span>`;
            }
        } catch (err) {
            connResult.innerHTML = `<span style='color:red;'>❌ Không thể kết nối tới ${url} (${err.message}).</span>`;
        }
    });

    // 5. Sao chép Token
    btnCopyToken.addEventListener("click", () => {
        if (!currentToken) {
            alert("Chưa phát hiện Token. Vui lòng mở trang hoadondientu.gdt.gov.vn và đăng nhập trước, hoặc bấm 'Dán Token thủ công'!");
            return;
        }
        navigator.clipboard.writeText(currentToken);
        alert("Đã sao chép Bearer Token thành công vào Clipboard!");
    });

    // 6. Bắt đầu quét & Đẩy hóa đơn
    btnStartSync.addEventListener("click", async () => {
        const targetServer = (serverUrlInput.value || "").trim().replace(/\/+$/, "");
        const taxCode = (syncMstInput.value || "").trim();
        const invoiceType = syncTypeSelect.value;
        const fromDate = fromDateInput.value;
        const toDate = toDateInput.value;

        if (!targetServer) {
            alert("Vui lòng điền địa chỉ máy chủ phần mềm.");
            return;
        }
        if (!taxCode) {
            alert("Vui lòng nhập mã số thuế doanh nghiệp.");
            return;
        }
        if (!currentToken) {
            alert("Chưa có phiên Cổng Thuế! Hãy mở tab hoadondientu.gdt.gov.vn và đăng nhập, hoặc bấm 'Dán Token thủ công'.");
            return;
        }

        btnStartSync.disabled = true;
        progressBox.style.display = "block";
        logBox.innerHTML = "";
        setProgress(5, "Đang chuẩn bị...");
        appendLog(`Bắt đầu đồng bộ hóa đơn cho MST: ${taxCode}...`, "info");

        try {
            const [fY, fM, fD] = fromDate.split("-");
            const [tY, tM, tD] = toDate.split("-");
            const fromFormatted = `${fD}/${fM}/${fY}`;
            const toFormatted = `${tD}/${tM}/${tY}`;

            const baseApi = invoiceType === "BanRa"
                ? "https://hoadondientu.gdt.gov.vn/api/query/invoices/sold"
                : "https://hoadondientu.gdt.gov.vn/api/query/invoices/purchase";

            const queryUrl = `${baseApi}?sort=tdlap:desc&size=100&search=tdlap=ge=${fromFormatted}T00:00:00;tdlap=le=${toFormatted}T23:59:59`;

            appendLog(`Đang gửi yêu cầu tra cứu tới Cổng Tổng cục Thuế...`, "info");
            setProgress(30, "Đang tải dữ liệu từ Cổng Thuế...");

            const gdtRes = await fetch(queryUrl, {
                headers: {
                    "Authorization": `Bearer ${currentToken}`,
                    "Accept": "application/json"
                }
            });

            if (!gdtRes.ok) {
                if (gdtRes.status === 401 || gdtRes.status === 403) {
                    appendLog("Phiên Cổng Thuế đã hết hạn! Vui lòng F5 tab Cổng Thuế để đăng nhập lại.", "error");
                    alert("Phiên Cổng Thuế đã hết hạn. Hãy tải lại trang Cổng Thuế và đăng nhập lại.");
                } else {
                    appendLog(`Lỗi từ Cổng Thuế (HTTP ${gdtRes.status}).`, "error");
                }
                btnStartSync.disabled = false;
                return;
            }

            const gdtData = await gdtRes.json();
            const rawItems = gdtData.datas || gdtData.data || (Array.isArray(gdtData) ? gdtData : []);
            appendLog(`Tìm thấy tổng cộng ${rawItems.length} hóa đơn trên Cổng Thuế.`, "info");

            if (rawItems.length === 0) {
                setProgress(100, "Hoàn tất (0 hóa đơn)");
                appendLog("Không có hóa đơn nào trong khoảng thời gian này.", "success");
                btnStartSync.disabled = false;
                return;
            }

            const mappedInvoices = rawItems.map(item => ({
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
                status: item.ttxly || "Hóa đơn mới"
            }));

            setProgress(65, "Đang đẩy dữ liệu về phần mềm...");
            appendLog(`Đang gửi ${mappedInvoices.length} hóa đơn về máy chủ ${targetServer}...`, "info");

            let pushRes;
            try {
                pushRes = await fetch(`${targetServer}/api/SyncApi/push-invoices`, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({
                        taxCode: taxCode,
                        invoiceType: invoiceType,
                        invoices: mappedInvoices
                    })
                });
                if (!pushRes.ok) {
                    pushRes = await fetch(`${targetServer}/api/sync/push-invoices`, {
                        method: "POST",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify({
                            taxCode: taxCode,
                            invoiceType: invoiceType,
                            invoices: mappedInvoices
                        })
                    });
                }
            } catch (errP) {
                pushRes = await fetch(`${targetServer}/api/sync/push-invoices`, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({
                        taxCode: taxCode,
                        invoiceType: invoiceType,
                        invoices: mappedInvoices
                    })
                });
            }

            const pushResult = await pushRes.json();

            if (pushResult.success) {
                setProgress(100, "Hoàn tất!");
                appendLog(`✅ Thành công: ${pushResult.inserted} hóa đơn mới đã được lưu!`, "success");
                if (pushResult.duplicated > 0) {
                    appendLog(`ℹ️ ${pushResult.duplicated} hóa đơn đã tồn tại trước đó (bỏ qua trùng).`, "info");
                }
                alert(`Đồng bộ thành công!\n- Hóa đơn mới: ${pushResult.inserted}\n- Trùng lặp bỏ qua: ${pushResult.duplicated}`);
            } else {
                appendLog(`❌ Lỗi lưu dữ liệu: ${pushResult.message}`, "error");
                alert(`Lỗi: ${pushResult.message}`);
            }
        } catch (error) {
            appendLog(`❌ Ngoại lệ: ${error.message}`, "error");
            alert(`Lỗi trong quá trình đồng bộ: ${error.message}`);
        } finally {
            btnStartSync.disabled = false;
        }
    });
});
