using System.Data;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>
/// The SQL tools' cells and results (2026-09-23), driven through <see cref="DataTable.CreateDataReader"/> — the
/// same <see cref="System.Data.Common.DbDataReader"/> surface SqlClient's reader has — so no server is needed.
/// </summary>
public sealed class SqlTextTests
{
    private static async Task<SqlGrid> Read(DataTable table, int maxRows)
    {
        using var reader = table.CreateDataReader();
        return await SqlGrid.ReadAsync(reader, maxRows, CancellationToken.None);
    }

    private static DataTable Numbers(int count)
    {
        var table = new DataTable();
        table.Columns.Add("n", typeof(int));
        table.Columns.Add("name", typeof(string));
        for (int i = 1; i <= count; i++)
        {
            table.Rows.Add(i, "row " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return table;
    }

    private static SqlRun Run(params SqlGrid[] grids) => new(SqlOutcome.Ok, "", "aw", "AdventureWorks2022", grids, TimeSpan.FromMilliseconds(12.4));

    [Fact]
    public void Cells_AreInvariant_Exact_AndClipped()
    {
        Assert.Equal("NULL", SqlText.Cell(null));
        Assert.Equal("NULL", SqlText.Cell(DBNull.Value));
        Assert.Equal("12345678901234567890.123456789", SqlText.Cell(12345678901234567890.123456789m));   // every digit, never a float
        Assert.Equal("0.1", SqlText.Cell(0.1d));
        Assert.Equal("2026-10-04", SqlText.Cell(new DateOnly(2026, 10, 4)));   // Npgsql's date (2026-10-04)
        Assert.Equal("10:15:00.123", SqlText.Cell(new TimeOnly(10, 15, 0, 123)));
        Assert.Equal("2009-01-07", SqlText.Cell(new DateTime(2009, 1, 7)));
        Assert.Equal("2009-01-07 13:05:09.5", SqlText.Cell(new DateTime(2009, 1, 7, 13, 5, 9, 500)));
        Assert.Equal("2009-01-07 13:05:09 -05:00", SqlText.Cell(new DateTimeOffset(2009, 1, 7, 13, 5, 9, TimeSpan.FromHours(-5))));
        Assert.Equal("01:02:03", SqlText.Cell(new TimeSpan(1, 2, 3)));
        Assert.Equal("true", SqlText.Cell(true));
        Assert.Equal("0xDEADBEEF", SqlText.Cell(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
        Assert.Equal("0x" + new string('A', 64) + "… (40 bytes)", SqlText.Cell(Enumerable.Repeat((byte)0xAA, 40).ToArray()));
        Assert.Equal("6f9619ff-8b86-d011-b42d-00c04fc964ff", SqlText.Cell(new Guid("6F9619FF-8B86-D011-B42D-00C04FC964FF")));
        Assert.Equal(new string('x', SqlText.MaxCellChars) + "… (500 chars)", SqlText.Cell(new string('x', 500)));
        Assert.Equal("(geography: select it with .ToString())", SqlText.Unreadable("geography"));
    }

    [Fact]
    public void ATableCell_EscapesPipes_AndShowsLineBreaks()
    {
        Assert.Equal("a\\|b\\nc\\nd", SqlText.TableCell("a|b\r\nc\nd"));
        Assert.Equal("C:\\\\x", SqlText.TableCell("C:\\x"));
    }

    [Fact]
    public async Task TheReader_StopsAtTheCap_AndKnowsMoreExist()
    {
        var capped = await Read(Numbers(5), 3);
        Assert.Equal(["n", "name"], capped.Columns);
        Assert.Equal(3, capped.Rows.Count);
        Assert.True(capped.More);
        Assert.Equal(["3", "row 3"], capped.Rows[2]);

        var whole = await Read(Numbers(3), 3);
        Assert.False(whole.More);
        Assert.Equal(3, whole.Rows.Count);

        var table = new DataTable();
        table.Columns.Add("x", typeof(string));
        table.Rows.Add(DBNull.Value);
        Assert.Equal(["NULL"], (await Read(table, 10)).Rows[0]);   // a column with no name is LiveSqlTests' (a DataTable names its own)
        Assert.Equal("(column 1)", SqlText.UnnamedColumn(0));
    }

    [Fact]
    public async Task AQuery_IsAHeader_ThenATable_TheCapsSaid()
    {
        var grid = await Read(Numbers(2), 100);
        Assert.Equal(
            "2 rows × 2 columns from aw/AdventureWorks2022 (12 ms)\n\n| n | name |\n|---|---|\n| 1 | row 1 |\n| 2 | row 2 |",
            SqlText.Query(Run(grid), 100, 32_000));

        var capped = await Read(Numbers(5), 3);
        Assert.StartsWith("3 rows+ × 2 columns from aw/AdventureWorks2022 (12 ms) — the first 3 shown, more exist (narrow it with WHERE or TOP, or raise max_rows)\n\n", SqlText.Query(Run(capped), 3, 32_000));

        string cut = SqlText.Query(Run(await Read(Numbers(50), 100)), 100, 200);
        Assert.Contains(" fit the text cap; select fewer columns or rows for the rest", SqlText.Note(cut));
        Assert.True(cut.Length < 400);

        var none = await Read(Numbers(0), 100);
        Assert.Equal("0 rows × 2 columns from aw/AdventureWorks2022 (12 ms)\n\n| n | name |\n|---|---|", SqlText.Query(Run(none), 100, 32_000));
        Assert.Equal("The query returned no result set (aw/AdventureWorks2022, 12 ms)", SqlText.Query(Run(), 100, 32_000));
    }

    [Fact]
    public void Listings_Describe_AndRelationships_ReadAsTheModelNeedsThem()
    {
        var tables = new SqlGrid(["schema", "name", "type", "rows"], [["Person", "Person", "table", "19972"], ["Sales", "vSalesPerson", "view", "NULL"]], false);
        Assert.Equal(
            "2 tables and views in aw/AdventureWorks2022\n\n| schema | name | type | rows |\n|---|---|---|---|\n| Person | Person | table | 19972 |\n| Sales | vSalesPerson | view | NULL |",
            SqlText.Listing("table or view", "tables and views", Run(tables), 32_000));
        Assert.Equal("No tables and views in aw/AdventureWorks2022", SqlText.Listing("table or view", "tables and views", Run(new SqlGrid(["schema"], [], false)), 32_000));

        // column, type, max_length, precision, scale, is_nullable, is_identity, is_computed, default, pk
        var columns = new SqlGrid(
            ["column", "type", "max_length", "precision", "scale", "is_nullable", "is_identity", "is_computed", "default", "pk"],
            [
                ["SalesOrderID", "int", "4", "10", "0", "false", "true", "false", "NULL", "1"],
                ["AccountNumber", "nvarchar", "30", "0", "0", "true", "false", "false", "NULL", "NULL"],
                ["SubTotal", "money", "8", "19", "4", "false", "false", "false", "((0.00))", "NULL"],
                ["Freight", "decimal", "9", "19", "4", "false", "false", "false", "NULL", "NULL"],
                ["Comment", "varchar", "-1", "0", "0", "true", "false", "false", "NULL", "NULL"],
                ["TotalDue", "money", "8", "19", "4", "false", "false", "true", "NULL", "NULL"],
            ],
            false);
        var keys = new SqlGrid(["direction", "constraint", "from_table", "from_column", "to_table", "to_column"], [["out", "FK_Customer", "Sales.SalesOrderHeader", "CustomerID", "Sales.Customer", "CustomerID"], ["in", "FK_Detail", "Sales.SalesOrderDetail", "SalesOrderID", "Sales.SalesOrderHeader", "SalesOrderID"]], false);
        var indexes = new SqlGrid(["index", "kind", "is_unique", "is_primary_key", "keys"], [["PK_SalesOrderHeader", "CLUSTERED", "true", "true", "SalesOrderID"], ["IX_Customer", "NONCLUSTERED", "false", "false", "CustomerID, OrderDate DESC"]], false);
        Assert.Equal(
            "Sales.SalesOrderHeader (table, 6 columns) in aw/AdventureWorks2022\n\n" +
            "| column | type | null | key | default |\n|---|---|---|---|---|\n" +
            "| SalesOrderID | int identity | no | PK |  |\n" +
            "| AccountNumber | nvarchar(15) | yes |  |  |\n" +
            "| SubTotal | money | no |  | ((0.00)) |\n" +
            "| Freight | decimal(19,4) | no |  |  |\n" +
            "| Comment | varchar(max) | yes |  |  |\n" +
            "| TotalDue | money computed | no |  |  |\n" +
            "\nForeign keys:\n- out: Sales.SalesOrderHeader.CustomerID -> Sales.Customer.CustomerID (FK_Customer)\n- in : Sales.SalesOrderDetail.SalesOrderID -> Sales.SalesOrderHeader.SalesOrderID (FK_Detail)\n" +
            "\nIndexes:\n- PK_SalesOrderHeader (primary key, clustered): SalesOrderID\n- IX_Customer (nonclustered): CustomerID, OrderDate DESC",
            SqlText.Describe("Sales", "SalesOrderHeader", "table", Run(columns, keys, indexes), 32_000));

        var pairs = new SqlGrid(["constraint", "from_table", "from_column", "to_table", "to_column"], [["FK_Customer", "Sales.SalesOrderHeader", "CustomerID", "Sales.Customer", "CustomerID"]], false);
        Assert.Equal("1 foreign-key column pair touching Sales.Customer in aw/AdventureWorks2022 (join on these):\n- Sales.SalesOrderHeader.CustomerID -> Sales.Customer.CustomerID (FK_Customer)", SqlText.Relationships("Sales.Customer", Run(pairs), 32_000));
        Assert.Equal("No foreign keys in aw/AdventureWorks2022", SqlText.Relationships(null, Run(new SqlGrid(["constraint"], [], false)), 32_000));
    }

    [Fact]
    public void Describe_AddsDescriptions_Constraints_AndTriggers_OnlyWhereThereAreAny()
    {
        // column, type, max_length, precision, scale, is_nullable, is_identity, is_computed, default, pk, description
        var columns = new SqlGrid(
            ["column", "type", "max_length", "precision", "scale", "is_nullable", "is_identity", "is_computed", "default", "pk", "description"],
            [
                ["Status", "tinyint", "1", "3", "0", "false", "false", "false", "((1))", "NULL", "Order current status.\n1 = In process"],
                ["Comment", "nvarchar", "256", "0", "0", "true", "false", "false", "NULL", "NULL", "NULL"],
            ],
            false);
        var none = new SqlGrid(["x"], [], false);
        var indexes = new SqlGrid(["index", "kind", "is_unique", "is_primary_key", "keys", "is_unique_constraint"], [["UQ_x", "NONCLUSTERED", "true", "false", "rowguid", "true"]], false);
        var described = new SqlGrid(["description"], [["General sales order information."]], false);
        var checks = new SqlGrid(["constraint", "definition", "is_disabled"], [["CK_Status", "([Status]>=(0) AND [Status]<=(8))", "false"], ["CK_Off", "([x]>(0))", "true"]], false);
        var triggers = new SqlGrid(["trigger", "is_instead_of_trigger", "is_disabled", "events"], [["uHeader", "false", "false", "UPDATE"], ["iHeader", "true", "true", "INSERT, DELETE"]], false);

        Assert.Equal(
            "Sales.SalesOrderHeader (table, 2 columns) in aw/AdventureWorks2022\nGeneral sales order information.\n\n" +
            "| column | type | null | key | default | description |\n|---|---|---|---|---|---|\n" +
            "| Status | tinyint | no |  | ((1)) | Order current status.\\n1 = In process |\n" +
            "| Comment | nvarchar(128) | yes |  |  |  |\n" +
            "\nIndexes:\n- UQ_x (unique constraint, nonclustered): rowguid\n" +
            "\nCheck constraints:\n- CK_Status: ([Status]>=(0) AND [Status]<=(8))\n- CK_Off: ([x]>(0)) (disabled)\n" +
            "\nTriggers:\n- uHeader (after UPDATE)\n- iHeader (instead of INSERT, DELETE, disabled)",
            SqlText.Describe("Sales", "SalesOrderHeader", "table", Run(columns, none, indexes, described, checks, triggers), 32_000));

        // No description anywhere: the header stands alone and the column table keeps its five columns.
        var plain = new SqlGrid(columns.Columns, [["Comment", "nvarchar", "256", "0", "0", "true", "false", "false", "NULL", "NULL", "NULL"]], false);
        Assert.Equal(
            "dbo.t (table, 1 column) in aw/AdventureWorks2022\n\n| column | type | null | key | default |\n|---|---|---|---|---|\n| Comment | nvarchar(128) | yes |  |  |",
            SqlText.Describe("dbo", "t", "table", Run(plain, none, none, none, none, none), 32_000));
    }

    [Fact]
    public void Indexes_ShowKindKeysAndSize_UsageWhenRead_AndTheMissingSuggestions()
    {
        // table, index, kind, is_unique, is_primary_key, is_unique_constraint, is_disabled, keys, included, filter, fill_factor, size_kb, object_id, index_id
        var indexes = new SqlGrid(
            ["table", "index", "kind", "is_unique", "is_primary_key", "is_unique_constraint", "is_disabled", "keys", "included", "filter", "fill_factor", "size_kb", "object_id", "index_id"],
            [
                ["Sales.Detail", "PK_Detail", "CLUSTERED", "true", "true", "false", "false", "SalesOrderID, LineID", "NULL", "NULL", "0", "20480", "7", "1"],
                ["Sales.Detail", "IX_Product", "NONCLUSTERED", "false", "false", "false", "false", "ProductID DESC", "Qty, Price", "([Qty]>(0))", "90", "2184", "7", "2"],
                ["Sales.Detail", "UQ_Guid", "NONCLUSTERED", "true", "false", "true", "true", "rowguid", "NULL", "NULL", "0", "NULL", "7", "3"],
            ],
            false);
        // object_id, index_id, user_seeks, user_scans, user_lookups, user_updates
        var usage = new SqlGrid(["object_id", "index_id", "user_seeks", "user_scans", "user_lookups", "user_updates"], [["7", "1", "12", "3", "0", "40"], ["7", "2", "0", "0", "0", "40"]], false);
        // table, equality_columns, inequality_columns, included_columns, uses, impact
        var missing = new SqlGrid(["table", "equality_columns", "inequality_columns", "included_columns", "uses", "impact"], [["Sales.Detail", "[CarrierTrackingNumber]", "NULL", "[OrderQty]", "1204", "87.5"]], false);
        var run = Run(indexes);

        Assert.Equal(
            "3 indexes on Sales.Detail in aw/AdventureWorks2022 (usage since the server started)\n\n" +
            "| table | index | kind | keys | included | filter | size | seeks | scans | lookups | updates |\n|---|---|---|---|---|---|---|---|---|---|---|\n" +
            "| Sales.Detail | PK_Detail | clustered PK | SalesOrderID, LineID |  |  | 20 MB | 12 | 3 | 0 | 40 |\n" +
            "| Sales.Detail | IX_Product | nonclustered | ProductID DESC | Qty, Price | ([Qty]>(0)) | 2184 KB | 0 | 0 | 0 | 40 " + SqlText.UnusedMarker + " |\n" +
            "| Sales.Detail | UQ_Guid | nonclustered unique constraint, disabled | rowguid |  |  |  | 0 | 0 | 0 | 0 " + SqlText.UnusedMarker + " |\n" +
            "\nMissing-index suggestions (the optimizer's hints since the server started, not a design):\n" +
            "- Sales.Detail ([CarrierTrackingNumber]) INCLUDE ([OrderQty]) — impact 87.5%, 1204 uses",
            SqlText.Indexes("on Sales.Detail", run, usage, null, missing, null, 32_000));

        // The login may not read the DMVs: the indexes stand, the two notes say why the rest is missing.
        string refused = SqlText.Indexes("", run, null, "Msg 300", null, "Msg 300", 32_000);
        Assert.StartsWith("3 indexes in aw/AdventureWorks2022\n" + SqlText.UsageUnavailable("Msg 300") + "\n\n| table | index | kind | keys | included | filter | size |\n", refused);
        Assert.EndsWith("\n" + SqlText.MissingUnavailable("Msg 300"), refused);
        Assert.Equal("0 indexes in schema Nope in aw/AdventureWorks2022 (usage since the server started)", SqlText.Indexes("in schema Nope", Run(new SqlGrid(indexes.Columns, [], false)), usage, null, null, null, 32_000));
        Assert.Contains("[… the first ", SqlText.Indexes("", run, usage, null, null, null, 400));
    }

    [Fact]
    public void Columns_AreOneDeclarationEach_AndDescriptionsBlankOrGone()
    {
        // table, kind, column, type, max_length, precision, scale, is_nullable, description
        var found = new SqlGrid(
            ["table", "kind", "column", "type", "max_length", "precision", "scale", "is_nullable", "description"],
            [
                ["Person.EmailAddress", "table", "EmailAddress", "nvarchar", "100", "0", "0", "true", "E-mail address for the person."],
                ["Sales.vIndividualCustomer", "view", "EmailAddress", "nvarchar", "100", "0", "0", "true", "NULL"],
            ],
            false);
        Assert.Equal(
            "2 columns in aw/AdventureWorks2022\n\n| table | kind | column | type | null | description |\n|---|---|---|---|---|---|\n" +
            "| Person.EmailAddress | table | EmailAddress | nvarchar(50) | yes | E-mail address for the person. |\n" +
            "| Sales.vIndividualCustomer | view | EmailAddress | nvarchar(50) | yes |  |",
            SqlText.Columns("EmailAddress", Run(found), 32_000));

        var undocumented = new SqlGrid(found.Columns, [["dbo.t", "table", "Email", "varchar", "-1", "0", "0", "false", "NULL"]], false);
        Assert.Equal("1 column in aw/AdventureWorks2022\n\n| table | kind | column | type | null |\n|---|---|---|---|---|\n| dbo.t | table | Email | varchar(max) | no |", SqlText.Columns("Email", Run(undocumented), 32_000));
        Assert.Equal("No columns in aw/AdventureWorks2022 named like 'Nope'", SqlText.Columns("Nope", Run(new SqlGrid(found.Columns, [], false)), 32_000));
    }

    [Fact]
    public void TypeNames_AreWrittenAsADeclarationWouldBe()
    {
        Assert.Equal("nvarchar(50)", SqlText.TypeName("nvarchar", "100", "0", "0"));
        Assert.Equal("nvarchar(max)", SqlText.TypeName("nvarchar", "-1", "0", "0"));
        Assert.Equal("char(3)", SqlText.TypeName("char", "3", "0", "0"));
        Assert.Equal("varbinary(max)", SqlText.TypeName("varbinary", "-1", "0", "0"));
        Assert.Equal("numeric(38,10)", SqlText.TypeName("numeric", "17", "38", "10"));
        Assert.Equal("datetime2(7)", SqlText.TypeName("datetime2", "8", "27", "7"));
        Assert.Equal("int", SqlText.TypeName("int", "4", "10", "0"));
        Assert.Equal("Name", SqlText.TypeName("Name", "100", "0", "0"));   // an alias type, bare
    }

    [Fact]
    public void Outcomes_ReadAsSentences()
    {
        Assert.Equal(SqlText.NoConnections, SqlText.Error(SqlRun.Refused(SqlOutcome.NoConnections, "")));
        Assert.Equal("Error: no SQL connection is named 'x'; the connections are aw, corp", SqlText.Error(SqlRun.Refused(SqlOutcome.UnknownConnection, "aw, corp", "x")));
        Assert.Equal("Error: could not connect to aw: Login failed", SqlText.Error(SqlRun.Refused(SqlOutcome.ConnectFailed, "Login failed", "aw")));
        Assert.Equal("Error: the query on aw ran past 30 s and was stopped; narrow it (WHERE, TOP, fewer joins)", SqlText.Error(new SqlRun(SqlOutcome.Timeout, "30", "aw", "db", [], TimeSpan.Zero)));
        Assert.Equal("Error: the server refused it (aw): Msg 208, line 1: Invalid object name 'x'.", SqlText.Error(new SqlRun(SqlOutcome.Failed, SqlText.ServerError(208, 1, "Invalid object name 'x'."), "aw", "db", [], TimeSpan.Zero)));
        Assert.Equal("the first line", SqlText.Note("the first line\nthe rest"));
    }
}
