using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace InvoiceManager.Jobs.JobTracker
{
    public class JobProgressInfo
    {
        public string JobId { get; set; } = string.Empty;
        public string Status { get; set; } = "Queued"; // Queued, Running, Completed, Failed, Cancelled
        public int Percent { get; set; } = 0;
        public string CurrentStage { get; set; } = "Đang khởi tạo...";
        public int TotalFound { get; set; } = 0;
        public int SuccessCount { get; set; } = 0;
        public int DuplicateCount { get; set; } = 0;
        public int FailedCount { get; set; } = 0;
        public string? ErrorMessage { get; set; }
        public List<string> Logs { get; } = new();
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedAt { get; set; }
        public bool IsFinished => Status is "Completed" or "Failed" or "Cancelled";
    }

    public interface IRemoteJobTracker
    {
        JobProgressInfo CreateJob(string jobId, out CancellationToken cancellationToken);
        void UpdateProgress(string jobId, int percent, string stage, int totalFound = -1, int success = -1, int duplicate = -1, int failed = -1);
        void AddLog(string jobId, string message);
        void CompleteJob(string jobId, string? summary = null);
        void FailJob(string jobId, string error);
        void CancelJob(string jobId);
        JobProgressInfo? GetProgress(string jobId);
    }

    public class RemoteJobTracker : IRemoteJobTracker
    {
        private readonly ConcurrentDictionary<string, JobProgressInfo> _jobs = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _cts = new();

        public JobProgressInfo CreateJob(string jobId, out CancellationToken cancellationToken)
        {
            var info = new JobProgressInfo
            {
                JobId = jobId,
                Status = "Queued",
                Percent = 5,
                CurrentStage = "Đang chờ worker Hangfire tiếp nhận..."
            };
            _jobs[jobId] = info;

            var cts = new CancellationTokenSource();
            _cts[jobId] = cts;
            cancellationToken = cts.Token;

            return info;
        }

        public void UpdateProgress(string jobId, int percent, string stage, int totalFound = -1, int success = -1, int duplicate = -1, int failed = -1)
        {
            if (_jobs.TryGetValue(jobId, out var info))
            {
                lock (info)
                {
                    info.Status = "Running";
                    info.Percent = Math.Clamp(percent, 0, 100);
                    info.CurrentStage = stage;
                    if (totalFound >= 0) info.TotalFound = totalFound;
                    if (success >= 0) info.SuccessCount = success;
                    if (duplicate >= 0) info.DuplicateCount = duplicate;
                    if (failed >= 0) info.FailedCount = failed;
                }
            }
        }

        public void AddLog(string jobId, string message)
        {
            if (_jobs.TryGetValue(jobId, out var info))
            {
                lock (info.Logs)
                {
                    var timeStr = DateTime.Now.ToString("HH:mm:ss");
                    info.Logs.Add($"[{timeStr}] {message}");
                    if (info.Logs.Count > 200) info.Logs.RemoveAt(0); // Giới hạn bộ nhớ
                }
            }
        }

        public void CompleteJob(string jobId, string? summary = null)
        {
            if (_jobs.TryGetValue(jobId, out var info))
            {
                lock (info)
                {
                    info.Status = "Completed";
                    info.Percent = 100;
                    info.CurrentStage = summary ?? "Hoàn thành lấy dữ liệu hóa đơn thành công.";
                    info.FinishedAt = DateTime.UtcNow;
                }
            }
            _cts.TryRemove(jobId, out _);
        }

        public void FailJob(string jobId, string error)
        {
            if (_jobs.TryGetValue(jobId, out var info))
            {
                lock (info)
                {
                    info.Status = "Failed";
                    info.CurrentStage = "Đã dừng do xảy ra lỗi.";
                    info.ErrorMessage = error;
                    info.FinishedAt = DateTime.UtcNow;
                }
                AddLog(jobId, $"❌ LỖI: {error}");
            }
            _cts.TryRemove(jobId, out _);
        }

        public void CancelJob(string jobId)
        {
            if (_cts.TryGetValue(jobId, out var cts))
            {
                cts.Cancel();
            }
            if (_jobs.TryGetValue(jobId, out var info))
            {
                lock (info)
                {
                    info.Status = "Cancelled";
                    info.CurrentStage = "Người dùng đã hủy tiến trình.";
                    info.FinishedAt = DateTime.UtcNow;
                }
                AddLog(jobId, "⚠️ Đã nhận lệnh hủy từ người dùng.");
            }
        }

        public JobProgressInfo? GetProgress(string jobId)
        {
            _jobs.TryGetValue(jobId, out var info);
            return info;
        }
    }
}
