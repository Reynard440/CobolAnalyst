# CobolAnalyst

CobolAnalyst is a local-first static analysis tool that uses the Anthropic Claude API to extract business rules, data definitions, and control flow from legacy COBOL source files. It supports natural-language querying of analysis results and generates C# migration scaffolds — bridging the gap between ageing mainframe code and modern software delivery. The tool was independently designed as a demonstration of LLM-augmented static analysis techniques applied to one of the most persistent problems in enterprise computing: COBOL modernisation.

An estimated 800 billion lines of COBOL remain in active production globally, underpinning major banks, government agencies, and insurance carriers. Despite its age, COBOL processes roughly 95% of ATM transactions and 80% of in-person transactions worldwide. Organisations that want to modernise face a fundamental challenge: the business rules embedded in COBOL programs were often never written down anywhere else, and the original authors are long gone. CobolAnalyst treats that knowledge extraction problem as an LLM task.

---

## Architecture

```
COBOL Files
    │
    ▼
┌─────────────┐
│   Chunker   │  Splits at paragraph / division boundaries (regex)
│             │  Merges small chunks (< 300 estimated tokens) to reduce API calls
└──────┬──────┘
       │  List<CobolChunk>
       ▼
┌──────────────────┐
│ ComplexityScorer │  Scores each chunk Low / Medium / High
│                  │  based on IF nesting depth, PERFORM count, branch count
└──────┬───────────┘
       │  ComplexityTier → adaptive prompt instruction
       ▼
┌──────────────────┐
│  PromptBuilder   │  Injects: persona, adaptive instruction, worked example,
│                  │  suppression list, knowledge-base hints, JSON schema
└──────┬───────────┘
       │  Prompt strings
       ▼
┌──────────────────────┐
│  Anthropic API       │  POST /v1/messages  stream:true
│  (raw HttpClient)    │  SSE → IAsyncEnumerable<string>
│  claude-sonnet-4-... │  Up to 4 concurrent chunks (SemaphoreSlim)
│  Exponential backoff │  3 retries on 529 / 503 / 429
└──────┬───────────────┘
       │  Raw SSE token stream
       ▼
┌──────────────┐
│  JSON Repair │  Bracket-stack closer for truncated responses
│  + Parser    │  Strips markdown fences; deserialises ExtractedRule list
└──────┬───────┘
       │
       ▼
┌────────────────┐
│ Deduplication  │  Jaccard similarity on normalised descriptions (threshold 0.82)
│                │  Marks rules appearing in 3+ chunks as cross-cutting
└──────┬─────────┘
       │  List<ExtractedRule>
       ▼
┌───────────────────────────────┐
│  Rule Cards UI (Analyst page) │  Label, type badge, confidence badge,
│                               │  COBOL reference, migration notes
│  Accept / Reject / Flag       │  Decisions persisted to session JSON
└──────┬────────────────────────┘
       │  Accepted rules
       ▼
┌─────────────────────────┐     ┌────────────────────┐
│  Session Store          │     │  Knowledge Base     │
│  /data/sessions/{id}    │     │  High-confidence    │
│  .json  (full session   │     │  accepted rules →   │
│  + all decisions)       │     │  prompt hints for   │
└─────────────────────────┘     │  future analyses    │
                                └────────────────────┘
       │  Accepted rules
       ▼
┌─────────────────────────────┐
│  C# Scaffold Generator      │  One file per RuleType namespace
│                             │  PascalCase class stubs with XML docs
│  Roslyn syntax validation   │  → /data/generated/{sessionId}/
└─────────────────────────────┘
```

---

## Key Technical Decisions

**Raw `HttpClient` instead of an Anthropic SDK**
The Anthropic .NET ecosystem has no official SDK. Using `HttpClient` directly forces the implementation to handle the actual API contract — SSE framing, `data:` line parsing, `content_block_delta` event types, and exponential back-off — rather than hiding it behind an abstraction. For a portfolio piece, this makes the API-level understanding explicit and auditable.

**Filesystem JSON instead of a database**
The tool is meant to run locally on a developer's machine with zero infrastructure. SQLite or any ORM adds a dependency and a migration story; a directory of JSON files is trivially inspectable, diffable with `git diff`, and portable across machines with a simple `xcopy`. The trade-off is that there is no indexing, so session listing does a linear file scan — acceptable at the scale of dozens of sessions.

