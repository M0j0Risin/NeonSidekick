using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace NeonSidekick.Bench;

/// <summary>
/// Nested JSON Extraction (2026-09-28, from LLMTester's <c>JsonSchemaTest</c>): three invoices out of a messy paragraph into
/// a nested schema sent as <c>response_format</c> (<c>json_schema</c>), each total computed. The root is an array, as
/// LLMTester has it: llama.cpp, vLLM and SGLang take one, OpenAI's strict mode would not. The judge parses the reply as it
/// came — a fenced answer fails, the server having ignored the schema — and checks each company's total and that it has
/// lines; the lines themselves are not checked.
/// </summary>
public sealed class NestedJsonTest : BenchTest
{
    internal const string MessyText =
        "Quarterly close — here are the open Q3 invoices:\n" +
        "INV-101 (AlphaCorp): 4 widgets @ $100, 2 gadgets @ $55.\n" +
        "INV-202 (Beta Dynamics): 3 modules @ $250.\n" +
        "INV-303 (Gamma LLC): 1 service @ $999, 2 widgets @ $100.\n" +
        "Send the totals back so I can cut the checks.";

    internal const string Prompt =
        "Extract every invoice mentioned in the text below. For each invoice report:\n" +
        "  - id: the invoice id (string)\n" +
        "  - company: the company name (string)\n" +
        "  - lines: an array of line items, each with item (string), qty (integer), unit_price (integer, whole dollars)\n" +
        "  - total: the integer total for the invoice, i.e. the sum of qty * unit_price across all its lines\n" +
        "You must answer by following the response_format JSON schema: a JSON array of invoice objects, " +
        "each with keys \"id\", \"company\", \"lines\", \"total\".\n" +
        "Do NOT include markdown formatting, a preamble, or explanation.\n" +
        "Your entire response must be the JSON array.\n" +
        "\n" +
        "Text to parse:\n" +
        MessyText;

    /// <summary>The schema's name on the wire.</summary>
    public const string SchemaName = "q3_invoice_batch";

    internal const string SchemaJson = """
        {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "id": { "type": "string" },
              "company": { "type": "string" },
              "lines": {
                "type": "array",
                "items": {
                  "type": "object",
                  "properties": {
                    "item": { "type": "string" },
                    "qty": { "type": "integer", "minimum": 0 },
                    "unit_price": { "type": "integer", "minimum": 0 }
                  },
                  "required": ["item", "qty", "unit_price"],
                  "additionalProperties": false
                }
              },
              "total": { "type": "integer", "minimum": 0 }
            },
            "required": ["id", "company", "lines", "total"],
            "additionalProperties": false
          }
        }
        """;

    private static readonly ChatResponseFormat Format =
        ChatResponseFormat.ForJsonSchema(JsonDocument.Parse(SchemaJson).RootElement.Clone(), SchemaName);

    private static readonly (string Company, int Total)[] ExpectedInvoices =
    [
        ("alphacorp", 510),
        ("beta dynamics", 750),
        ("gamma llc", 1199),
    ];

    public override string Id => "json";

    public override string Name => "Nested JSON Extraction (Structured Output)";

    public override BenchCategory Category => BenchCategory.StructuredOutput;

    public override string Expected => "a JSON array of 3 invoices: AlphaCorp 510, Beta Dynamics 750, Gamma LLC 1199, each with line items";

    public override string Description => "Requests a nested JSON schema via response_format; the answer must be an array of invoices with correct computed totals.";

    public override BenchRequest BuildRequest(BenchContext context) => new([User(Prompt)], Format);

