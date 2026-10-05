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
            // Tạo 4 hóa đơn giả lập theo ngày yêu cầu
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

            int count = 1;
            var sampleSellers = new[]
            {
                ("0101234567", "CÔNG TY CỔ PHẦN CÔNG NGHỆ VÀ TRUYỀN THÔNG SAO BẮC ĐẨU"),
                ("0309876543", "TỔNG CÔNG TY DỊCH VỤ VIỄN THÔNG VNPT - VINAPHONE"),
                ("0100109106", "TẬP ĐOÀN CÔNG NGHIỆP - VIỄN THÔNG QUÂN ĐỘI VIETTEL"),
                ("0102030405", "CÔNG TY CỔ PHẦN MISA CHI NHÁNH HÀ NỘI")
            };

            foreach (var type in typesToFetch)
            {
                for (int i = 0; i < sampleSellers.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(100, ct); // Mô phỏng độ trễ mạng nhẹ

                    var seller = sampleSellers[i];
                    var issueDate = req.FromDate.AddDays(Math.Min(i * 3, Math.Max(0, (req.ToDate - req.FromDate).Days)));
                    var symbol = type == "MuaVao" ? "1C25TKT" : "1C25TAA";
                    var number = $"{count:D7}";

                    yield return new RemoteInvoice
                    {
                        ProviderKey = $"mock_{type}_{count}_{symbol}_{number}",
                        InvoiceSymbol = symbol,
                        InvoiceNumber = number,
                        IssueDate = issueDate,
                        SellerTaxCode = type == "MuaVao" ? seller.Item1 : req.TaxCode,
                        SellerName = type == "MuaVao" ? seller.Item2 : "DOANH NGHIỆP ĐANG CHỌN",
                        BuyerTaxCode = type == "MuaVao" ? req.TaxCode : seller.Item1,
                        BuyerName = type == "MuaVao" ? "DOANH NGHIỆP ĐANG CHỌN" : seller.Item2,
                        AmountBeforeTax = 10000000m * (i + 1),
                        TaxAmount = 1000000m * (i + 1),
                        TotalAmount = 11000000m * (i + 1),
                        InvoiceType = type,
                        CqtCode = $"CQT_{Guid.NewGuid().ToString("N").Substring(0, 10).ToUpper()}",
                        Status = "Mới"
                    };

                    count++;
                }
            }
        }

        public Task<byte[]> DownloadXmlAsync(RemoteInvoice inv, string token, CancellationToken ct = default)
        {
            // Tạo XML theo chuẩn Tổng cục Thuế Quyết định 1450/QĐ-TCT
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
      <Ten>{inv.SellerName}</Ten>
      <MST>{inv.SellerTaxCode}</MST>
      <DChi>Số 123 Đường Giải Phóng, Quận Hai Bà Trưng, Hà Nội</DChi>
      <SDThoai>02431234567</SDThoai>
    </NBan>
    <NMua>
      <Ten>{inv.BuyerName}</Ten>
      <MST>{inv.BuyerTaxCode}</MST>
      <DChi>Số 456 Đường Nguyễn Huệ, Quận 1, TP Hồ Chí Minh</DChi>
    </NMua>
    <DSHHDVu>
      <HHDVu>
        <TCat>1</TCat>
        <STT>1</STT>
        <THHDVu>Dịch vụ phần mềm quản lý hóa đơn điện tử gói chuyên nghiệp</THHDVu>
        <DVT>Gói</DVT>
        <SLuong>1</SLuong>
        <DGia>{inv.AmountBeforeTax:F0}</DGia>
        <ThTien>{inv.AmountBeforeTax:F0}</ThTien>
        <TSuat>10%</TSuat>
      </HHDVu>
    </DSHHDVu>
    <TToan>
      <TgTCThue>{inv.AmountBeforeTax:F0}</TgTCThue>
      <TgTThue>{inv.TaxAmount:F0}</TgTThue>
      <TgTTTBSo>{inv.TotalAmount:F0}</TgTTTBSo>
      <TgTTTBChu>Mười một triệu đồng chẵn</TgTTTBChu>
    </TToan>
  </NDHDon>
</HDon>";

            return Task.FromResult(Encoding.UTF8.GetBytes(xml));
        }
    }
}
