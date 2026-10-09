using InvoiceManager.Services.Providers.Models;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InvoiceManager.Services.Providers.Mock
{
    public class MockInvoiceSourceProvider : IInvoiceSourceProvider
    {
        public string ProviderName => "Mock";
        public string DisplayName => "Cổng Thuế Giả Lập (Test Mode - Dữ liệu mẫu)";

        public Task<CaptchaChallenge?> GetCaptchaAsync(CancellationToken ct = default)
        {
            // Trả về một SVG captcha mẫu hiển thị chữ "ABCD"
            var svg = @"<svg xmlns='http://www.w3.org/2000/svg' width='160' height='50' viewBox='0 0 160 50'>
                <rect width='100%' height='100%' fill='#f3f4f6'/>
                <line x1='10' y1='10' x2='150' y2='40' stroke='#9ca3af' stroke-width='2'/>
                <line x1='15' y1='38' x2='140' y2='12' stroke='#d1d5db' stroke-width='1.5'/>
                <text x='25' y='35' font-family='Arial, sans-serif' font-size='26' font-weight='bold' fill='#1e40af' letter-spacing='6'>ABCD</text>
            </svg>";

            var base64 = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));

            return Task.FromResult<CaptchaChallenge?>(new CaptchaChallenge
            {
                Key = Guid.NewGuid().ToString("N"),
                ImageBase64 = base64,
                IsSvg = true,
                SvgContent = svg
            });
        }

        public Task<LoginResult> LoginAsync(ProviderCredentials cred, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(cred.Username) || string.IsNullOrWhiteSpace(cred.Password))
            {
                return Task.FromResult(LoginResult.Fail("Vui lòng nhập đầy đủ tên đăng nhập và mật khẩu cổng thuế."));
            }

            // Với Mock, cho phép captcha là "ABCD" hoặc bất kỳ mã nào không rỗng
            if (string.IsNullOrWhiteSpace(cred.CaptchaCode))
            {
                return Task.FromResult(LoginResult.Fail("Vui lòng nhập mã Captcha."));
            }

            var mockJwt = "mock-token-" + Guid.NewGuid().ToString("N");
            return Task.FromResult(LoginResult.Ok(mockJwt, DateTime.UtcNow.AddHours(2)));
        }

        public async IAsyncEnumerable<RemoteInvoice> FetchInvoicesAsync(
            FetchRequest req,
            string token,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var typesToFetch = new List<string>();
            if (req.InvoiceType == "All")
            {
                typesToFetch.Add("MuaVao");
                typesToFetch.Add("BanRa");
            }
            else
            {
                typesToFetch.Add(req.InvoiceType);
            }

            int invIndex = 1;
            var purchaseVendors = new[]
            {
                (TaxCode: "0100109106", Name: "TẬP ĐOÀN CÔNG NGHIỆP - VIỄN THÔNG QUÂN ĐỘI VIETTEL", Item: "Cước dịch vụ Cloud Server & Đường truyền cáp quang", Price: 15000000m, TaxRate: 0.10m),
                (TaxCode: "0102030405", Name: "CÔNG TY CỔ PHẦN MISA CHI NHÁNH HÀ NỘI", Item: "Bản quyền phần mềm meInvoice & Chữ ký số từ xa", Price: 8500000m, TaxRate: 0.10m),
                (TaxCode: "0101234567", Name: "CÔNG TY CỔ PHẦN CÔNG NGHỆ VÀ TRUYỀN THÔNG SAO BẮC ĐẨU", Item: "Máy chủ Server Dell PowerEdge & Thiết bị cân bằng tải", Price: 42000000m, TaxRate: 0.08m),
                (TaxCode: "0309876543", Name: "TỔNG CÔNG TY DỊCH VỤ VIỄN THÔNG VNPT - VINAPHONE", Item: "Cước dịch vụ tổng đài hotline & Tin nhắn Brandname", Price: 6200000m, TaxRate: 0.10m)
            };

            var saleClients = new[]
            {
                (TaxCode: "0100107518", Name: "NGÂN HÀNG THƯƠNG MẠI CỔ PHẦN QUÂN ĐỘI (MB BANK)", Item: "Hợp đồng triển khai hệ thống quản trị & Đối soát hóa đơn số", Price: 85000000m, TaxRate: 0.10m),
                (TaxCode: "0101245486", Name: "TẬP ĐOÀN VINGROUP - CÔNG TY CP", Item: "Gói giải pháp phần mềm tự động hóa kế toán và bóc tách dữ liệu", Price: 120000000m, TaxRate: 0.10m),
                (TaxCode: "0300588569", Name: "CÔNG TY CỔ PHẦN BÁN LẺ KỸ THUẬT SỐ FPT (FPT RETAIL)", Item: "Dịch vụ tích hợp API đồng bộ hóa đơn điện tử Tổng cục Thuế", Price: 45000000m, TaxRate: 0.10m),
                (TaxCode: "0100107574", Name: "TỔNG CÔNG TY HÀNG KHÔNG VIỆT NAM - CTCP (VIETNAM AIRLINES)", Item: "Bảo trì và vận hành hệ thống phần mềm đối soát tài chính", Price: 35000000m, TaxRate: 0.10m)
            };

            foreach (var type in typesToFetch)
            {
                var dataset = type == "MuaVao" ? purchaseVendors : saleClients;
                int dayOffset = 1;

                for (int i = 0; i < dataset.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(120, ct); // Mô phỏng độ trễ API thực tế

                    var itemData = dataset[i];
                    var issueDate = req.FromDate.AddDays(Math.Min(dayOffset * 4, Math.Max(0, (req.ToDate - req.FromDate).Days)));
                    dayOffset++;

                    var symbol = type == "MuaVao" ? "1C25TKT" : "1C25TAA";
                    var number = $"{invIndex:D7}";

                    decimal amountBeforeTax = itemData.Price;
                    decimal taxAmount = Math.Round(amountBeforeTax * itemData.TaxRate, 0);
                    decimal totalAmount = amountBeforeTax + taxAmount;

                    string sellerTaxCode = type == "MuaVao" ? itemData.TaxCode : req.TaxCode;
                    string sellerName = type == "MuaVao" ? itemData.Name : "DOANH NGHIỆP HIỆN TẠI";
                    string buyerTaxCode = type == "MuaVao" ? req.TaxCode : itemData.TaxCode;
                    string buyerName = type == "MuaVao" ? "DOANH NGHIỆP HIỆN TẠI" : itemData.Name;

                    yield return new RemoteInvoice
                    {
                        ProviderKey = $"mock_{type}_{invIndex}_{symbol}_{number}",
                        InvoiceSymbol = symbol,
                        InvoiceNumber = number,
                        IssueDate = issueDate,
                        SellerTaxCode = sellerTaxCode,
                        SellerName = sellerName,
                        BuyerTaxCode = buyerTaxCode,
                        BuyerName = buyerName,
                        AmountBeforeTax = amountBeforeTax,
                        TaxAmount = taxAmount,
                        TotalAmount = totalAmount,
                        InvoiceType = type,
                        CqtCode = $"CQT_{Guid.NewGuid().ToString("N").Substring(0, 10).ToUpper()}",
                        Status = "Mới"
                    };

                    invIndex++;
                }
            }
        }

        public Task<byte[]> DownloadXmlAsync(RemoteInvoice inv, string token, CancellationToken ct = default)
        {
            decimal taxRateVal = inv.AmountBeforeTax > 0 ? Math.Round(inv.TaxAmount / inv.AmountBeforeTax * 100, 0) : 10;
            string taxRateStr = $"{taxRateVal}%";

            // Tạo XML theo chuẩn Tổng cục Thuế Quyết định 1450/QĐ-TCT với chi tiết dòng hàng
            var xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<HDon>
  <TTChung>
    <PBan>2.0.0</PBan>
    <THDon>HÓA ĐƠN GIÁ TRỊ GIA TĂNG</THDon>
    <KHMSHDon>1</KHMSHDon>
    <KHHDon>{inv.InvoiceSymbol}</KHHDon>
    <SHDon>{inv.InvoiceNumber}</SHDon>
    <NLap>{inv.IssueDate:yyyy-MM-dd}</NLap>
    <DVTTe>VND</DVTTe>
    <TGia>1</TGia>
    <HTTToan>TM/CK</HTTToan>
    <MCQT>{inv.CqtCode}</MCQT>
  </TTChung>
  <NDHDon>
    <NBan>
      <Ten>{System.Security.SecurityElement.Escape(inv.SellerName)}</Ten>
      <MST>{inv.SellerTaxCode}</MST>
      <DChi>Số 123 Đường Giải Phóng, Quận Hai Bà Trưng, Hà Nội</DChi>
      <SDThoai>02431234567</SDThoai>
    </NBan>
    <NMua>
      <Ten>{System.Security.SecurityElement.Escape(inv.BuyerName)}</Ten>
      <MST>{inv.BuyerTaxCode}</MST>
      <DChi>Số 456 Đường Nguyễn Huệ, Quận 1, TP Hồ Chí Minh</DChi>
    </NMua>
    <DSHHDVu>
      <HHDVu>
        <TCat>1</TCat>
        <STT>1</STT>
        <THHDVu>{System.Security.SecurityElement.Escape(inv.InvoiceType == "MuaVao" ? "Dịch vụ hạ tầng viễn thông và thiết bị phần mềm chuyên dụng" : "Dịch vụ cung cấp giải pháp công nghệ và phần mềm quản lý hóa đơn")}</THHDVu>
        <DVT>Gói</DVT>
        <SLuong>1</SLuong>
        <DGia>{inv.AmountBeforeTax:F0}</DGia>
        <ThTien>{inv.AmountBeforeTax:F0}</ThTien>
        <TSuat>{taxRateStr}</TSuat>
      </HHDVu>
    </DSHHDVu>
    <TToan>
      <TgTCThue>{inv.AmountBeforeTax:F0}</TgTCThue>
      <TgTThue>{inv.TaxAmount:F0}</TgTThue>
      <TgTTTBSo>{inv.TotalAmount:F0}</TgTTTBSo>
      <TgTTTBChu>{NumberToWords(inv.TotalAmount)}</TgTTTBChu>
    </TToan>
  </NDHDon>
</HDon>";

            return Task.FromResult(Encoding.UTF8.GetBytes(xml));
        }

        private static string NumberToWords(decimal number)
        {
            if (number <= 0) return "Không đồng";
            return $"{number:N0} đồng chẵn";
        }
    }
}
