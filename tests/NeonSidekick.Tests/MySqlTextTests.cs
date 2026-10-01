using NeonSidekick.MySql;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>The MySQL tools' formatting (2026-09-30), pure: the describe and index pages, the headers, the outcomes, the wizard's lines.</summary>
public sealed class MySqlTextTests
{
    private const string N = SqlText.Null;

    private static SqlRun Ok(params SqlGrid[] grids) => new(SqlOutcome.Ok, "", "shop", "shop", grids, TimeSpan.FromMilliseconds(7));

    [Fact]
    public void Describe_ShowsColumnsKeysIndexesChecksAndTriggers()
    {
        var columns = new SqlGrid(["column", "type", "nullable", "extra", "default", "pk", "description"],
        [
            ["order_id", "int", "NO", "auto_increment", N, "1", N],
            ["status", "enum('NEW','PAID')", "NO", "", "NEW", N, "Where the order is"],
            ["total_x2", "decimal(12,2)", "YES", "VIRTUAL GENERATED", N, N, N],
        ], false);
        var keys = new SqlGrid(["direction", "constraint", "from_table", "from_column", "to_table", "to_column"], [["out", "orders_customer_fk", "shop.orders", "customer_id", "shop.customers", "customer_id"]], false);
        var indexes = new SqlGrid(["index", "kind", "non_unique", "keys"], [["PRIMARY", "BTREE", "0", "order_id"], ["orders_customer_ix", "BTREE", "1", "customer_id, ordered_at DESC"]], false);
        var comment = new SqlGrid(["description"], [["One row per order"]], false);
        var checks = new SqlGrid(["constraint", "definition"], [["orders_total_ck", "(`total` >= 0)"]], false);
        var triggers = new SqlGrid(["trigger", "timing", "event"], [["orders_touch", "BEFORE", "UPDATE"]], false);

        string text = MySqlText.Describe("shop", "orders", "table", Ok(columns, keys, indexes, comment, checks, triggers), 10_000);

        Assert.StartsWith("shop.orders (table, 3 columns) in shop\nOne row per order\n", text);
        Assert.Contains("| order_id | int auto_increment | no | PK |  |  |\n", text);
        Assert.Contains("| status | enum('NEW','PAID') | no |  | NEW | Where the order is |\n", text);
        Assert.Contains("| total_x2 | decimal(12,2) generated | yes |  |  |  |\n", text);
        Assert.Contains("Foreign keys:\n- out: shop.orders.customer_id -> shop.customers.customer_id (orders_customer_fk)\n", text);
        Assert.Contains("Indexes:\n- PRIMARY (primary key, btree): order_id\n- orders_customer_ix (btree): customer_id, ordered_at DESC\n", text);
        Assert.Contains("Check constraints:\n- orders_total_ck: (`total` >= 0)\n", text);
        Assert.EndsWith("Triggers:\n- orders_touch (before update)", text);
    }

    [Fact]
    public void Indexes_ShowUsage_OrWhyItIsMissing()
    {
        string[] columns = ["table", "index", "kind", "non_unique", "keys", "cardinality", "database", "name"];
        var run = Ok(new SqlGrid(columns, [["shop.orders", "PRIMARY", "BTREE", "0", "order_id", "200", "shop", "orders"], ["shop.orders", "orders_customer_ix", "BTREE", "1", "customer_id", "40", "shop", "orders"], ["shop.orders", "idle_ix", "BTREE", "1", "status", "5", "shop", "orders"]], false));
        var usage = new SqlGrid(["database", "table", "index", "reads", "writes"], [["shop", "orders", "orders_customer_ix", "12", "3"], ["shop", "orders", "idle_ix", "0", "9"]], false);

        string with = MySqlText.Indexes("on shop.orders", run, usage, null, 10_000);
        Assert.StartsWith("3 indexes on shop.orders in shop (usage since the server started)\n", with);
        Assert.Contains("| shop.orders | PRIMARY | btree PK | order_id | 200 | 0 | 0 |", with);
        Assert.Contains("| shop.orders | orders_customer_ix | btree | customer_id | 40 | 12 | 3 |", with);
        Assert.Contains("| shop.orders | idle_ix | btree | status | 5 | 0 " + MySqlText.UnusedMarker + " | 9 |", with);
        Assert.Contains(MySqlText.UsageUnavailable("Error 1142: denied"), MySqlText.Indexes("", run, null, "Error 1142: denied", 10_000));
    }

    [Fact]
    public void Headers_AndOutcomes()
    {
        Assert.StartsWith("2 rows+ × 1 column from shop/shop (7 ms) — the first 2 shown, more exist (narrow it with WHERE or LIMIT, or raise max_rows)", MySqlText.Query(Ok(new SqlGrid(["x"], [["1"], ["2"]], true)), 2, 10_000));
        Assert.Equal("No tables and views in shop", MySqlText.Listing("table or view", "tables and views", Ok(new SqlGrid(["x"], [], false)) with { Database = "" }, 10_000));
        Assert.Equal("Error: the query on shop ran past 30 s and was stopped; narrow it (WHERE, LIMIT, fewer joins)", MySqlText.Error(new SqlRun(SqlOutcome.Timeout, "30", "shop", "", [], TimeSpan.Zero)));
        Assert.Equal("Error: the server refused it (shop): Error 1146: Table 'shop.x' doesn't exist", MySqlText.Error(new SqlRun(SqlOutcome.Failed, MySqlText.ServerError(1146, "Table 'shop.x' doesn't exist"), "shop", "", [], TimeSpan.Zero)));
        Assert.Equal("Connected to 'shop' as shop_reader@%, 8.4.11.", MySqlText.TestOk("shop", "shop_reader@%", "8.4.11"));
        Assert.StartsWith("This account can change data (ALL PRIVILEGES ON `neon`.*);", MySqlText.CanWrite(["ALL PRIVILEGES ON `neon`.*"]));
    }
}