**Paragraph-boundary chunking instead of token or line splitting**
COBOL business logic is organised into named paragraphs (`COMPUTE-OVERTIME.`, `VALIDATE-ACCOUNT.`). A chunk that perfectly contains one paragraph gives the LLM a semantically coherent unit: one verb phrase, one set of local variables, one set of PERFORM calls. Line-based splitting would routinely cut through an `IF ... END-IF` block, producing malformed context; token-based splitting has the same problem. Paragraph splitting preserves the logical unit that the original developer chose.

**Complexity-adaptive prompting**
Early prototyping showed that a uniform "describe this COBOL" prompt hallucinated on deeply nested `EVALUATE` blocks while being verbose on trivial `MOVE` statements. Scoring each chunk first and injecting a tier-appropriate instruction ("trace every execution path" vs "be concise") reduces both over-generation and under-generation. The score runs locally in milliseconds with zero API calls.

**Streaming LLM output**
Analysing a realistic 150-line COBOL file with six paragraphs can take 20–30 seconds of LLM wall-clock time if collected synchronously. Streaming lets the progress list update token-by-token, giving the analyst immediate visual feedback that the system is working. The Blazor Server `InvokeAsync(StateHasChanged)` pattern propagates each token to the browser over the existing SignalR connection.

---

## Setup

### Prerequisites
- .NET 8 SDK
- An Anthropic API key (get one at console.anthropic.com)

### Steps

```bash
git clone <repo-url>
cd CobolAnalyst

# Copy the example config and add your API key
cp appsettings.example.json src/CobolAnalyst.Web/appsettings.json
# Edit src/CobolAnalyst.Web/appsettings.json and set Anthropic:ApiKey

dotnet run --project src/CobolAnalyst.Web
```

Open your browser at `https://localhost:5001` (or the URL shown in the terminal).

On the **Analyst** page, click the upload zone and select one or more files from the `sample-data/` directory, then click **Analyse**.

---

## Sample Output

After analysing `PAYROLL.cbl`, the tool extracts rules similar to the following:

```json
{
  "rules": [
    {
      "id": "c3f1a2b4-d5e6-7890-ab12-cd3456ef7890",
      "label": "Overtime Hours Threshold Rule",
      "type": "Calculation",
      "description": "Employees who work more than 40 hours in a week are entitled to overtime pay at 1.5 times the regular hourly rate for each hour beyond 40. Hours at or below 40 are compensated at the base rate only. The overtime supplement is calculated separately and combined with regular pay to produce gross pay.",
      "cobol_reference": "2200-CALC-OVERTIME, lines 58-67",
      "confidence": "High",
      "migration_notes": "Use decimal arithmetic in C# to avoid floating-point rounding on pay calculations. The 40-hour threshold may be configurable by employment category; check for any table-driven override before hardcoding."
    },
    {
      "id": "e7d8c9b0-f1a2-3456-bc78-de9012fg3456",
      "label": "Progressive Tax Bracket Application",
      "type": "Calculation",
      "description": "Tax is computed in four progressive brackets keyed on gross pay: 0-300 at 10%, 301-600 at 18%, 601-1000 at 24%, and above 1000 at 30%. Only the marginal portion of gross pay in each bracket is taxed at that bracket's rate. The total tax is the sum of each bracket's contribution.",
      "cobol_reference": "2300-CALC-TAX, lines 69-90",
      "confidence": "High",
      "migration_notes": "Bracket boundaries and rates should be externalised to configuration, not hardcoded; tax law changes frequently. C# decimal type is required. Consider whether this is gross-pay tax or taxable-income tax — deductions ordering matters."
    },
    {
      "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "label": "Health Insurance Deduction Eligibility",
      "type": "BusinessRule",
      "description": "Health insurance is deducted from net pay only when the employee's health-flag field is set to 'Y'. The deduction amount is a flat 85.00 per pay period regardless of coverage level. Employees without the flag set receive no deduction.",
      "cobol_reference": "2400-CALC-DEDUCTIONS, lines 92-102",
      "confidence": "Medium",
      "migration_notes": "The flat 85.00 amount is almost certainly wrong for a real payroll — verify whether this is a per-plan lookup that the original system had in a separate config file. The Y/N flag maps naturally to a C# bool property."
    }
  ]
}
```

