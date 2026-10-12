// Content Script chạy trên trang https://hoadondientu.gdt.gov.vn/
// Tự động nhận diện token và phiên đăng nhập của người dùng

(function () {
    console.log("[GDT Sync Helper] Tiện ích đồng bộ hóa đơn đã kích hoạt trên Cổng Thuế.");

    function findAndSaveToken() {
        let token = null;

        // 1. Kiểm tra localStorage và sessionStorage
        for (let i = 0; i < localStorage.length; i++) {
            const key = localStorage.key(i);
            const val = localStorage.getItem(key);
            if (val && (key.toLowerCase().includes("token") || key.toLowerCase().includes("jwt") || val.startsWith("eyJh"))) {
                token = val;
                break;
            }
        }

        if (!token) {
            for (let i = 0; i < sessionStorage.length; i++) {
                const key = sessionStorage.key(i);
                const val = sessionStorage.getItem(key);
                if (val && (key.toLowerCase().includes("token") || key.toLowerCase().includes("jwt") || val.startsWith("eyJh"))) {
                    token = val;
                    break;
                }
            }
        }

        // Lấy MST hiển thị trên trang nếu có
        let mst = "";
        const mstEls = document.querySelectorAll("span, div, b, p");
        for (let el of mstEls) {
            const text = (el.innerText || "").trim();
            const match = text.match(/(?:MST|Mã số thuế)[:\s]+([0-9]{10}(?:-[0-9]{3})?)/i);
            if (match && match[1]) {
                mst = match[1];
                break;
            }
        }

        if (token) {
            // Chuẩn hóa token nếu có dấu ngoặc kép JSON
            if (token.startsWith('"') && token.endsWith('"')) {
                token = token.slice(1, -1);
            }

            chrome.storage.local.set({
                gdtToken: token,
                taxCode: mst,
                lastUpdated: Date.now()
            }, () => {
                showFloatingBanner(mst);
            });
        }
    }

    function showFloatingBanner(mst) {
        if (document.getElementById("gdt-sync-banner")) return;

        const banner = document.createElement("div");
        banner.id = "gdt-sync-banner";
        banner.style.cssText = `
            position: fixed;
            bottom: 20px;
            right: 20px;
            background: linear-gradient(135deg, #1e3a8a, #2563eb);
            color: #ffffff;
            padding: 12px 18px;
            border-radius: 12px;
            box-shadow: 0 10px 25px rgba(0, 0, 0, 0.25);
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
            font-size: 13px;
            font-weight: 600;
            z-index: 999999;
            display: flex;
            align-items: center;
            gap: 10px;
            cursor: pointer;
            transition: all 0.2s ease;
        `;
        banner.innerHTML = `
            <span style="font-size: 18px;">⚡</span>
            <div>
                <div>Quản Lý Hóa Đơn: Sẵn sàng đồng bộ!</div>
                <div style="font-size: 11px; opacity: 0.85; font-weight: normal;">Nhấp vào biểu tượng Extension để đẩy hóa đơn</div>
            </div>
        `;

        banner.addEventListener("mouseenter", () => banner.style.transform = "scale(1.03)");
        banner.addEventListener("mouseleave", () => banner.style.transform = "scale(1)");

        document.body.appendChild(banner);
    }

    // Chạy kiểm tra định kỳ mỗi 2 giây
    setInterval(findAndSaveToken, 2000);
    setTimeout(findAndSaveToken, 1000);
})();
