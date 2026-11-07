using System;
using System.Collections.Generic;

namespace InvoiceManager.Models.ViewModels
{
    public class MultiCompanySyncRequestModel
    {
        public List<int> AccountIds { get; set; } = new();
        public string Direction { get; set; } = "All"; // "All", "BanRa", "MuaVao"
        public string Category { get; set; } = "All"; // "All", "NoCashRegister", "OnlyCashRegister"
        public string? FromDate { get; set; }
        public string? ToDate { get; set; }
    }

    public class MultiCompanyAccountItemDto
    {
        public int Id { get; set; }
        public string TaxCode { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string? AliasName { get; set; }
        public bool HasCredentials { get; set; }
        public bool HasActiveToken { get; set; }
        public bool IsCurrent { get; set; }
    }

    public class MultiCompanyJobStatusDto
    {
        public int AccountId { get; set; }
        public string TaxCode { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string JobId { get; set; } = string.Empty;
        public string Status { get; set; } = "Pending"; // "Pending", "Running", "Completed", "Failed"
        public int Percent { get; set; }
        public string Stage { get; set; } = string.Empty;
        public int TotalFound { get; set; }
        public int SuccessCount { get; set; }
        public int NewCount { get; set; }
        public int UpdatedCount { get; set; }
        public int DuplicateCount { get; set; }
        public int FailedCount { get; set; }
        public int FilesDownloadedCount { get; set; }
        public string? ErrorMessage { get; set; }
        public bool IsFinished { get; set; }
    }
}