---

## Project Structure

```
CobolAnalyst/
├── CobolAnalyst.sln
├── README.md
├── appsettings.example.json
├── .gitignore
├── sample-data/
│   ├── PAYROLL.cbl       Weekly payroll: gross pay, tax, overtime, deductions
│   ├── INVCTL.cbl        Inventory control: reorder points, stock movement
│   ├── LOANAMRT.cbl      Loan amortisation: monthly payment, early settlement
│   ├── CUSTMAST.cbl      Customer master: add/update/delete validation
│   ├── GLEDGER.cbl       General ledger: debit/credit posting, period close
│   └── RPTGEN.cbl        Report generation: date filter, subtotals, page breaks
│
└── src/
    └── CobolAnalyst.Web/
        ├── Core/
        │   ├── Chunking/          ICobolChunker, CobolChunker
        │   ├── Analysis/          IAnalysisOrchestrator, AnalysisOrchestrator,
        │   │                      ComplexityScorer
        │   ├── Llm/               IAnthropicClient, AnthropicClient (raw SSE),
        │   │                      PromptBuilder
        │   ├── Cache/             AnalysisCache (memory + filesystem)
        │   ├── KnowledgeBase/     KnowledgeBaseService
        │   ├── Sessions/          SessionStore, SessionSummary
        │   └── Generation/        CSharpScaffoldGenerator (Roslyn-validated)
        ├── Models/
        │   ├── CobolFile.cs       Source file metadata
        │   ├── CobolChunk.cs      Paragraph-boundary slice
        │   ├── ExtractedRule.cs   Rule with type, confidence, decision
        │   ├── AnalysisSession.cs Full session: files, chunks, rules, decisions
        │   ├── KnowledgeEntry.cs  Persisted high-confidence rule
        │   └── GenerationResult.cs Output of scaffold generation
        └── Components/
            ├── Pages/
            │   ├── Analyst.razor  Main workflow: upload → analyse → review → query
            │   ├── Sessions.razor Saved session browser
            │   └── Generate.razor C# scaffold viewer with copy-to-clipboard
            └── Layout/
                └── MainLayout.razor  Top nav, dark theme layout
```

---

## Limitations and Future Work

**What it does not do today:**

- **Full COBOL grammar parsing.** The chunker uses paragraph-boundary regex rather than a proper AST. Division headers like `PROCEDURE DIVISION` are split correctly; however, nested `SECTION` declarations within the `PROCEDURE DIVISION` are treated as paragraph boundaries, which may group unrelated paragraphs together in large programs.

- **Copybook resolution.** Programs that `COPY` external copybooks will have those references appear as unexpanded tokens. The chunker will still process the main file, but any logic in the copybook will not be analysed unless the copybook file is uploaded separately.

- **Data dictionary cross-referencing.** The tool extracts rules from code text but does not build a symbol table linking `WS-GROSS-PAY` references across paragraphs. The LLM is told the field names in context but cannot trace data flow between chunks.

- **Dialect support.** The regex chunker is calibrated for standard ANSI COBOL 85. IBM Enterprise COBOL extensions (`EXEC CICS`, `EXEC SQL`, `SERVICE RELOAD`) and Micro Focus extensions are not specifically handled; such lines will appear as source text and may confuse the LLM.

- **Multi-user or team use.** Session files and the knowledge base live on the local filesystem with no locking or conflict resolution. The tool is intended for a single analyst session.

**Plausible next steps:**

- Integrate a COBOL grammar (ANTLR4 has a public COBOL grammar) to replace regex chunking with proper parse-tree traversal and enable data-flow analysis.
- Add copybook resolution by allowing the user to specify a copybook search path.
- Export rules to Markdown or CSV for stakeholder review.
- Embed rule descriptions as vector embeddings for semantic search instead of keyword Jaccard overlap.
- Support batch analysis of entire mainframe program libraries via a CLI mode alongside the Blazor UI.
