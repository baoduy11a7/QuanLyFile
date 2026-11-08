using System;
using System.Collections.Generic;
using System.Linq;

namespace InvoiceManager.Services.TaxEngine
{
    public interface ITaxFormRegistry
    {
        TaxFormVersion ResolveVersionForPeriod(DateTime fromDate, DateTime toDate);
        TaxFormVersion? GetVersionByCode(string code);
        IReadOnlyList<TaxFormVersion> GetAllVersions();
        IReadOnlyList<TaxParameter> GetParametersForPeriod(DateTime date);
        decimal GetParameterValue(string key, DateTime date, decimal defaultValue = 0);
        IReadOnlyList<TaxRule> GetRulesForVersion(string versionCode);
        IReadOnlyList<TaxXmlMapping> GetXmlMappingsForVersion(string versionCode);
        void SaveVersion(TaxFormVersion version, string user);
    }

    public class TaxFormRegistry : ITaxFormRegistry
    {
        private readonly List<TaxFormVersion> _versions = new();
        private readonly List<TaxParameter> _parameters = new();
        private readonly List<TaxXmlMapping> _xmlMappings = new();

        public TaxFormRegistry()
        {
            InitializeDefaultParameters();
            InitializeVersionTT80();
            InitializeVersionTT89();
            InitializeXmlMappings();
        }

        private void InitializeDefaultParameters()
        {
            // Ngưỡng thanh toán không dùng tiền mặt:
            // Trước 01/07/2026: 20 triệu đồng (Luật GTGT cũ, TT 219/2013/TT-BTC)
            // Từ 01/07/2026: 5 triệu đồng (Luật Thuế GTGT mới 48/2024/QH15 & NĐ 252/2026/NĐ-CP)
            _parameters.Add(new TaxParameter
            {
                Key = "CashPaymentThreshold",
                Name = "Ngưỡng chứng từ thanh toán không dùng tiền mặt đối với khấu trừ đầu vào",
                NumericValue = 20000000m,
                EffectiveFrom = new DateTime(2014, 1, 1),
                EffectiveTo = new DateTime(2026, 6, 30),
                LegalBasis = "Thông tư 219/2013/TT-BTC",
                Description = "Hóa đơn từng lần từ 20 triệu đồng trở lên phải có chứng từ thanh toán không dùng tiền mặt"
            });

            _parameters.Add(new TaxParameter
            {
                Key = "CashPaymentThreshold",
                Name = "Ngưỡng chứng từ thanh toán không dùng tiền mặt đối với khấu trừ đầu vào (Mới)",
                NumericValue = 5000000m,
                EffectiveFrom = new DateTime(2026, 7, 1),
                EffectiveTo = null,
                LegalBasis = "Luật Thuế GTGT 48/2024/QH15, Điều 15",
                Description = "Hóa đơn từng lần từ 5 triệu đồng trở lên phải có chứng từ thanh toán không dùng tiền mặt để được khấu trừ"
            });

            // Tỷ lệ giảm thuế theo Nghị quyết 110/2025/UBTVQH15: Giảm 2% (từ 10% xuống 8%)
            _parameters.Add(new TaxParameter
            {
                Key = "VatReductionRate",
                Name = "Tỷ lệ giảm thuế giá trị gia tăng theo Nghị quyết 110/2025/UBTVQH15",
                NumericValue = 0.02m,
                EffectiveFrom = new DateTime(2024, 1, 1),
                EffectiveTo = new DateTime(2026, 12, 31),
                LegalBasis = "Nghị quyết 110/2025/UBTVQH15",
                Description = "Giảm 2% thuế suất thuế GTGT áp dụng cho nhóm hàng hóa, dịch vụ theo quy định"
            });
        }

