using ChessOverMesh.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
// DetailedErrors surfaces the real exception in the browser's error bar instead of the generic message.
builder.Services.AddServerSideBlazor(o => o.DetailedErrors = true);

// One game/connection per running server (the device's /fromradio is a single-consumer queue), so the
// orchestration service is a singleton shared by every browser circuit.
builder.Services.AddSingleton<GameService>();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
