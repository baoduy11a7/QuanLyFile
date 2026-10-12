# HƯỚNG DẪN CÀI ĐẶT & SỬ DỤNG TIỆN ÍCH ĐỒNG BỘ HÓA ĐƠN GDT (CHROME / CỐC CỐC)

Tiện ích này giúp bạn đồng bộ hóa đơn trực tiếp từ **Cổng Tổng cục Thuế (`hoadondientu.gdt.gov.vn`)** vào hệ thống **Quản Lý Hóa Đơn** (cả trên `Localhost` và link `Render`) một cách an toàn 100%, **không bao giờ bị Tường lửa (F5 WAF) của Cổng Thuế chặn**.

---

## 🚀 Bước 1: Cài đặt Tiện ích vào trình duyệt (Chỉ làm 1 lần)

1. Mở trình duyệt **Google Chrome** hoặc **Cốc Cốc**.
2. Trên thanh địa chỉ trình duyệt, gõ:
   - Chrome: `chrome://extensions`
   - Cốc Cốc: `coccoc://extensions`
   *(Hoặc bấm vào dấu 3 chấm góc trên bên phải $\rightarrow$ **Tiện ích mở rộng** $\rightarrow$ **Quản lý tiện ích**)*.
3. Ở góc trên cùng bên phải, bật công tắc **"Chế độ dành cho nhà phát triển" (Developer mode)**.
4. Bấm vào nút **"Tải tiện ích đã giải nén" (Load unpacked)** ở góc trên bên trái.
5. Chọn thư mục:
   `c:\Users\PC\OneDrive\Desktop\QuanLyFile\Extension_DongBo_HoaDon`
6. Nhấn **Select Folder**. Bạn sẽ thấy biểu tượng tiện ích **"Đồng Bộ Hóa Đơn Điện Tử (GDT Sync Helper)"** xuất hiện.
7. Bấm vào biểu tượng mảnh ghép (Extensions) trên thanh công cụ trình duyệt và **Ghim 📌** tiện ích ra ngoài để tiện sử dụng.

---

## ⚡ Bước 2: Sử dụng để Đồng bộ Hóa đơn (Chỉ với 2 bước)

1. **Đăng nhập Cổng Thuế:**
   - Mở tab [https://hoadondientu.gdt.gov.vn](https://hoadondientu.gdt.gov.vn).
   - Đăng nhập tài khoản thuế của bạn bình thường (gõ MST, mật khẩu, mã captcha).
   - Vì bạn đăng nhập trên trình duyệt thật bằng mạng Internet Việt Nam, Cổng Thuế sẽ cho phép đăng nhập thành công 100%.

2. **Bấm Đồng bộ về Phần mềm:**
   - Sau khi đã đăng nhập trên Cổng Thuế, nhấn vào biểu tượng **⚡ GDT Sync Helper** trên thanh công cụ trình duyệt.
   - Kiểm tra thông tin:
     - **Máy chủ phần mềm**: Mặc định là link Render của bạn `https://quanlihoadondientu.onrender.com` (hoặc `http://localhost:5249`).
     - **Mã số thuế**: Nhập MST của doanh nghiệp cần lấy.
     - **Khoảng ngày**: Chọn từ ngày đến ngày cần lấy hóa đơn.
     - **Loại hóa đơn**: Chọn *Mua vào* hoặc *Bán ra*.
   - Nhấn nút **"🚀 Bắt đầu quét & Đẩy về Phần mềm"**.
3. **Kết quả:** Tiện ích sẽ tự động quét toàn bộ hóa đơn từ Cổng Thuế và đẩy về phần mềm của bạn. Màn hình sẽ thông báo số lượng hóa đơn mới được lưu thành công!