    public override BenchJudgement Judge(string answer, BenchContext context)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(answer.Trim());
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return BenchJudgement.Fail("Response is not valid JSON; the server did not honor the requested schema.");
        }

        if (root.ValueKind is not JsonValueKind.Array)
        {
            return BenchJudgement.Fail("Response is JSON but not the expected array of invoices.");
        }

        var missing = new List<string>();
        foreach (var (company, expectedTotal) in ExpectedInvoices)
        {
            JsonElement? found = null;
            foreach (var element in root.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("company", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && string.Equals(name.GetString(), company, StringComparison.OrdinalIgnoreCase))
                {
                    found = element;
                    break;
                }
            }

            if (found is not { } invoice)
            {
                missing.Add("missing company " + company);
                continue;
            }

            if (!invoice.TryGetProperty("lines", out var lines) || lines.ValueKind is not JsonValueKind.Array || lines.GetArrayLength() == 0)
            {
                missing.Add(company + " (no line items)");
            }

            bool totalOk = invoice.TryGetProperty("total", out var total)
                && total.ValueKind == JsonValueKind.Number
                && total.TryGetInt32(out int value)
                && value == expectedTotal;
            if (!totalOk)
            {
                missing.Add(company + " total (expected " + expectedTotal.ToString(CultureInfo.InvariantCulture) + ")");
            }
        }

        return missing.Count == 0
            ? BenchJudgement.Pass("Schema-conforming JSON array with the right computed totals.")
            : BenchJudgement.Fail("Schema-conforming array but wrong extraction; " + string.Join(", ", missing));
    }
}

/// <summary>
/// State Tracking (2026-09-28, from LLMTester's <c>StateTrackingTest</c>): four inventory steps, the final state as a JSON
/// object under a <c>response_format</c> schema. The judge parses the reply as it came and wants 2 health potions, 1 mana
/// potion and 1 sword (the keys ignoring case; a missing one counts 0).
/// </summary>
public sealed class StateTrackingTest : BenchTest
{
    internal const string Prompt =
        "You have an empty inventory.\n" +
        "1. Acquire 5 health potions and 2 mana potions.\n" +
        "2. Drop 1 health potion.\n" +
        "3. Trade 2 health potions for 1 sword.\n" +
        "4. Drink 1 mana potion.\n" +
        "List your current inventory exactly as a JSON object with the keys " +
        "\"health potion\", \"mana potion\" and \"sword\" (omit any item whose count is 0).\n" +
        "You must answer by following the response_format JSON schema: a single JSON object with those keys.\n" +
        "Output ONLY the JSON object: no markdown fences, no preamble, no explanation.";

    /// <summary>The schema's name on the wire.</summary>
    public const string SchemaName = "inventory_state";

    internal const string SchemaJson = """
        {
          "type": "object",
          "properties": {
            "health potion": { "type": "integer", "minimum": 0 },
            "mana potion": { "type": "integer", "minimum": 0 },
            "sword": { "type": "integer", "minimum": 0 }
          },
          "required": ["health potion", "mana potion", "sword"],
          "additionalProperties": false
        }
        """;

    private static readonly ChatResponseFormat Format =
        ChatResponseFormat.ForJsonSchema(JsonDocument.Parse(SchemaJson).RootElement.Clone(), SchemaName);

    private static readonly (string Key, int Count)[] ExpectedCounts =
    [
        ("health potion", 2),
        ("mana potion", 1),
        ("sword", 1),
    ];

    public override string Id => "state";

    public override string Name => "State Tracking (Structured Output)";

    public override BenchCategory Category => BenchCategory.StructuredOutput;

    public override string Expected => """{"health potion": 2, "mana potion": 1, "sword": 1}""";

    public override string Description => "Four inventory operations; the model must report the final state as a JSON object under a schema.";

    public override BenchRequest BuildRequest(BenchContext context) => new([User(Prompt)], Format);

    public override BenchJudgement Judge(string answer, BenchContext context)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(answer.Trim());
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return BenchJudgement.Fail("Response is not valid JSON on its own (fenced output violates the constraint).");
        }

        if (root.ValueKind is not JsonValueKind.Object)
        {
            return BenchJudgement.Fail("Response is JSON but not an object.");
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (int.TryParse(property.Value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                counts[property.Name] = n;
            }
        }

        var wrong = new List<string>();
        foreach (var (key, expected) in ExpectedCounts)
        {
            int actual = counts.TryGetValue(key, out int n) ? n : 0;
            if (actual != expected)
            {
                wrong.Add(string.Create(CultureInfo.InvariantCulture, $"{key} = {actual} (expected {expected})"));
            }
        }

        return wrong.Count == 0
            ? BenchJudgement.Pass("All three final counts correct.")
            : BenchJudgement.Fail("Counts wrong: " + string.Join("; ", wrong));
    }
}
