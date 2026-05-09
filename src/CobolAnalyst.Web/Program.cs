using CobolAnalyst.Web.Components;
using CobolAnalyst.Web.Core.Analysis;
using CobolAnalyst.Web.Core.Cache;
using CobolAnalyst.Web.Core.Chunking;
using CobolAnalyst.Web.Core.Generation;
using CobolAnalyst.Web.Core.KnowledgeBase;
using CobolAnalyst.Web.Core.Llm;
using CobolAnalyst.Web.Core.Prompts;
using CobolAnalyst.Web.Core.Sessions;
using CobolAnalyst.Web.Core.Validation;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<ValidationService>();

builder.Services.AddScoped<ICobolChunker, CobolChunker>();
builder.Services.AddScoped<IAnalysisOrchestrator, AnalysisOrchestrator>();

builder.Services.AddTransient<CSharpScaffoldGenerator>();

var app = builder.Build();

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
