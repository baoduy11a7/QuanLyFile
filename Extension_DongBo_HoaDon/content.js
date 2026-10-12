// Content Script chạy trên trang https://hoadondientu.gdt.gov.vn/
// Tự động nhận diện token và phiên đăng nhập của người dùng

(function () {
    console.log("[GDT Sync Helper] Kích hoạt bộ thu nhận Token thông minh trên Cổng Thuế.");

    const JWT_REGEX = /eyJh[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/;

    function extractJwtFromText(text) {
        if (!text || typeof text !== "string") return null;
        const match = text.match(JWT_REGEX);
        return match ? match[0] : null;
    }

    function extractTaxCodeFromPage() {
        // Tìm MST trên giao diện người dùng
        const allText = document.body ? document.body.innerText : "";
        const mstMatch = allText.match(/(?:MST|Mã số thuế)[:\s]+([0-9]{10}(?:-[0-9]{3})?)/i);
        if (mstMatch && mstMatch[1]) return mstMatch[1];

        // Tìm trong các thẻ input hoặc span
        const inputs = document.querySelectorAll("input, span, b, strong, div");
        for (let el of inputs) {
            const val = el.value || el.innerText || "";
            const m = val.match(/^([0-9]{10}(?:-[0-9]{3})?)$/);
            if (m && m[1]) return m[1];
        }
        return "";
    }

    function scanStoragesForToken() {
        let token = null;

        // 1. Quét sessionStorage
        try {
            for (let i = 0; i < sessionStorage.length; i++) {
                const k = sessionStorage.key(i);
                const v = sessionStorage.getItem(k);
                const found = extractJwtFromText(v) || extractJwtFromText(k);
                if (found) { token = found; break; }
            }
        } catch (e) { }

        // 2. Quét localStorage
        if (!token) {
            try {
                for (let i = 0; i < localStorage.length; i++) {
                    const k = localStorage.key(i);
                    const v = localStorage.getItem(k);
                    const found = extractJwtFromText(v) || extractJwtFromText(k);
                    if (found) { token = found; break; }
                }
            } catch (e) { }
        }

        // 3. Quét Cookies
        if (!token) {
            try {
                token = extractJwtFromText(document.cookie);
            } catch (e) { }
        }

        if (token) {
            const mst = extractTaxCodeFromPage();
            chrome.storage.local.set({
                gdtToken: token,
                taxCode: mst || undefined,
                lastUpdated: Date.now()
            }, () => {
                showFloatingBanner(mst);
            });
            return true;
        }
        return false;
    }

    function showFloatingBanner(mst) {
        if (document.getElementById("gdt-sync-banner")) return;

        const banner = document.createElement("div");
        banner.id = "gdt-sync-banner";
        banner.style.cssText = `
            position: fixed;
            bottom: 24px;
            right: 24px;
            background: linear-gradient(135deg, #1e3a8a, #2563eb);
            color: #ffffff;
            padding: 12px 20px;
            border-radius: 12px;
            box-shadow: 0 10px 25px rgba(0, 0, 0, 0.3);
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
            font-size: 13px;
            font-weight: 600;
            z-index: 9999999;
            display: flex;
            align-items: center;
            gap: 12px;
            cursor: pointer;
            transition: transform 0.2s;
        `;
        banner.innerHTML = `
            <span style="font-size: 20px;">⚡</span>
            <div>
                <div>Quản Lý Hóa Đơn: Đã nhận phiên ${mst ? `(MST: ${mst})` : ""}</div>
                <div style="font-size: 11px; opacity: 0.9; font-weight: normal;">Bấm vào Extension trên thanh công cụ để đồng bộ!</div>
            </div>
        `;

        banner.addEventListener("mouseenter", () => banner.style.transform = "scale(1.03)");
        banner.addEventListener("mouseleave", () => banner.style.transform = "scale(1)");

        if (document.body) {
            document.body.appendChild(banner);
        } else {
            window.addEventListener("DOMContentLoaded", () => document.body.appendChild(banner));
        }
    }

    // Quét ngay khi nạp và lặp lại định kỳ
    scanStoragesForToken();
    setInterval(scanStoragesForToken, 2000);
})();
