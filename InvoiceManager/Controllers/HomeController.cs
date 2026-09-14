using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using InvoiceManager.Models;

namespace InvoiceManager.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;

    public HomeController(ILogger<HomeController> logger)
    {
        _logger = logger;
    }

    public IActionResult Index()
    {
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        var exceptionFeature = HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
        if (exceptionFeature != null)
        {
            _logger.LogError(exceptionFeature.Error, "Lỗi chưa được xử lý tại đường dẫn {Path}", exceptionFeature.Path);
            ViewBag.ErrorMessage = exceptionFeature.Error.Message;
            ViewBag.ErrorPath = exceptionFeature.Path;
        }

        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
