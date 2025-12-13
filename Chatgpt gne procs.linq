<Query Kind="Program">
  <Connection>
    <ID>49598a36-613b-4feb-9d96-3edf2a6c4920</ID>
    <NamingServiceVersion>2</NamingServiceVersion>
    <Persist>true</Persist>
    <Server>localhost\sqlexpress</Server>
    <AllowDateOnlyTimeOnly>true</AllowDateOnlyTimeOnly>
    <Database>BlazorBlogDb</Database>
    <DriverData>
      <LegacyMFA>false</LegacyMFA>
    </DriverData>
  </Connection>
  <NuGetReference>Microsoft.Data.SqlClient</NuGetReference>
</Query>

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// ---- FIX: Explicit aliases to avoid CS0104 ambiguity ----
using SqlConnection = Microsoft.Data.SqlClient.SqlConnection;
using SqlCommand = Microsoft.Data.SqlClient.SqlCommand;
using SqlDataReader = Microsoft.Data.SqlClient.SqlDataReader;

// ─────────────────────────────────────────────
//  MAIN
// ─────────────────────────────────────────────
void Main()
{
	var connectionString = this.Connection.ConnectionString;

	var outputRoot = @"C:\dev\SqlCrudProcs";
	Directory.CreateDirectory(outputRoot);

	var options = new ProcGenOptions(); // defaults = usp_ + table schema

	using var cx = new SqlConnection(connectionString);
	cx.Open();

	var tables = LoadTables(cx);

	var all = new StringBuilder();
	all.AppendLine("-- Auto-generated CRUD + UPSERT stored procedures");
	all.AppendLine("-- Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
	all.AppendLine();

	foreach (var t in tables)
	{
		var cols = LoadColumns(cx, t.Schema, t.Name);
		var pk = LoadPrimaryKey(cx, t.Schema, t.Name);

		if (pk.Count == 0)
		{
			all.AppendLine($"-- SKIP [{t.Schema}].[{t.Name}] (no primary key)");
			all.AppendLine();
			continue;
		}

		var sql = GenerateCrudForTable(options, t.Schema, t.Name, cols, pk);

		File.WriteAllText(
			Path.Combine(outputRoot, $"{t.Schema}.{t.Name}.CrudProcs.sql"),
			sql,
			Encoding.UTF8);

		all.AppendLine(sql);
		all.AppendLine("GO");
		all.AppendLine();
	}

	File.WriteAllText(Path.Combine(outputRoot, "AllProcs.sql"), all.ToString(), Encoding.UTF8);

	$"Done. Output: {outputRoot}".Dump();
}

// ─────────────────────────────────────────────
//  OPTIONS
// ─────────────────────────────────────────────
sealed class ProcGenOptions
{
	public bool UseUspPrefix { get; init; } = true;
	public string? ProcSchemaOverride { get; init; } = null;
	public bool PluralizeProcNames { get; init; } = false;
}

// ─────────────────────────────────────────────
//  SCHEMA MODELS
// ─────────────────────────────────────────────
sealed record TableInfo(string Schema, string Name);

sealed record ColumnInfo(
	string Name,
	string SqlType,
	int? MaxLength,
	byte Precision,
	int Scale,
	bool IsNullable,
	bool IsIdentity,
	bool IsComputed,
	bool IsRowVersion,
	bool IsGeneratedAlways
);

// ─────────────────────────────────────────────
//  METADATA
// ─────────────────────────────────────────────
static List<TableInfo> LoadTables(SqlConnection cx)
{
	const string sql = """
	SELECT s.name, t.name
	FROM sys.tables t
	JOIN sys.schemas s ON s.schema_id = t.schema_id
	WHERE t.is_ms_shipped = 0
	ORDER BY s.name, t.name;
	""";

	using var cmd = new SqlCommand(sql, cx);
	using var r = cmd.ExecuteReader();

	var list = new List<TableInfo>();
	while (r.Read())
		list.Add(new TableInfo(r.GetString(0), r.GetString(1)));

	return list;
}

static List<ColumnInfo> LoadColumns(SqlConnection cx, string schema, string table)
{
	const string sql = """
	SELECT
		c.name,
		t.name,
		c.max_length,
		c.precision,
		c.scale,
		c.is_nullable,
		c.is_identity,
		c.is_computed,
		c.generated_always_type
	FROM sys.columns c
	JOIN sys.tables tb ON tb.object_id = c.object_id
	JOIN sys.schemas s ON s.schema_id = tb.schema_id
	JOIN sys.types t ON t.user_type_id = c.user_type_id
	WHERE s.name = @schema AND tb.name = @table
	ORDER BY c.column_id;
	""";

	using var cmd = new SqlCommand(sql, cx);
	cmd.Parameters.AddWithValue("@schema", schema);
	cmd.Parameters.AddWithValue("@table", table);

	using var r = cmd.ExecuteReader();

	var list = new List<ColumnInfo>();
	while (r.Read())
	{
		var type = r.GetString(1);
		list.Add(new ColumnInfo(
			r.GetString(0),
			type,
			r.IsDBNull(2) ? null : r.GetInt16(2),
			r.IsDBNull(3) ? (byte)0 : r.GetByte(3),
			r.IsDBNull(4) ? 0 : r.GetByte(4),
			r.GetBoolean(5),
			r.GetBoolean(6),
			r.GetBoolean(7),
			type.Equals("timestamp", StringComparison.OrdinalIgnoreCase),
			r.GetByte(8) != 0
		));
	}

	return list;
}

static List<string> LoadPrimaryKey(SqlConnection cx, string schema, string table)
{
	const string sql = """
	SELECT c.name
	FROM sys.key_constraints k
	JOIN sys.index_columns ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id
	JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
	JOIN sys.tables t ON t.object_id = k.parent_object_id
	JOIN sys.schemas s ON s.schema_id = t.schema_id
	WHERE k.type = 'PK'
	  AND s.name = @schema
	  AND t.name = @table
	ORDER BY ic.key_ordinal;
	""";

	using var cmd = new SqlCommand(sql, cx);
	cmd.Parameters.AddWithValue("@schema", schema);
	cmd.Parameters.AddWithValue("@table", table);

	using var r = cmd.ExecuteReader();

	var pk = new List<string>();
	while (r.Read())
		pk.Add(r.GetString(0));

	return pk;
}

// ─────────────────────────────────────────────
//  GENERATION
// ─────────────────────────────────────────────
static string GenerateCrudForTable(
	ProcGenOptions options,
	string tableSchema,
	string tableName,
	List<ColumnInfo> cols,
	List<string> pkCols)
{
	var fullTable = $"[{tableSchema}].[{tableName}]";
	var procSchema = options.ProcSchemaOverride ?? tableSchema;
	var procPrefix = options.UseUspPrefix ? "usp_" : "";
	var procBase = $"{procPrefix}{tableName}";

	var identity = cols.FirstOrDefault(c => c.IsIdentity);
	var rowver = cols.FirstOrDefault(c => c.IsRowVersion);

	var insertCols = cols.Where(c => !c.IsIdentity && !c.IsComputed && !c.IsRowVersion && !c.IsGeneratedAlways).ToList();
	var updateCols = cols.Where(c => !pkCols.Contains(c.Name) && !c.IsIdentity && !c.IsComputed && !c.IsRowVersion && !c.IsGeneratedAlways).ToList();
	var pkParams = pkCols.Select(n => cols.First(c => c.Name == n)).ToList();

	var sb = new StringBuilder();
	sb.AppendLine($"-- {fullTable}");
	sb.AppendLine();

	sb.AppendLine(GenInsert(procSchema, procBase, fullTable, insertCols, pkCols, identity));
	sb.AppendLine("GO");
	sb.AppendLine();

	sb.AppendLine(GenUpdate(procSchema, procBase, fullTable, pkParams, updateCols, rowver));
	sb.AppendLine("GO");
	sb.AppendLine();

	sb.AppendLine(GenDelete(procSchema, procBase, fullTable, pkParams, rowver));
	sb.AppendLine("GO");
	sb.AppendLine();

	sb.AppendLine(GenUpsert(procSchema, procBase, fullTable, pkParams, insertCols, updateCols, identity, rowver));
	sb.AppendLine();

	return sb.ToString();
}

// ─────────────────────────────────────────────
//  PROC BODIES
// ─────────────────────────────────────────────
static string GenInsert(string schema, string baseName, string table,
	List<ColumnInfo> insertCols, List<string> pkCols, ColumnInfo? identity)
{
	var proc = $"[{schema}].[{baseName}_Insert]";
	var parms = insertCols.Select(Param).ToList();

	bool retId = identity != null && pkCols.Count == 1 && pkCols[0] == identity.Name;
	if (retId) parms.Add($"@NewId {identity!.SqlType} OUTPUT");

	return $"""
CREATE OR ALTER PROCEDURE {proc}
	{string.Join(",\n\t", parms)}
AS
BEGIN
	SET NOCOUNT ON;
	SET XACT_ABORT ON;

	BEGIN TRY
		BEGIN TRAN;

		INSERT INTO {table} ({string.Join(", ", insertCols.Select(c => $"[{c.Name}]"))})
		VALUES ({string.Join(", ", insertCols.Select(c => $"@{c.Name}"))});

		{(retId ? $"SET @NewId = CONVERT({identity!.SqlType}, SCOPE_IDENTITY());" : "")}

		COMMIT;
	END TRY
	BEGIN CATCH
		IF @@TRANCOUNT > 0 ROLLBACK;
		THROW;
	END CATCH
END
""";
}

static string GenUpdate(string schema, string baseName, string table,
	List<ColumnInfo> pk, List<ColumnInfo> upd, ColumnInfo? rv)
{
	var proc = $"[{schema}].[{baseName}_Update]";
	var parms = pk.Select(Param).Concat(upd.Select(Param)).ToList();
	if (rv != null) parms.Insert(pk.Count, $"@RowVersion {rv.SqlType}");

	return $"""
CREATE OR ALTER PROCEDURE {proc}
	{string.Join(",\n\t", parms)}
AS
BEGIN
	SET NOCOUNT ON;
	SET XACT_ABORT ON;

	UPDATE t
	SET {string.Join(", ", upd.Select(c => $"[{c.Name}] = @{c.Name}"))}
	FROM {table} t
	WHERE {string.Join(" AND ", pk.Select(c => $"t.[{c.Name}] = @{c.Name}"))}
	{(rv != null ? $"AND t.[{rv.Name}] = @RowVersion" : "")};

	IF @@ROWCOUNT = 0
		THROW 50002, 'Update failed.', 1;
END
""";
}

static string GenDelete(string schema, string baseName, string table,
	List<ColumnInfo> pk, ColumnInfo? rv)
{
	var proc = $"[{schema}].[{baseName}_Delete]";
	var parms = pk.Select(Param).ToList();
	if (rv != null) parms.Add($"@RowVersion {rv.SqlType}");

	return $"""
CREATE OR ALTER PROCEDURE {proc}
	{string.Join(",\n\t", parms)}
AS
BEGIN
	SET NOCOUNT ON;

	DELETE t FROM {table} t
	WHERE {string.Join(" AND ", pk.Select(c => $"t.[{c.Name}] = @{c.Name}"))}
	{(rv != null ? $"AND t.[{rv.Name}] = @RowVersion" : "")};

	IF @@ROWCOUNT = 0
		THROW 50003, 'Delete failed.', 1;
END
""";
}

static string GenUpsert(string schema, string baseName, string table,
	List<ColumnInfo> pk, List<ColumnInfo> ins, List<ColumnInfo> upd,
	ColumnInfo? id, ColumnInfo? rv)
{
	var proc = $"[{schema}].[{baseName}_Upsert]";
	var parms = pk.Select(Param).Concat(ins.Concat(upd).DistinctBy(c => c.Name).Select(Param));

	return $"""
CREATE OR ALTER PROCEDURE {proc}
	{string.Join(",\n\t", parms)}
AS
BEGIN
	SET NOCOUNT ON;
	SET XACT_ABORT ON;

	BEGIN TRY
		BEGIN TRAN;

		UPDATE t
		SET {string.Join(", ", upd.Select(c => $"[{c.Name}] = @{c.Name}"))}
		FROM {table} t
		WHERE {string.Join(" AND ", pk.Select(c => $"t.[{c.Name}] = @{c.Name}"))};

		IF @@ROWCOUNT = 0
			INSERT INTO {table} ({string.Join(", ", ins.Select(c => $"[{c.Name}]"))})
			VALUES ({string.Join(", ", ins.Select(c => $"@{c.Name}"))});

		COMMIT;
	END TRY
	BEGIN CATCH
		IF @@TRANCOUNT > 0 ROLLBACK;
		THROW;
	END CATCH
END
""";
}

// ─────────────────────────────────────────────
//  HELPERS
// ─────────────────────────────────────────────
static string Param(ColumnInfo c) =>
	$"@{c.Name} {c.SqlType}{(c.IsNullable ? " = NULL" : "")}";
