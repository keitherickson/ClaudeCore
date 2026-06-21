using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using KeithVision.Models;

namespace KeithVision.Controllers;

public class HomeController : Controller
{
    // Index and Privacy were removed; the app lands on Video/Index.
    // Error is kept because it backs the global exception handler (/Home/Error).
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