        private void InitializeVersionTT80()
        {
            var v = new TaxFormVersion
            {
                Code = "01_GTGT_TT80",
                FormName = "Tờ khai thuế giá trị gia tăng (Mẫu 01/GTGT)",
                LegalDocRef = "Thông tư 80/2021/TT-BTC",
                LegalDocFullTitle = "Thông tư số 80/2021/TT-BTC ngày 29/9/2021 của Bộ Tài chính",
                EffectiveFrom = new DateTime(2022, 1, 1),
                EffectiveTo = new DateTime(2026, 6, 30),
                Status = "Active",
                XmlXsdVersion = "2.0.0",
                XmlTemplateCode = "01_GTGT_TT80",
                Description = "Mẫu 01/GTGT áp dụng cho các kỳ tính thuế từ 30/06/2026 trở về trước (Quý 1, Quý 2/2026 hoặc tháng 1..6/2026)"
            };

            // Section A: Không phát sinh
            v.Fields.Add(new TaxFormField { Code = "[21]", Label = "Không phát sinh hoạt động mua, bán trong kỳ", SectionGroup = "A", DataType = "Boolean", DataSource = "Input", DisplayOrder = 1, LegalBasis = "Điều 7 Nghị định 126/2020/NĐ-CP" });

            // Section B: Thuế kỳ trước chuyển sang
            v.Fields.Add(new TaxFormField { Code = "[22]", Label = "Thuế GTGT còn được khấu trừ kỳ trước chuyển sang", SectionGroup = "B", DataType = "Decimal", DataSource = "Prior_Target43", DisplayOrder = 2, LegalBasis = "TT 80/2021/TT-BTC" });

            // Section C: Mua vào
            v.Fields.Add(new TaxFormField { Code = "[23]", Label = "Giá trị của hàng hóa, dịch vụ mua vào", SectionGroup = "C", DataType = "Decimal", DataSource = "System_Purchase_Amount", DisplayOrder = 3, LegalBasis = "Điều 14 Thông tư 219/2013/TT-BTC" });
            v.Fields.Add(new TaxFormField { Code = "[24]", Label = "Thuế GTGT của hàng hóa, dịch vụ mua vào", SectionGroup = "C", DataType = "Decimal", DataSource = "System_Purchase_Tax", DisplayOrder = 4, LegalBasis = "Điều 14 Thông tư 219/2013/TT-BTC" });
            v.Fields.Add(new TaxFormField { Code = "[25]", Label = "Tổng số thuế GTGT của HHDV mua vào được khấu trừ kỳ này", SectionGroup = "C", DataType = "Decimal", DataSource = "System_Purchase_Deductible", DisplayOrder = 5, LegalBasis = "Điều 15 Thông tư 219/2013/TT-BTC" });

            // Section D: Bán ra
            v.Fields.Add(new TaxFormField { Code = "[26]", Label = "Hàng hóa, dịch vụ bán ra không chịu thuế GTGT", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_KCT", DisplayOrder = 6, LegalBasis = "Điều 4 Thông tư 219/2013/TT-BTC" });
            v.Fields.Add(new TaxFormField { Code = "[27]", Label = "Hàng hóa, dịch vụ bán ra chịu thuế suất 0%", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_0", DisplayOrder = 7, LegalBasis = "Điều 9 Thông tư 219/2013/TT-BTC" });
            v.Fields.Add(new TaxFormField { Code = "[28]", Label = "Hàng hóa, dịch vụ bán ra chịu thuế suất 5%", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_5_Amount", DisplayOrder = 8, LegalBasis = "Điều 10 Thông tư 219/2013/TT-BTC" });
            v.Fields.Add(new TaxFormField { Code = "[29]", Label = "Thuế GTGT của HHDV bán ra chịu thuế suất 5%", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_5_Tax", DisplayOrder = 9, LegalBasis = "Điều 10 Thông tư 219/2013/TT-BTC" });
            v.Fields.Add(new TaxFormField { Code = "[30]", Label = "Hàng hóa, dịch vụ bán ra chịu thuế suất 8% (NQ 110)", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_8_Amount", DisplayOrder = 10, LegalBasis = "NQ 110/2025/UBTVQH15" });
            v.Fields.Add(new TaxFormField { Code = "[31]", Label = "Thuế GTGT của HHDV bán ra chịu thuế suất 8%", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_8_Tax", DisplayOrder = 11, LegalBasis = "NQ 110/2025/UBTVQH15" });
            v.Fields.Add(new TaxFormField { Code = "[32]", Label = "Hàng hóa, dịch vụ bán ra chịu thuế suất 10%", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_10_Amount", DisplayOrder = 12, LegalBasis = "Điều 11 Thông tư 219/2013/TT-BTC" });
            v.Fields.Add(new TaxFormField { Code = "[33]", Label = "Thuế GTGT của HHDV bán ra chịu thuế suất 10%", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_10_Tax", DisplayOrder = 13, LegalBasis = "Điều 11 Thông tư 219/2013/TT-BTC" });
            v.Fields.Add(new TaxFormField { Code = "[32b]", Label = "Khoản ghi trên hóa đơn không tính thuế", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_NoTax", DisplayOrder = 14, LegalBasis = "Thông tư 219/2013/TT-BTC" });

            // Các chỉ tiêu tổng hợp và quyết toán
            v.Fields.Add(new TaxFormField { Code = "[34]", Label = "Tổng doanh thu HHDV bán ra", SectionGroup = "D", DataType = "Decimal", DataSource = "Calculated", DisplayOrder = 15, ExpressionFormula = "[26] + [27] + [28] + [30] + [32] + [32b]", TooltipFormula = "[34] = [26] + [27] + [28] + [30] + [32] + [32b]", LegalBasis = "Mẫu 01/GTGT TT80" });
            v.Fields.Add(new TaxFormField { Code = "[35]", Label = "Tổng thuế GTGT của HHDV bán ra", SectionGroup = "D", DataType = "Decimal", DataSource = "Calculated", DisplayOrder = 16, ExpressionFormula = "[29] + [31] + [33]", TooltipFormula = "[35] = [29] + [31] + [33]", LegalBasis = "Mẫu 01/GTGT TT80" });
            v.Fields.Add(new TaxFormField { Code = "[36]", Label = "Thuế GTGT phát sinh trong kỳ", SectionGroup = "E", DataType = "Decimal", DataSource = "Calculated", DisplayOrder = 17, ExpressionFormula = "MAX(0, [35] - [25])", TooltipFormula = "[36] = MAX(0, [35] - [25])", LegalBasis = "Mẫu 01/GTGT TT80" });
            v.Fields.Add(new TaxFormField { Code = "[37]", Label = "Điều chỉnh giảm thuế GTGT còn được khấu trừ các kỳ trước", SectionGroup = "E", DataType = "Decimal", DataSource = "Input", DisplayOrder = 18, LegalBasis = "Mẫu 01/GTGT TT80" });
            v.Fields.Add(new TaxFormField { Code = "[38]", Label = "Điều chỉnh tăng thuế GTGT còn được khấu trừ các kỳ trước", SectionGroup = "E", DataType = "Decimal", DataSource = "Input", DisplayOrder = 19, LegalBasis = "Mẫu 01/GTGT TT80" });
            v.Fields.Add(new TaxFormField { Code = "[40]", Label = "Thuế GTGT còn phải nộp trong kỳ", SectionGroup = "E", DataType = "Decimal", DataSource = "Calculated", DisplayOrder = 20, ExpressionFormula = "MAX(0, [36] - [22] + [37] - [38])", TooltipFormula = "[40] = MAX(0, [36] - [22] + [37] - [38])", LegalBasis = "Mẫu 01/GTGT TT80" });
            v.Fields.Add(new TaxFormField { Code = "[41]", Label = "Thuế GTGT chưa khấu trừ hết kỳ này", SectionGroup = "E", DataType = "Decimal", DataSource = "Calculated", DisplayOrder = 21, ExpressionFormula = "MAX(0, ([25] + [22] - [37] + [38]) - [35])", TooltipFormula = "[41] = MAX(0, ([25] + [22] - [37] + [38]) - [35])", LegalBasis = "Mẫu 01/GTGT TT80" });
            v.Fields.Add(new TaxFormField { Code = "[42]", Label = "Thuế GTGT đề nghị hoàn", SectionGroup = "E", DataType = "Decimal", DataSource = "Input", DisplayOrder = 22, LegalBasis = "Điều 18 Thông tư 219/2013/TT-BTC" });
            v.Fields.Add(new TaxFormField { Code = "[43]", Label = "Thuế GTGT còn được khấu trừ chuyển kỳ sau", SectionGroup = "E", DataType = "Decimal", DataSource = "Calculated", DisplayOrder = 23, ExpressionFormula = "MAX(0, [41] - [42])", TooltipFormula = "[43] = MAX(0, [41] - [42])", LegalBasis = "Mẫu 01/GTGT TT80" });

            // Rules cho TT 80
            v.Rules.Add(new TaxRule { RuleCode = "RULE_ZERO_ACTIVITY", RuleName = "Kiểm tra hoạt động mua bán", Severity = "Error", ConditionExpression = "If [21] == true then InvoicesCount must be 0", Message = "Có hóa đơn phát sinh trong kỳ, không thể chọn chỉ tiêu [21] (Không phát sinh hoạt động mua bán)", LegalBasis = "NĐ 126/2020/NĐ-CP" });
            v.Rules.Add(new TaxRule { RuleCode = "RULE_SETTLEMENT_BALANCE", RuleName = "Quy tắc quyết toán nghĩa vụ thuế", Severity = "Error", ConditionExpression = "([40] > 0 and [43] == 0) or ([43] > 0 and [40] == 0) or ([40] == 0 and [43] == 0)", Message = "Chỉ tiêu [40] (Phải nộp) và [43] (Khấu trừ chuyển sau) không thể cùng lớn hơn 0", LegalBasis = "TT 80/2021/TT-BTC" });
            v.Rules.Add(new TaxRule { RuleCode = "RULE_CASH_THRESHOLD_20M", RuleName = "Ngưỡng thanh toán tiền mặt 20 triệu", Severity = "Warning", ConditionExpression = "Hóa đơn mua vào >= 20tr tiền mặt", Message = "Hóa đơn mua vào từ 20 triệu đồng thanh toán tiền mặt không đủ điều kiện khấu trừ theo TT 219/2013/TT-BTC", LegalBasis = "TT 219/2013/TT-BTC, Điều 15" });

            _versions.Add(v);
        }

        private void InitializeVersionTT89()
        {
            var v = new TaxFormVersion
            {
                Code = "01_GTGT_TT89",
                FormName = "Tờ khai thuế giá trị gia tăng (Mẫu 01/GTGT)",
                LegalDocRef = "Thông tư 89/2026/TT-BTC",
                LegalDocFullTitle = "Thông tư số 89/2026/TT-BTC của Bộ Tài chính (Hiệu lực từ 01/07/2026)",
                EffectiveFrom = new DateTime(2026, 7, 1),
                EffectiveTo = null,
                Status = "Active",
                XmlXsdVersion = "2.1.0",
                XmlTemplateCode = "01_GTGT_TT89",
                Description = "Mẫu 01/GTGT chuẩn mới nhất ban hành kèm Thông tư 89/2026/TT-BTC, căn cứ Luật Quản lý thuế 108/2025/QH15, NĐ 252/2026/NĐ-CP và Luật Thuế GTGT 48/2024/QH15"
            };

            // Section A: Không phát sinh
            v.Fields.Add(new TaxFormField { Code = "[21]", Label = "Không phát sinh hoạt động mua, bán trong kỳ", SectionGroup = "A", DataType = "Boolean", DataSource = "Input", DisplayOrder = 1, LegalBasis = "Nghị định 252/2026/NĐ-CP" });

            // Section B: Thuế kỳ trước chuyển sang
            v.Fields.Add(new TaxFormField { Code = "[22]", Label = "Thuế GTGT còn được khấu trừ kỳ trước chuyển sang", SectionGroup = "B", DataType = "Decimal", DataSource = "Prior_Target43", DisplayOrder = 2, LegalBasis = "Thông tư 89/2026/TT-BTC" });

            // Section C: Mua vào
            v.Fields.Add(new TaxFormField { Code = "[23]", Label = "Giá trị của hàng hóa, dịch vụ mua vào", SectionGroup = "C", DataType = "Decimal", DataSource = "System_Purchase_Amount", DisplayOrder = 3, LegalBasis = "Điều 14 Luật 48/2024/QH15" });
            v.Fields.Add(new TaxFormField { Code = "[23a]", Label = "Hàng hóa, dịch vụ mua vào nhập khẩu", SectionGroup = "C", DataType = "Decimal", DataSource = "System_Purchase_Import", DisplayOrder = 4, LegalBasis = "Thông tư 89/2026/TT-BTC" });
            v.Fields.Add(new TaxFormField { Code = "[24]", Label = "Thuế GTGT của hàng hóa, dịch vụ mua vào", SectionGroup = "C", DataType = "Decimal", DataSource = "System_Purchase_Tax", DisplayOrder = 5, LegalBasis = "Điều 14 Luật 48/2024/QH15" });
            v.Fields.Add(new TaxFormField { Code = "[25]", Label = "Tổng số thuế GTGT của HHDV mua vào được khấu trừ kỳ này", SectionGroup = "C", DataType = "Decimal", DataSource = "System_Purchase_Deductible", DisplayOrder = 6, LegalBasis = "Điều 15 Luật 48/2024/QH15" });

            // Section D: Bán ra
            v.Fields.Add(new TaxFormField { Code = "[26]", Label = "Hàng hóa, dịch vụ bán ra không chịu thuế GTGT", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_KCT", DisplayOrder = 7, LegalBasis = "Điều 5 Luật 48/2024/QH15" });
            v.Fields.Add(new TaxFormField { Code = "[27]", Label = "Hàng hóa, dịch vụ bán ra chịu thuế suất 0%", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_0", DisplayOrder = 8, LegalBasis = "Điều 9 Luật 48/2024/QH15" });
            v.Fields.Add(new TaxFormField { Code = "[28]", Label = "Hàng hóa, dịch vụ bán ra chịu thuế suất 5%", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_5_Amount", DisplayOrder = 9, LegalBasis = "Điều 10 Luật 48/2024/QH15" });
            v.Fields.Add(new TaxFormField { Code = "[29]", Label = "Thuế GTGT của HHDV bán ra chịu thuế suất 5%", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_5_Tax", DisplayOrder = 10, LegalBasis = "Điều 10 Luật 48/2024/QH15" });
            v.Fields.Add(new TaxFormField { Code = "[30]", Label = "Hàng hóa, dịch vụ bán ra chịu thuế suất 8% (NQ 110)", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_8_Amount", DisplayOrder = 11, LegalBasis = "NQ 110/2025/UBTVQH15" });
            v.Fields.Add(new TaxFormField { Code = "[31]", Label = "Thuế GTGT của HHDV bán ra chịu thuế suất 8%", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_8_Tax", DisplayOrder = 12, LegalBasis = "NQ 110/2025/UBTVQH15" });
            v.Fields.Add(new TaxFormField { Code = "[32]", Label = "Hàng hóa, dịch vụ bán ra chịu thuế suất 10%", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_10_Amount", DisplayOrder = 13, LegalBasis = "Điều 10 Luật 48/2024/QH15" });
            v.Fields.Add(new TaxFormField { Code = "[32a]", Label = "Thuế GTGT của HHDV bán ra chịu thuế suất 10%", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_10_Tax", DisplayOrder = 14, LegalBasis = "Điều 10 Luật 48/2024/QH15" });
            v.Fields.Add(new TaxFormField { Code = "[32b]", Label = "Khoản ghi trên HĐ không tính vào giá tính thuế", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_NoTax", DisplayOrder = 15, LegalBasis = "Thông tư 89/2026/TT-BTC [Mới]" });
            v.Fields.Add(new TaxFormField { Code = "[33]", Label = "Tổng doanh thu HHDV bán ra chịu thuế", SectionGroup = "D", DataType = "Decimal", DataSource = "Calculated", DisplayOrder = 16, ExpressionFormula = "[27] + [28] + [30] + [32]", TooltipFormula = "[33] = [27] + [28] + [30] + [32] (Đã loại trừ [32b])", LegalBasis = "Mẫu 01/GTGT TT89" });
            v.Fields.Add(new TaxFormField { Code = "[34a]", Label = "Hàng hóa, dịch vụ không phải kê khai, nộp thuế GTGT", SectionGroup = "D", DataType = "Decimal", DataSource = "System_Sales_NoDeclare", DisplayOrder = 17, LegalBasis = "Thông tư 89/2026/TT-BTC [Mới]" });
            v.Fields.Add(new TaxFormField { Code = "[34]", Label = "Tổng doanh thu HHDV bán ra", SectionGroup = "D", DataType = "Decimal", DataSource = "Calculated", DisplayOrder = 18, ExpressionFormula = "[26] + [33] + [34a]", TooltipFormula = "[34] = [26] + [33] + [34a]", LegalBasis = "Mẫu 01/GTGT TT89" });
            v.Fields.Add(new TaxFormField { Code = "[35]", Label = "Tổng thuế GTGT của HHDV bán ra", SectionGroup = "D", DataType = "Decimal", DataSource = "Calculated", DisplayOrder = 19, ExpressionFormula = "[29] + [31] + [32a]", TooltipFormula = "[35] = [29] + [31] + [32a]", LegalBasis = "Mẫu 01/GTGT TT89" });

            // Section E: Quyết toán
            v.Fields.Add(new TaxFormField { Code = "[36]", Label = "Thuế GTGT phát sinh trong kỳ", SectionGroup = "E", DataType = "Decimal", DataSource = "Calculated", DisplayOrder = 20, ExpressionFormula = "MAX(0, [35] - [25])", TooltipFormula = "[36] = MAX(0, [35] - [25])", LegalBasis = "Mẫu 01/GTGT TT89" });
            v.Fields.Add(new TaxFormField { Code = "[37]", Label = "Điều chỉnh giảm thuế GTGT còn được khấu trừ các kỳ trước", SectionGroup = "E", DataType = "Decimal", DataSource = "Input", DisplayOrder = 21, LegalBasis = "Mẫu 01/GTGT TT89" });
            v.Fields.Add(new TaxFormField { Code = "[38]", Label = "Điều chỉnh tăng thuế GTGT còn được khấu trừ các kỳ trước", SectionGroup = "E", DataType = "Decimal", DataSource = "Input", DisplayOrder = 22, LegalBasis = "Mẫu 01/GTGT TT89" });
            v.Fields.Add(new TaxFormField { Code = "[39a]", Label = "Thuế GTGT nhận bàn giao được khấu trừ", SectionGroup = "E", DataType = "Decimal", DataSource = "Input", DisplayOrder = 23, LegalBasis = "Thông tư 89/2026/TT-BTC" });
            v.Fields.Add(new TaxFormField { Code = "[40a]", Label = "Thuế GTGT nộp thừa kỳ trước chuyển sang", SectionGroup = "E", DataType = "Decimal", DataSource = "Input", DisplayOrder = 24, LegalBasis = "Nghị định 252/2026/NĐ-CP" });
            v.Fields.Add(new TaxFormField { Code = "[40]", Label = "Thuế GTGT còn phải nộp trong kỳ", SectionGroup = "E", DataType = "Decimal", DataSource = "Calculated", DisplayOrder = 25, ExpressionFormula = "MAX(0, [36] - [22] + [37] - [38] - [40a])", TooltipFormula = "[40] = MAX(0, [36] - [22] + [37] - [38] - [40a])", LegalBasis = "Mẫu 01/GTGT TT89" });
            v.Fields.Add(new TaxFormField { Code = "[41]", Label = "Thuế GTGT chưa khấu trừ hết kỳ này", SectionGroup = "E", DataType = "Decimal", DataSource = "Calculated", DisplayOrder = 26, ExpressionFormula = "MAX(0, ([25] + [22] - [37] + [38] + [39a]) - [35])", TooltipFormula = "[41] = MAX(0, ([25] + [22] - [37] + [38] + [39a]) - [35])", LegalBasis = "Mẫu 01/GTGT TT89" });
            v.Fields.Add(new TaxFormField { Code = "[42]", Label = "Thuế GTGT đề nghị hoàn", SectionGroup = "E", DataType = "Decimal", DataSource = "Input", DisplayOrder = 27, LegalBasis = "Điều 18 Luật 48/2024/QH15" });
            v.Fields.Add(new TaxFormField { Code = "[43]", Label = "Thuế GTGT còn được khấu trừ chuyển kỳ sau", SectionGroup = "E", DataType = "Decimal", DataSource = "Calculated", DisplayOrder = 28, ExpressionFormula = "MAX(0, [41] - [42])", TooltipFormula = "[43] = MAX(0, [41] - [42])", LegalBasis = "Mẫu 01/GTGT TT89" });

            // Rules cho TT 89
            v.Rules.Add(new TaxRule { RuleCode = "RULE_ZERO_ACTIVITY", RuleName = "Kiểm tra hoạt động mua bán", Severity = "Error", ConditionExpression = "If [21] == true then InvoicesCount must be 0", Message = "Có hóa đơn phát sinh trong kỳ, không thể chọn chỉ tiêu [21] (Không phát sinh hoạt động mua bán)", LegalBasis = "NĐ 252/2026/NĐ-CP" });
            v.Rules.Add(new TaxRule { RuleCode = "RULE_SETTLEMENT_BALANCE", RuleName = "Quy tắc quyết toán nghĩa vụ thuế", Severity = "Error", ConditionExpression = "([40] > 0 and [43] == 0) or ([43] > 0 and [40] == 0) or ([40] == 0 and [43] == 0)", Message = "Chỉ tiêu [40] (Phải nộp) và [43] (Khấu trừ chuyển sau) không thể cùng lớn hơn 0", LegalBasis = "TT 89/2026/TT-BTC" });
            v.Rules.Add(new TaxRule { RuleCode = "RULE_CASH_THRESHOLD_5M", RuleName = "Ngưỡng thanh toán tiền mặt 5 triệu (Luật mới)", Severity = "Warning", ConditionExpression = "Hóa đơn mua vào >= 5tr thanh toán tiền mặt", Message = "Hóa đơn mua vào từ 5 triệu đồng trở lên thanh toán bằng tiền mặt không đủ điều kiện khấu trừ thuế GTGT theo Luật 48/2024/QH15", LegalBasis = "Luật Thuế GTGT 48/2024/QH15, Điều 15" });
            v.Rules.Add(new TaxRule { RuleCode = "RULE_SALES_SUM_TT89", RuleName = "Ràng buộc tổng doanh thu bán ra", Severity = "Error", ConditionExpression = "[34] == [26] + [33] + [34a]", Message = "Chỉ tiêu [34] phải bằng [26] + [33] + [34a] theo đúng mẫu TT 89/2026", LegalBasis = "TT 89/2026/TT-BTC" });

            _versions.Add(v);
        }

        private void InitializeXmlMappings()
        {
            string[] versions = { "01_GTGT_TT80", "01_GTGT_TT89" };
            foreach (var ver in versions)
            {
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[21]", XmlTag = "ct21" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[22]", XmlTag = "ct22" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[23]", XmlTag = "ct23" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[24]", XmlTag = "ct24" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[25]", XmlTag = "ct25" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[26]", XmlTag = "ct26" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[27]", XmlTag = "ct27" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[28]", XmlTag = "ct28" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[29]", XmlTag = "ct29" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[30]", XmlTag = "ct30" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[31]", XmlTag = "ct31" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[32]", XmlTag = "ct32" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[32a]", XmlTag = "ct33" }); // Trong XSD HTKK, ct33 là thuế 10%
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[33]", XmlTag = "ct33" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[34]", XmlTag = "ct34" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[35]", XmlTag = "ct35" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[36]", XmlTag = "ct36" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[40]", XmlTag = "ct40" });
                _xmlMappings.Add(new TaxXmlMapping { FormVersionCode = ver, FieldCode = "[43]", XmlTag = "ct43" });
            }
        }

        /// <summary>
        /// Tự động chọn phiên bản mẫu biểu theo KỲ TÍNH THUẾ (Nguyên tắc tối thượng 1.4 & 8.3)
        /// </summary>
        public TaxFormVersion ResolveVersionForPeriod(DateTime fromDate, DateTime toDate)
        {
            var match = _versions
                .Where(v => v.Status == "Active")
                .OrderByDescending(v => v.EffectiveFrom)
                .FirstOrDefault(v => fromDate >= v.EffectiveFrom && (v.EffectiveTo == null || fromDate <= v.EffectiveTo.Value));

            if (match != null) return match;

            // Fallback an toàn: nếu kỳ từ 01/07/2026 trở đi chọn TT89, ngược lại chọn TT80
            return fromDate >= new DateTime(2026, 7, 1)
                ? GetVersionByCode("01_GTGT_TT89") ?? _versions.First()
                : GetVersionByCode("01_GTGT_TT80") ?? _versions.First();
        }

        public TaxFormVersion? GetVersionByCode(string code)
        {
            return _versions.FirstOrDefault(v => string.Equals(v.Code, code, StringComparison.OrdinalIgnoreCase));
        }

        public IReadOnlyList<TaxFormVersion> GetAllVersions() => _versions;

        public IReadOnlyList<TaxParameter> GetParametersForPeriod(DateTime date)
        {
            return _parameters
                .Where(p => date >= p.EffectiveFrom && (p.EffectiveTo == null || date <= p.EffectiveTo.Value))
                .ToList();
        }

        public decimal GetParameterValue(string key, DateTime date, decimal defaultValue = 0)
        {
            var param = _parameters
                .Where(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase) && date >= p.EffectiveFrom && (p.EffectiveTo == null || date <= p.EffectiveTo.Value))
                .OrderByDescending(p => p.EffectiveFrom)
                .FirstOrDefault();

            return param?.NumericValue ?? defaultValue;
        }

        public IReadOnlyList<TaxRule> GetRulesForVersion(string versionCode)
        {
            var ver = GetVersionByCode(versionCode);
            return ver?.Rules ?? new List<TaxRule>();
        }

        public IReadOnlyList<TaxXmlMapping> GetXmlMappingsForVersion(string versionCode)
        {
            return _xmlMappings
                .Where(m => string.Equals(m.FormVersionCode, versionCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public void SaveVersion(TaxFormVersion version, string user)
        {
            var existing = _versions.FirstOrDefault(v => string.Equals(v.Code, version.Code, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                _versions.Remove(existing);
            }
            _versions.Add(version);
        }
    }
}

