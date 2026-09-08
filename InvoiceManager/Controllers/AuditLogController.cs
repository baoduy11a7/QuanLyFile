using InvoiceManager.Data;
using InvoiceManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Threading.Tasks;

namespace InvoiceManager.Controllers
{
    [Authorize(Roles = "Admin,Accountant")]
    public class AuditLogController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly ITaxAccountContext _taxAccountContext;

        public AuditLogController(ApplicationDbContext db, ITaxAccountContext taxAccountContext)
        {
            _db = db;
            _taxAccountContext = taxAccountContext;
        }

        public async Task<IActionResult> Index(int page = 1)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var query = _db.AuditLogs.AsQueryable();

            if (!User.IsInRole("Admin") && taxAccountId.HasValue)
            {
                query = query.Where(a => a.TaxAccountId == taxAccountId.Value || a.TaxAccountId == null);
            }

            int pageSize = 25;
            var total = await query.CountAsync();
            var logs = await query
                .OrderByDescending(a => a.Timestamp)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = (int)System.Math.Ceiling(total / (double)pageSize);

            return View(logs);
        }
    }
}
