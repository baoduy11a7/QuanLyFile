using System;
using System.Collections.Generic;

namespace InvoiceManager.Services.TaxEngine
{
    /// <summary>
    /// Phiên bản biểu mẫu tờ khai thuế (tax_form_version)
    /// </summary>
    public class TaxFormVersion
    {
        public string Code { get; set; } = string.Empty; // Ví dụ: "01_GTGT_TT89", "01_GTGT_TT80"
        public string FormName { get; set; } = "Tờ khai thuế giá trị gia tăng (Mẫu 01/GTGT)";
        public string LegalDocRef { get; set; } = string.Empty; // Thông tư 89/2026/TT-BTC hoặc Thông tư 80/2021/TT-BTC
        public string LegalDocFullTitle { get; set; } = string.Empty;
        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public string Status { get; set; } = "Active"; // Active, Draft, Archived
        public string XmlXsdVersion { get; set; } = "2.1.0";
        public string XmlTemplateCode { get; set; } = "01_GTGT_TT89";
        public string Description { get; set; } = string.Empty;
        public List<TaxFormField> Fields { get; set; } = new();
        public List<TaxRule> Rules { get; set; } = new();
    }

    /// <summary>
    /// Chỉ tiêu kê khai trong biểu mẫu (tax_form_field)
    /// </summary>
    public class TaxFormField
    {
        public string Code { get; set; } = string.Empty; // Ví dụ: "[21]", "[22]", "[23]", "[34]"
        public string Label { get; set; } = string.Empty;
        public string SectionGroup { get; set; } = string.Empty; // "A", "B", "C", "D", "E"
        public string ExpressionFormula { get; set; } = string.Empty; // Biểu thức tính toán, vd: "[26] + [33] + [34a]"
        public string DataType { get; set; } = "Decimal"; // Decimal, Integer, Boolean, String
        public string DataSource { get; set; } = "Calculated"; // "Calculated", "Input", "System_Purchase", "System_Sales"
        public string TooltipFormula { get; set; } = string.Empty;
        public string LegalBasis { get; set; } = string.Empty;
        public int DisplayOrder { get; set; }
        public bool IsOutputVat { get; set; }
        public bool IsInputVat { get; set; }
    }

    /// <summary>
    /// Quy tắc kiểm soát tính đúng đắn và cảnh báo pháp luật (tax_rule)
    /// </summary>
    public class TaxRule
    {
        public string RuleCode { get; set; } = string.Empty;
        public string FormVersionCode { get; set; } = string.Empty;
        public string Severity { get; set; } = "Warning"; // "Error" (chặn xuất), "Warning" (cảnh báo)
        public string RuleName { get; set; } = string.Empty;
        public string ConditionExpression { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string LegalBasis { get; set; } = string.Empty;
        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
    }

    /// <summary>
    /// Tham số pháp lý theo thời gian (tax_parameter)
    /// </summary>
    public class TaxParameter
    {
        public string Key { get; set; } = string.Empty; // Ví dụ: "CashPaymentThreshold", "VatReductionRate"
        public string Name { get; set; } = string.Empty;
        public decimal NumericValue { get; set; }
        public string StringValue { get; set; } = string.Empty;
        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public string LegalBasis { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Ánh xạ trường sang XML theo phiên bản XSD (xml_mapping)
    /// </summary>
    public class TaxXmlMapping
    {
        public string FormVersionCode { get; set; } = string.Empty;
        public string FieldCode { get; set; } = string.Empty;
        public string XmlTag { get; set; } = string.Empty;
        public string DataType { get; set; } = "Number";
        public bool IsMandatory { get; set; } = true;
    }

    /// <summary>
    /// Kết quả kiểm tra quy tắc pháp lý (Rule check result)
    /// </summary>
    public class TaxRuleValidationResult
    {
        public string RuleCode { get; set; } = string.Empty;
        public string RuleName { get; set; } = string.Empty;
        public string Severity { get; set; } = "Warning"; // "Error", "Warning", "Info"
        public string Message { get; set; } = string.Empty;
        public string LegalBasis { get; set; } = string.Empty;
        public bool IsPassed { get; set; }
    }

    /// <summary>
    /// Báo cáo phiên bản pháp lý áp dụng cho hồ sơ khai thuế (Legal version report)
    /// </summary>
    public class TaxLegalVersionReport
    {
        public string FormVersionCode { get; set; } = string.Empty;
        public string LegalDocument { get; set; } = string.Empty;
        public string TaxPeriod { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; } = DateTime.Now;
        public string AppliedRulesSummary { get; set; } = string.Empty;
        public Dictionary<string, string> AppliedParameters { get; set; } = new();
        public List<TaxRuleValidationResult> ValidationResults { get; set; } = new();
    }
}
