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
- Quản lý nhiều **Tài khoản thuế (MST)** trên cùng một hệ thống.
- Chuyển đổi công ty làm việc tức thì trên thanh Header (1-click switch).
- Cô lập dữ liệu triệt để, không lộ chéo thông tin tài chính giữa các doanh nghiệp.

### 2. Danh Sách & Bộ Lọc Hóa Đơn Chuẩn Nghiệp Vụ
- **Thanh tổng hợp số liệu thời gian thực (Summary Cards)**:
  - Tổng số hóa đơn, Có mã CQT, Không mã CQT, HĐ từ máy tính tiền.
  - Tổng Chưa thuế, Tiền thuế GTGT, Tổng thanh toán tự động cập nhật theo kết quả lọc.
- **Bộ lọc chuyên sâu**:
  - Phân loại: Hóa đơn Mua vào / Hóa đơn Bán ra.
  - Khoảng ngày nhanh: Hôm nay, Tháng này, Tháng trước, Quý này, Năm nay, Tùy chọn ngày.
  - Lọc theo MST người bán, khoảng tiền thanh toán, trạng thái (Mới, Đã điều chỉnh, Thay thế, Bị hủy), trạng thái vào sổ kế toán.
- **Xem chi tiết & XML gốc**:
  - Modal hiển thị **Bản thể hiện hóa đơn điện tử** chuẩn mẫu quy định.
  - Tab tra cứu **Dữ liệu XML gốc** phục vụ đối chiếu pháp lý với cơ quan thuế.

### 3. Dashboard Quản Lý & Tra Cứu Tốc Độ Cao
- **Hộp tra cứu trực tiếp (Live Instant Search)**: Tìm kiếm tức thì theo Số HĐ, Ký hiệu, MST người bán, Tên đơn vị hoặc Mã CQT.
- **Cân đối Thuế GTGT**: Tự động tính toán số thuế GTGT dự kiến phải nộp hoặc còn được khấu trừ chuyển kỳ sau.
- **Tiến độ Vào Sổ Kế Toán**: Giám sát % hóa đơn đã vào sổ và danh sách tồn đọng cần nhập số chứng từ.
- **Biểu đồ tài chính (Chart.js)**: Doanh thu vs Chi phí 12 tháng, cơ cấu mã CQT và top 5 nhà cung cấp lớn nhất.

### 4. Import & Parser XML Hóa Đơn Điện Tử
- Tải lên file đơn lẻ `.xml` hoặc nén hàng loạt `.zip`.
- **Strategy Pattern Parser**: Cấu trúc linh hoạt hỗ trợ XML từ nhiều nhà cung cấp (MISA meInvoice, Viettel S-Invoice, VNPT Invoice, BKAV, chuẩn Tổng cục Thuế QĐ 1450).
- **Nguyên tắc Idempotent**: Khóa duy nhất `(TaxAccountId + InvoiceType + SellerTaxCode + Symbol + Number)` ngăn chặn hoàn toàn việc tạo trùng lặp hóa đơn khi import lại.
- Lưu trữ file XML/PDF gốc an toàn theo cấu trúc thư mục pháp lý.

### 5. Xuất Báo Cáo Hàng Loạt
- **Xuất Excel Chi tiết (dòng hàng)**: Bảng kê chi tiết từng mặt hàng, số lượng, đơn vị tính, đơn giá, tiền thuế, thuế suất % (ClosedXML).
- **Xuất Excel Tổng hợp**: Bảng kê tổng hợp từng hóa đơn phục vụ kê khai thuế.
- **Tải file ZIP hàng loạt**: Đóng gói toàn bộ bản thể hiện HTML và XML gốc thành 1 file nén.

### 6. Đồng Bộ Tự Động (Hangfire Background Jobs)
- Quét định kỳ thư mục theo dõi (`App_Data/WatchFolder/{TaxAccountId}`) để tự động import hóa đơn mới không cần thao tác thủ công.
- Giao diện giám sát Hangfire Dashboard tại `/hangfire`.

### 7. Bảo Mật & Nhật Ký Kiểm Toán (Audit Trail)
- Phân quyền theo vai trò: `Admin`, `Accountant` (Kế toán), `Viewer` (Người xem).
- Ghi vết mọi thao tác nhạy cảm (Đăng nhập, Xem hóa đơn, Xem XML, Xuất Excel, Tải ZIP) phục vụ kiểm toán nội bộ.

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
