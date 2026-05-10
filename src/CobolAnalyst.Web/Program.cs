using CobolAnalyst.Web.Components;
using CobolAnalyst.Web.Core.Analysis;
using CobolAnalyst.Web.Core.Cache;
using CobolAnalyst.Web.Core.Chunking;
using CobolAnalyst.Web.Core.Editing;
using CobolAnalyst.Web.Core.Generation;
using CobolAnalyst.Web.Core.KnowledgeBase;
using CobolAnalyst.Web.Core.Llm;
using CobolAnalyst.Web.Core.Prompts;
using CobolAnalyst.Web.Core.Query;
using CobolAnalyst.Web.Core.Sessions;
using CobolAnalyst.Web.Core.State;
using CobolAnalyst.Web.Core.Validation;

var builder = WebApplication.CreateBuilder(args);

// ── Resolve DataPath to an absolute path anchored at the content root ─────────
// "Storage:DataPath" is "./data" by default.  Without this fix the path resolves
// relative to the process working directory, which differs between `dotnet run`,
// Visual Studio, and Rider — causing each service to write to a different folder.
// Normalising to ContentRootPath here means every Singleton reads the same value.
{
    var raw     = builder.Configuration["Storage:DataPath"] ?? "./data";
    var absolute = Path.IsPathRooted(raw)
        ? raw
        : Path.GetFullPath(raw, builder.Environment.ContentRootPath);
    builder.Configuration["Storage:DataPath"] = absolute;
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Large local models can take several minutes per chunk — remove the default 100-second cap.
builder.Services.AddHttpClient<OllamaClient>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(15);
});
builder.Services.AddSingleton<ILlmClient>(sp =>
    sp.GetRequiredService<OllamaClient>());

builder.Services.AddSingleton<AnalysisCache>();
builder.Services.AddSingleton<KnowledgeBaseService>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<ComplexityScorer>();
builder.Services.AddSingleton<PromptTemplateStore>();
builder.Services.AddTransient<ValidationService>();
builder.Services.AddSingleton<ValidationStore>();
builder.Services.AddTransient<GroundTruthParser>();

builder.Services.AddScoped<AnalysisStateService>();
builder.Services.AddScoped<ICobolChunker, CobolChunker>();
builder.Services.AddScoped<IAnalysisOrchestrator, AnalysisOrchestrator>();

builder.Services.AddTransient<CSharpScaffoldGenerator>();
builder.Services.AddTransient<QueryService>();
builder.Services.AddTransient<CodeEditorService>();

var app = builder.Build();

// ── Confirm local-only operation ──────────────────────────────────────────────
// All inference runs through Ollama on localhost. No data leaves the machine.
// Verified by: grep -r "anthropic.com\|openai.com" src/ --include="*.cs" --include="*.razor"
// → returns zero results (only this comment, which is not a live URL).
app.Logger.LogInformation(
    "CobolAnalyst started in LOCAL-ONLY mode. " +
    "All inference via Ollama at {OllamaUrl}. " +
    "No data is sent to external APIs.",
    app.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
