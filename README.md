# InvoiceManager — Phần Mềm Quản Lý & Tra Cứu Hóa Đơn Điện Tử Doanh Nghiệp

[![.NET 8](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![SQL Server](https://img.shields.io/badge/Database-SQL%20Server-red.svg)](https://www.microsoft.com/sql-server)
[![Hangfire](https://img.shields.io/badge/Background%20Jobs-Hangfire-orange.svg)](https://www.hangfire.io/)
[![ClosedXML](https://img.shields.io/badge/Excel-ClosedXML-green.svg)](https://github.com/ClosedXML/ClosedXML)
[![Standard](https://img.shields.io/badge/Chuẩn-Nghị%20định%20123%20%2F%20Thông%20tư%2078-success.svg)](https://thuvienphapluat.vn)

**InvoiceManager** là phần mềm quản lý, tra cứu, đối soát và xuất báo cáo hóa đơn điện tử dùng thật cho doanh nghiệp và cá nhân kinh doanh tại Việt Nam, tuân thủ nghiêm ngặt theo quy định của Tổng cục Thuế:
- **Nghị định 123/2020/NĐ-CP** quy định về hóa đơn, chứng từ.
- **Thông tư 78/2021/TT-BTC** hướng dẫn thực hiện một số điều của Luật Quản lý thuế và Nghị định số 123.
- **Quyết định 1450/QĐ-TCT** quy định về thành phần chứa dữ liệu nghiệp vụ hóa đơn điện tử.

---

## 🌟 Tính Năng Nổi Bật

### 1. Quản lý Đa Công Ty (Multi-Tenant Tax Account)
- Quản lý nhiều **Tài khoản thuế (MST)** trên cùng một hệ thống (Thêm mới, Tra cứu Cổng Thuế, Xóa an toàn).
- Chuyển đổi công ty làm việc tức thì trên thanh Header (1-click switch).
- Cô lập dữ liệu triệt để, không lộ chéo thông tin tài chính giữa các doanh nghiệp.
- Hỗ trợ xóa tài khoản thuế cùng cơ chế dọn dẹp liên kết và chuyển đổi tenant đang hoạt động tự động.

### 2. Danh Sách & Bộ Lọc Hóa Đơn Chuẩn Nghiệp Vụ (Khớp Giao Diện Tham Khảo)
- **Thanh tổng hợp số liệu thời gian thực (Summary Cards)**:
  - Tổng số hóa đơn, Có mã CQT, Không mã CQT, HĐ từ máy tính tiền.
  - Tổng Chưa thuế, Tiền thuế GTGT, Tổng thanh toán tự động cập nhật theo kết quả lọc.
- **Drawer Bộ lọc chuyên sâu chuẩn ảnh (Offcanvas Filter Drawer)**:
  - Trượt mở mượt mà từ bên trái màn hình với đầy đủ các tiêu chí:
    - **Trạng thái tải file**: Đã tải PDF gốc, Chưa tải PDF, Đã tải XML gốc, Chưa tải XML, Đã tải đủ PDF & XML.
    - **Ký hiệu mẫu số hóa đơn**: 1 (HĐ GTGT), 2 (HĐ bán hàng), 3 (HĐ bán tài sản công), 4 (HĐ dự trữ QG), 5 (Tem, vé, thẻ), 6 (Chứng từ, biên lai).
    - **Ký hiệu hóa đơn**: Tìm chính xác hoặc tương đối theo ký hiệu (ví dụ: `C25TTP`).
    - **Số hóa đơn**: Tìm kiếm theo số hóa đơn cụ thể (ví dụ: `2835`).
    - **Tên người bán** & **MST người bán** (ví dụ: `0309587979`).
    - **Trạng thái hóa đơn**: Hóa đơn mới, Đã thay thế, Đã điều chỉnh, Đã bị hủy.
    - **Kết quả kiểm tra**: Hợp lệ/Bình thường, Cảnh báo rủi ro, Có mã CQT, Không mã CQT, Khởi tạo từ máy tính tiền.
    - **Khoảng ngày lập hóa đơn** & **Khoảng tiền thanh toán** & **Trạng thái vào sổ kế toán**.
- **Xem chi tiết, XML gốc & PDF**:
  - Modal hiển thị **Bản thể hiện hóa đơn điện tử** chuẩn Nghị định 123 với dấu chữ ký số điện tử.
  - Tải file PDF gốc trực tiếp từng hóa đơn chỉ với 1 click.
  - Tab tra cứu **Dữ liệu XML gốc** phục vụ đối chiếu pháp lý với cơ quan thuế.

### 3. Dashboard Quản Lý, Đối Soát & Tra Cứu Tốc Độ Cao
- **Bộ lọc kỳ đối soát linh hoạt theo Ngày - Tháng - Năm**:
  - Lọc nhanh: Hôm nay, Tháng này, Tháng trước, Quý này, Năm nay.
  - Chọn khoảng ngày tùy biến: Chọn chính xác *Từ ngày* - *Đến ngày*.
  - Menu chuyển nhanh theo bất kỳ Tháng (T1 - T12), Quý (Q1 - Q4) hoặc Năm cụ thể.
- **Hộp tra cứu trực tiếp (Live Instant Search)**: Tìm kiếm tức thì theo Số HĐ, Ký hiệu, MST người bán, Tên đơn vị hoặc Mã CQT.
- **Cân đối Thuế GTGT**: Tự động tính toán số thuế GTGT dự kiến phải nộp hoặc còn được khấu trừ chuyển kỳ sau theo kỳ đã chọn.
- **Tiến độ Vào Sổ Kế Toán**: Giám sát % hóa đơn đã vào sổ và danh sách tồn đọng cần nhập số chứng từ.
- **Biểu đồ tài chính (Chart.js)**: Doanh thu vs Chi phí 12 tháng tự động đồng bộ theo mốc thời gian đối soát, cơ cấu mã CQT và top 5 nhà cung cấp lớn nhất.

### 4. Import & Parser XML, ZIP & Excel Hóa Đơn Điện Tử
- Tải lên file đơn lẻ `.xml`, nén hàng loạt `.zip` hoặc bảng kê Excel `.xlsx / .xls`.
- Hỗ trợ tải file mẫu Excel chuẩn cho cả Hóa đơn Mua vào và Bán ra.
- Tự động nhận diện tiêu đề cột thông minh từ bảng kê Cổng Tổng cục Thuế, MISA meInvoice hoặc phần mềm kế toán.
- **Strategy Pattern Parser**: Cấu trúc linh hoạt hỗ trợ XML từ nhiều nhà cung cấp (MISA meInvoice, Viettel S-Invoice, VNPT Invoice, BKAV, chuẩn Tổng cục Thuế QĐ 1450).
- **Nguyên tắc Idempotent**: Khóa duy nhất `(TaxAccountId + InvoiceType + SellerTaxCode + Symbol + Number)` ngăn chặn hoàn toàn việc tạo trùng lặp hóa đơn khi import lại.
- Lưu trữ file XML/Excel/PDF gốc an toàn theo cấu trúc thư mục pháp lý.

### 5. Tải File PDF Gốc Hàng Loạt & Xuất Báo Cáo
- **Tải file PDF gốc hàng loạt (.zip)**: 
  - Chọn hóa đơn qua ô checkbox (hỗ trợ chọn tất cả) hoặc tải tự động theo toàn bộ kết quả lọc hiện tại.
  - Tự động xuất hiện thanh công cụ thao tác hàng loạt nổi (Floating Bulk Action Bar) hiển thị số lượng hóa đơn đã chọn.
  - Đóng gói toàn bộ các file PDF gốc dạng vector chuẩn chữ ký số thành 1 file ZIP (`HoaDon_PDF_Goc_*.zip`), tên từng file được đặt khoa học theo cấu trúc `HD_[Ký hiệu]_[Số HĐ]_[MST].pdf`.
  - Tích hợp engine in PDF headless tự động caching để tốc độ tải về lần sau là tức thì.
- **Tải file đơn lẻ**: Nút tải PDF gốc trực tiếp ngay tại từng dòng hóa đơn và bên trong popup Chi tiết hóa đơn.
- **Xuất Excel Chi tiết (dòng hàng)**: Bảng kê chi tiết từng mặt hàng, số lượng, đơn vị tính, đơn giá, tiền thuế, thuế suất % (ClosedXML).
- **Xuất Excel Tổng hợp**: Bảng kê tổng hợp từng hóa đơn phục vụ kê khai thuế.
- **Tải file ZIP Bản thể hiện & XML**: Đóng gói toàn bộ bản thể hiện HTML và XML gốc thành 1 file nén.

### 6. Đồng Bộ Tự Động (Hangfire Background Jobs)
- Quét định kỳ thư mục theo dõi (`App_Data/WatchFolder/{TaxAccountId}`) để tự động import hóa đơn mới không cần thao tác thủ công.
- Giao diện giám sát Hangfire Dashboard tại `/hangfire`.

### 7. Bảo Mật & Nhật Ký Kiểm Toán (Audit Trail)
- Phân quyền theo vai trò: `Admin`, `Accountant` (Kế toán), `Viewer` (Người xem).
- Ghi vết mọi thao tác nhạy cảm (Đăng nhập, Xem hóa đơn, Xem XML, Xuất Excel, Tải ZIP) phục vụ kiểm toán nội bộ.

### 8. Cài Đặt Tài Khoản & Quản Trị Người Dùng (Account Settings)
- **Hồ sơ cá nhân**: Cập nhật họ tên, số điện thoại, xem email và ngày tham gia hệ thống.
- **Bảo mật**: Đổi mật khẩu tài khoản trực tiếp với cơ chế xác thực an toàn.
- **Phân quyền công ty**: Xem danh sách các công ty/MST mà tài khoản có quyền truy cập, chuyển đổi nhanh công ty làm việc.
- **Quản trị người dùng (Admin)**:
  - Thêm tài khoản người dùng mới (phân vai trò: Admin, Kế toán, Người xem) và tự động gán quyền truy cập vào các công ty.
  - Khóa / Mở khóa tài khoản nhân viên.
  - Đặt lại (reset) mật khẩu cho người dùng.

### 9. Kênh Báo Lỗi & Yêu Cầu Tính Năng (Feature Requests & Feedback)
- **Modal gửi phản hồi nhanh**: Gửi báo lỗi nghiệp vụ, đề xuất tính năng mới hoặc yêu cầu hỗ trợ đối soát từ mọi trang qua menu người dùng.
- **Trang theo dõi tiến độ (`/Feedback`)**:
  - Thống kê số lượng yêu cầu theo trạng thái: *Chờ tiếp nhận*, *Đang xử lý*, *Đã xử lý / Hoàn thành*.
  - Bảng tra cứu, tìm kiếm và xem chi tiết phản hồi đã gửi.
  - Quản trị viên có thể đổi trạng thái xử lý yêu cầu trực tiếp qua menu hành động.

---

## 💻 Tech Stack

- **Backend & Frontend**: ASP.NET Core MVC (.NET 8), Razor View, Bootstrap 5, Bootstrap Icons.
- **Database**: SQL Server, Entity Framework Core Code First (Migrations).
- **Authentication**: ASP.NET Core Identity (SQL Server).
- **XML Processing**: System.Xml.Linq (LINQ to XML).
- **Export**: ClosedXML (Excel), System.IO.Compression (ZIP).
- **Background Jobs**: Hangfire (SQL Server Storage).
- **Logging**: Serilog (Console & Rolling File Audit Log).
- **Data Visualization**: Chart.js.

---

## 🚀 Hướng Dẫn Cài Đặt & Chạy Ứng Dụng

### Yêu cầu môi trường
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [SQL Server](https://www.microsoft.com/sql-server) (MSSQLSERVER hoặc LocalDB)

### Các bước khởi chạy

1. **Clone repository**:
   ```bash
   git clone https://github.com/your-username/InvoiceManager.git
   cd InvoiceManager
   ```

2. **Cấu hình Connection String**:
   Mở file `InvoiceManager/appsettings.json` và kiểm tra chuỗi kết nối:
   ```json
   "ConnectionStrings": {
     "DefaultConnection": "Server=localhost;Database=InvoiceManagerDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True"
   }
   ```

3. **Chạy Migration Database**:
   ```bash
   cd InvoiceManager
   dotnet ef database update
   ```
   *(Hệ thống sẽ tự động tạo database `InvoiceManagerDb` và seed sẵn tài khoản mẫu cùng dữ liệu hóa đơn)*

4. **Khởi chạy ứng dụng**:
   ```bash
   dotnet run --urls=http://localhost:5120
   ```

5. **Truy cập hệ thống**:
   Mở trình duyệt và vào địa chỉ: [http://localhost:5120](http://localhost:5120)

---

## 🔑 Tài Khoản Mẫu Đăng Nhập

Hệ thống có sẵn các nút bấm tự động điền tài khoản tại màn hình đăng nhập:

| Vai trò | Email đăng nhập | Mật khẩu | Quyền hạn |
| :--- | :--- | :--- | :--- |
| **Quản trị viên (Admin)** | `admin@invoicemanager.vn` | `Admin@123456` | Toàn quyền, thêm MST, xem Hangfire |
| **Kế toán trưởng (Accountant)** | `ketoan@invoicemanager.vn` | `Ketoan@123456` | Quản lý hóa đơn, đối soát, xuất Excel/ZIP, Audit Log |
| **Người xem (Viewer)** | `viewer@invoicemanager.vn` | `Viewer@123456` | Chỉ xem hóa đơn, không được sửa đổi/xuất báo cáo |

---

## 📁 Cấu Trúc Dự Án

```
InvoiceManager/
├── Controllers/
│   ├── InvoiceController.cs         # Danh sách, bộ lọc, xem chi tiết & XML
│   ├── DashboardController.cs       # Thống kê, cân đối thuế GTGT, tra cứu nhanh
│   ├── ImportController.cs          # Tải lên XML đơn lẻ hoặc ZIP hàng loạt
│   ├── ExportController.cs          # Xuất Excel chi tiết/tổng hợp & ZIP PDF/HTML
│   ├── TaxAccountController.cs      # Quản lý MST công ty & chuyển đổi công ty
│   ├── AccountController.cs         # Đăng nhập, đăng xuất, phân quyền Identity
│   └── AuditLogController.cs        # Tra cứu nhật ký kiểm toán
├── Models/
│   ├── Entities/                    # TaxAccount, Invoice, InvoiceDetail, UserTaxAccount, AuditLog...
│   └── ViewModels/                  # DashboardViewModel, InvoiceViewModels...
├── Services/
│   ├── Parsers/                     # IInvoiceParser, StandardTctParser, MISA, Viettel, VNPT...
│   ├── InvoiceImportService.cs      # Xử lý import, chống trùng lặp, lưu trữ file gốc
│   ├── ExportService.cs             # Xuất báo cáo ClosedXML & sinh bản thể hiện HTML
│   ├── SyncService.cs               # Đồng bộ tự động thư mục
│   ├── TaxAccountContext.cs         # Quản lý phiên làm việc theo từng MST
│   └── AuditLogService.cs           # Ghi nhật ký kiểm toán
├── Jobs/
│   └── InvoiceAutoSyncJob.cs        # Hangfire recurring job
├── Data/
│   ├── ApplicationDbContext.cs      # EF Core DbContext & Unique Indexes
│   ├── DbInitializer.cs             # Seed dữ liệu thực tế chuẩn NĐ 123
│   └── Migrations/                  # EF Core Code First Migrations
└── Views/                           # Giao diện Razor Views bám sát phần mềm kế toán thực tế
```

---

## 📄 Bản Quyền & Giấy Phép
Dự án được xây dựng phục vụ nhu cầu quản trị tài chính doanh nghiệp Việt Nam. Giấy phép mã nguồn mở MIT License.
