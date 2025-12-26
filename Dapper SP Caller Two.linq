<Query Kind="Program">
  <Connection>
    <ID>87b6df13-2c94-4d97-bd8d-077b419ecd46</ID>
    <NamingServiceVersion>3</NamingServiceVersion>
    <Persist>true</Persist>
    <Server>localhost</Server>
    <AllowDateOnlyTimeOnly>true</AllowDateOnlyTimeOnly>
    <UseMicrosoftDataSqlClient>true</UseMicrosoftDataSqlClient>
    <EncryptTraffic>true</EncryptTraffic>
    <Database>NW</Database>
    <MapXmlToString>false</MapXmlToString>
    <DriverData>
      <SkipCertificateCheck>true</SkipCertificateCheck>
    </DriverData>
  </Connection>
  <NuGetReference>Dapper</NuGetReference>
  <NuGetReference>Microsoft.Data.SqlClient</NuGetReference>
  <Namespace>System.Globalization</Namespace>
</Query>

// LINQPad 9 / .NET 10
// Generates a Dapper StoredProcedureCaller class from existing SQL Server stored procedures.
// - Supports Execute + Query + "smart wrapper" for Unknown procs:
//     FooExecuteAsync(...)
//     FooQueryAsync<T>(...)
//     FooAsync<T>(..., bool query = true) -> routes to Query vs Execute
// - Handles OUTPUT params (returns a dictionary)
// - Generates request records when parameter count is high
// - Uses Microsoft.Data.SqlClient only (no ambiguous SqlConnection)
//
// NOTE: This script does NOT generate CRUD procs. It generates C# caller methods for *existing* procs.
//
// Requires NuGet refs (LINQPad): Dapper, Microsoft.Data.SqlClient
#nullable enable

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;

// ────────────────────────────────────────────────────────────────────────────────
// CONFIGURATION (change if you want)
// ────────────────────────────────────────────────────────────────────────────────
const bool SaveToDisk = true;                      // still outputs to Results either way
readonly string OutputDir = @"c:\dve\GenerateDapperCaller";
const int RequestRecordThreshold = 8;              // create Request records when param count >= this
const bool EmitConfigureAwaitFalse = true;
const string NamespaceName = "Generated.Dapper";
const string ClassName = "StoredProcedureCaller";

// Optional filters:
// - If null/empty => includes all non-system stored procs
// - If provided => only include schemas listed (case-insensitive)
string[]? IncludeSchemas = null; // e.g. new[] { "dbo", "api" };

// Optional include regex: match against "schema.procname"
// If null => include all
Regex? IncludeProcRegex = null; // e.g. new Regex(@"(?i)^(dbo|api)\.usp_", RegexOptions.Compiled);

// Optional exclude regex: match against "schema.procname"
Regex? ExcludeProcRegex = null; // e.g. new Regex(@"(?i)\.usp_Internal_", RegexOptions.Compiled);

// ────────────────────────────────────────────────────────────────────────────────
// MAIN
// ────────────────────────────────────────────────────────────────────────────────
void Main()
{
	var connectionString = this.Connection.ConnectionString;

	var procs = LoadProcedures(connectionString);

	// Apply filters
	procs = procs
		.Where(p => IncludeSchemas is null || IncludeSchemas.Length == 0 || IncludeSchemas.Contains(p.Schema, StringComparer.OrdinalIgnoreCase))
		.Where(p => IncludeProcRegex is null || IncludeProcRegex.IsMatch($"{p.Schema}.{p.Name}"))
		.Where(p => ExcludeProcRegex is null || !ExcludeProcRegex.IsMatch($"{p.Schema}.{p.Name}"))
		.OrderBy(p => p.Schema, StringComparer.OrdinalIgnoreCase)
		.ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
		.ToList();

	var code = GenerateCallerFile(procs);

	// Output to Results
	code.Dump("Generated C#");

	// Save to disk
	if (SaveToDisk)
	{
		Directory.CreateDirectory(OutputDir);
		var path = Path.Combine(OutputDir, $"{ClassName}.g.cs");
		File.WriteAllText(path, code, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		path.Dump("Wrote file");
	}
}

// ────────────────────────────────────────────────────────────────────────────────
// MODEL
// ────────────────────────────────────────────────────────────────────────────────
sealed record ProcInfo(
	string Schema,
	string Name,
	int ObjectId,
	List<ProcParam> Params,
	ProcResultKind ResultKind
);

sealed record ProcParam(
	string Name,               // no @
	string SqlTypeName,         // e.g. nvarchar, int, uniqueidentifier
	bool IsNullable,
	bool IsOutput,
	int? MaxLength,             // bytes for (n)varchar; -1 for max
	byte? Precision,
	byte? Scale
);

enum ProcResultKind
{
	None,       // no rowset
	Rowset,     // rowset is described
	Unknown     // could not determine / dynamic SQL / errors
}

// ────────────────────────────────────────────────────────────────────────────────
// DATA LOAD (SQL Server metadata)
// ────────────────────────────────────────────────────────────────────────────────
static List<ProcInfo> LoadProcedures(string connectionString)
{
	using var conn = new SqlConnection(connectionString);
	conn.Open();

	// 1) procedures
	var procRows = conn.Query<ProcRow>(@"
SELECT
	s.name  AS [Schema],
	p.name  AS [Name],
	p.object_id AS [ObjectId]
FROM sys.procedures p
JOIN sys.schemas s ON s.schema_id = p.schema_id
WHERE
	p.is_ms_shipped = 0
	AND s.name NOT IN ('sys')
ORDER BY s.name, p.name;
").AsList();

	// 2) parameters
	var paramRows = conn.Query<ParamRow>(@"
SELECT
	p.object_id AS [ObjectId],
	prm.parameter_id AS [ParameterId],
	REPLACE(prm.name, '@', '') AS [Name],
	t.name AS [SqlTypeName],
	prm.max_length AS [MaxLength],
	prm.precision AS [Precision],
	prm.scale AS [Scale],
	prm.is_output AS [IsOutput],
	prm.has_default_value AS [HasDefault],
	prm.is_nullable AS [IsNullable]
FROM sys.procedures p
JOIN sys.parameters prm ON prm.object_id = p.object_id
JOIN sys.types t ON t.user_type_id = prm.user_type_id
WHERE p.is_ms_shipped = 0
ORDER BY p.object_id, prm.parameter_id;
").AsList();

	// 3) result set detection per proc using dm_exec_describe_first_result_set_for_object
	//    If it returns rows => Rowset
	//    If returns 0 rows and error_number is null => None
	//    If error_number not null => Unknown
	var resultInfo = conn.Query<ResultRow>(@"
SELECT
	p.object_id AS [ObjectId],
	rs.error_number AS [ErrorNumber],
	CASE WHEN EXISTS (
		SELECT 1
		FROM sys.dm_exec_describe_first_result_set_for_object(p.object_id, 0) x
		WHERE x.name IS NOT NULL
	) THEN 1 ELSE 0 END AS [HasColumns]
FROM sys.procedures p
OUTER APPLY (
	SELECT TOP (1) error_number
	FROM sys.dm_exec_describe_first_result_set_for_object(p.object_id, 0)
	WHERE error_number IS NOT NULL
) rs
WHERE p.is_ms_shipped = 0;
").AsList()
.ToDictionary(x => x.ObjectId);

	var procs = new List<ProcInfo>(procRows.Count);

	foreach (var pr in procRows)
	{
		var parms = paramRows
			.Where(x => x.ObjectId == pr.ObjectId)
			.OrderBy(x => x.ParameterId)
			.Select(x => new ProcParam(
				Name: x.Name,
				SqlTypeName: x.SqlTypeName,
				IsNullable: x.IsNullable,
				IsOutput: x.IsOutput,
				MaxLength: x.MaxLength,
				Precision: x.Precision,
				Scale: x.Scale
			))
			.ToList();

		var kind = ProcResultKind.Unknown;
		if (resultInfo.TryGetValue(pr.ObjectId, out var ri))
		{
			if (ri.ErrorNumber is not null) kind = ProcResultKind.Unknown;
			else if (ri.HasColumns == 1) kind = ProcResultKind.Rowset;
			else kind = ProcResultKind.None;
		}

		procs.Add(new ProcInfo(pr.Schema, pr.Name, pr.ObjectId, parms, kind));
	}

	return procs;
}

sealed class ProcRow { public string Schema { get; set; } = ""; public string Name { get; set; } = ""; public int ObjectId { get; set; } }
sealed class ParamRow
{
	public int ObjectId { get; set; }
	public int ParameterId { get; set; }
	public string Name { get; set; } = "";
	public string SqlTypeName { get; set; } = "";
	public short MaxLength { get; set; }
	public byte Precision { get; set; }
	public byte Scale { get; set; }
	public bool IsOutput { get; set; }
	public bool HasDefault { get; set; }
	public bool IsNullable { get; set; }
}
sealed class ResultRow { public int ObjectId { get; set; } public int? ErrorNumber { get; set; } public int HasColumns { get; set; } }

// ────────────────────────────────────────────────────────────────────────────────
// CODEGEN
// ────────────────────────────────────────────────────────────────────────────────
static string GenerateCallerFile(IReadOnlyList<ProcInfo> procs)
{
	var sb = new StringBuilder();
	sb.AppendLine("// <auto-generated />");
	sb.AppendLine("#nullable enable");
	sb.AppendLine("using System;");
	sb.AppendLine("using System.Collections.Generic;");
	sb.AppendLine("using System.Data;");
	sb.AppendLine("using System.Globalization;");
	sb.AppendLine("using System.Linq;");
	sb.AppendLine("using System.Threading;");
	sb.AppendLine("using System.Threading.Tasks;");
	sb.AppendLine("using Dapper;");
	sb.AppendLine("using Microsoft.Data.SqlClient;");
	sb.AppendLine();
	sb.AppendLine($"namespace {NamespaceName};");
	sb.AppendLine();
	sb.AppendLine("public interface IDbConnectionFactory");
	sb.AppendLine("{");
	sb.AppendLine("\tIDbConnection Create();");
	sb.AppendLine("}");
	sb.AppendLine();
	sb.AppendLine("public sealed class SqlConnectionFactory : IDbConnectionFactory");
	sb.AppendLine("{");
	sb.AppendLine("\tprivate readonly string _connectionString;");
	sb.AppendLine("\tpublic SqlConnectionFactory(string connectionString) => _connectionString = connectionString;");
	sb.AppendLine("\tpublic IDbConnection Create() => new SqlConnection(_connectionString);");
	sb.AppendLine("}");
	sb.AppendLine();
	sb.AppendLine($"public sealed class {ClassName}");
	sb.AppendLine("{");
	sb.AppendLine("\tprivate readonly IDbConnectionFactory _connectionFactory;");
	sb.AppendLine();
	sb.AppendLine($"\tpublic {ClassName}(IDbConnectionFactory connectionFactory)");
	sb.AppendLine("\t{");
	sb.AppendLine("\t\t_connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));");
	sb.AppendLine("\t}");
	sb.AppendLine();
	sb.AppendLine("\tprivate static string QuoteProc(string schema, string name) => $\"[{schema}].[{name}]\";");
	sb.AppendLine();
	sb.AppendLine("\tpublic sealed record ProcCallResult<T>(");
	sb.AppendLine("\t\tint RowsAffected,");
	sb.AppendLine("\t\tIReadOnlyList<T> Rows,");
	sb.AppendLine("\t\tIReadOnlyDictionary<string, object?> Output");
	sb.AppendLine("\t);");
	sb.AppendLine();

	// Request records (generated when parameter count is high)
	var requestRecords = new StringBuilder();
	foreach (var p in procs)
	{
		if (p.Params.Count >= RequestRecordThreshold)
		{
			var baseName = BuildMethodBaseName(p.Schema, p.Name);
			var recordName = $"{baseName}Request";
			requestRecords.AppendLine($"\tpublic sealed record {recordName}({BuildRecordCtorParams(p.Params)});");
			requestRecords.AppendLine();
		}
	}

	if (requestRecords.Length > 0)
	{
		sb.AppendLine("\t// Request records (generated when parameter count is high)");
		sb.Append(requestRecords.ToString());
	}

	// Methods
	foreach (var p in procs)
	{
		sb.Append(GenerateMethodsForProc(p));
	}

	sb.AppendLine("}");
	return sb.ToString();
}

static string GenerateMethodsForProc(ProcInfo proc)
{
	var sb = new StringBuilder();

	var schema = proc.Schema;
	var procName = proc.Name;
	var baseName = BuildMethodBaseName(schema, procName);
	var parameters = proc.Params;
	var kind = proc.ResultKind;

	bool hasOutput = parameters.Any(p => p.IsOutput);
	bool needsRequestRecord = parameters.Count >= RequestRecordThreshold;
	var requestRecordName = $"{baseName}Request";

	string procLiteralSchema = EscapeString(schema);
	string procLiteralName = EscapeString(procName);

	// EXECUTE signature
	// - if output => returns (rowsAffected, outputDict)
	// - else => returns rowsAffected
	if (hasOutput)
	{
		sb.AppendLine("\t/// <summary>");
		sb.AppendLine($"\t/// Calls {schema}.{procName}");
		sb.AppendLine("\t/// </summary>");
		sb.AppendLine($"\tpublic async Task<(int RowsAffected, IReadOnlyDictionary<string, object?> Output)> {baseName}ExecuteAsync({BuildMethodParams(parameters)}CancellationToken cancellationToken = default)");
		sb.AppendLine("\t{");
		sb.AppendLine($"\t\tvar procName = QuoteProc(\"{procLiteralSchema}\", \"{procLiteralName}\");");
		sb.AppendLine("\t\tusing var conn = _connectionFactory.Create();");
		sb.AppendLine("\t\tif (conn.State != ConnectionState.Open) conn.Open();");
		sb.AppendLine();
		sb.AppendLine("\t\tvar dp = new DynamicParameters();");
		foreach (var prm in parameters)
			sb.AppendLine(BuildDpAdd(prm));
		sb.AppendLine();
		sb.AppendLine("\t\tvar command = new CommandDefinition(procName, dp, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken);");
		sb.AppendLine($"\t\tvar rowsAffected = await conn.ExecuteAsync(command){(EmitConfigureAwaitFalse ? ".ConfigureAwait(false)" : "")};");
		sb.AppendLine();
		sb.AppendLine("\t\tvar output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);");
		foreach (var op in parameters.Where(p => p.IsOutput))
			sb.AppendLine($"\t\toutput[\"{ToPascalIdentifier(op.Name)}\"] = dp.Get<object?>(\"@{op.Name}\");");
		sb.AppendLine("\t\treturn (rowsAffected, output);");
		sb.AppendLine("\t}");
		sb.AppendLine();
	}
	else
	{
		sb.AppendLine("\t/// <summary>");
		sb.AppendLine($"\t/// Calls {schema}.{procName}");
		sb.AppendLine("\t/// </summary>");
		sb.AppendLine($"\tpublic async Task<int> {baseName}ExecuteAsync({BuildMethodParams(parameters)}CancellationToken cancellationToken = default)");
		sb.AppendLine("\t{");
		sb.AppendLine($"\t\tvar procName = QuoteProc(\"{procLiteralSchema}\", \"{procLiteralName}\");");
		sb.AppendLine("\t\tusing var conn = _connectionFactory.Create();");
		sb.AppendLine("\t\tif (conn.State != ConnectionState.Open) conn.Open();");
		sb.AppendLine();
		sb.AppendLine("\t\tvar dp = new DynamicParameters();");
		foreach (var prm in parameters)
			sb.AppendLine(BuildDpAdd(prm));
		sb.AppendLine();
		sb.AppendLine("\t\tvar command = new CommandDefinition(procName, dp, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken);");
		sb.AppendLine($"\t\tvar rowsAffected = await conn.ExecuteAsync(command){(EmitConfigureAwaitFalse ? ".ConfigureAwait(false)" : "")};");
		sb.AppendLine("\t\treturn rowsAffected;");
		sb.AppendLine("\t}");
		sb.AppendLine();
	}

	// QUERY signature (only meaningful if Rowset or Unknown; still generated for Unknown)
	if (kind == ProcResultKind.Rowset || kind == ProcResultKind.Unknown)
	{
		sb.AppendLine("\t/// <summary>");
		sb.AppendLine($"\t/// Calls {schema}.{procName} and returns a rowset");
		sb.AppendLine("\t/// </summary>");
		sb.AppendLine($"\tpublic async Task<IReadOnlyList<T>> {baseName}QueryAsync<T>({BuildMethodParams(parameters)}CancellationToken cancellationToken = default)");
		sb.AppendLine("\t{");
		sb.AppendLine($"\t\tvar procName = QuoteProc(\"{procLiteralSchema}\", \"{procLiteralName}\");");
		sb.AppendLine("\t\tusing var conn = _connectionFactory.Create();");
		sb.AppendLine("\t\tif (conn.State != ConnectionState.Open) conn.Open();");
		sb.AppendLine();
		sb.AppendLine("\t\tvar dp = new DynamicParameters();");
		foreach (var prm in parameters)
			sb.AppendLine(BuildDpAdd(prm));
		sb.AppendLine();
		sb.AppendLine("\t\tvar command = new CommandDefinition(procName, dp, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken);");
		sb.AppendLine($"\t\tvar rows = (await conn.QueryAsync<T>(command){(EmitConfigureAwaitFalse ? ".ConfigureAwait(false)" : "")}).AsList();");
		sb.AppendLine("\t\treturn rows;");
		sb.AppendLine("\t}");
		sb.AppendLine();
	}

	// UNKNOWN smart wrapper (routes to Query vs Execute)
	if (kind == ProcResultKind.Unknown)
	{
		sb.AppendLine("\t/// <summary>");
		sb.AppendLine($"\t/// Calls {schema}.{procName}. Result shape is unknown; choose query vs execute.");
		sb.AppendLine("\t/// </summary>");
		sb.AppendLine($"\tpublic async Task<ProcCallResult<T>> {baseName}Async<T>({BuildMethodParams(parameters)}bool query = true, CancellationToken cancellationToken = default)");
		sb.AppendLine("\t{");
		sb.AppendLine("\t\tif (query)");
		sb.AppendLine("\t\t{");
		var qArgs = string.Join(", ", parameters.Select(p => ToCamelIdentifier(p.Name)));
		sb.AppendLine($"\t\t\tvar rows = await {baseName}QueryAsync<T>({qArgs}{(qArgs.Length > 0 ? ", " : "")}cancellationToken){(EmitConfigureAwaitFalse ? ".ConfigureAwait(false)" : "")};");
		sb.AppendLine("\t\t\treturn new ProcCallResult<T>(RowsAffected: 0, Rows: rows, Output: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));");
		sb.AppendLine("\t\t}");
		sb.AppendLine("\t\telse");
		sb.AppendLine("\t\t{");
		if (hasOutput)
		{
			sb.AppendLine($"\t\t\tvar (rowsAffected, output) = await {baseName}ExecuteAsync({qArgs}{(qArgs.Length > 0 ? ", " : "")}cancellationToken){(EmitConfigureAwaitFalse ? ".ConfigureAwait(false)" : "")};");
			sb.AppendLine("\t\t\treturn new ProcCallResult<T>(RowsAffected: rowsAffected, Rows: Array.Empty<T>(), Output: output);");
		}
		else
		{
			sb.AppendLine($"\t\t\tvar rowsAffected = await {baseName}ExecuteAsync({qArgs}{(qArgs.Length > 0 ? ", " : "")}cancellationToken){(EmitConfigureAwaitFalse ? ".ConfigureAwait(false)" : "")};");
			sb.AppendLine("\t\t\treturn new ProcCallResult<T>(RowsAffected: rowsAffected, Rows: Array.Empty<T>(), Output: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));");
		}
		sb.AppendLine("\t\t}");
		sb.AppendLine("\t}");
		sb.AppendLine();

		if (needsRequestRecord)
		{
			sb.AppendLine("\t/// <summary>");
			sb.AppendLine($"\t/// Calls {schema}.{procName} using a request record (composite parameters).");
			sb.AppendLine("\t/// </summary>");
			sb.AppendLine($"\tpublic Task<ProcCallResult<T>> {baseName}Async<T>({requestRecordName} request, bool query = true, CancellationToken cancellationToken = default)");
			sb.AppendLine("\t{");
			sb.AppendLine("\t\tif (request is null) throw new ArgumentNullException(nameof(request));");
			var args = string.Join(", ", parameters.Select(p => $"request.{ToPascalIdentifier(p.Name)}"));
			sb.AppendLine($"\t\treturn {baseName}Async<T>({args}{(args.Length > 0 ? ", " : "")}query, cancellationToken);");
			sb.AppendLine("\t}");
			sb.AppendLine();
		}
	}

	// Request record overloads for Execute/Query (optional; keeps parity with your sample)
	if (needsRequestRecord)
	{
		// Execute overload
		if (hasOutput)
		{
			sb.AppendLine("\t/// <summary>");
			sb.AppendLine($"\t/// Calls {schema}.{procName} using a request record (composite parameters).");
			sb.AppendLine("\t/// </summary>");
			sb.AppendLine($"\tpublic Task<(int RowsAffected, IReadOnlyDictionary<string, object?> Output)> {baseName}ExecuteAsync({requestRecordName} request, CancellationToken cancellationToken = default)");
			sb.AppendLine("\t{");
			sb.AppendLine("\t\tif (request is null) throw new ArgumentNullException(nameof(request));");
			var args = string.Join(", ", parameters.Select(p => $"request.{ToPascalIdentifier(p.Name)}"));
			sb.AppendLine($"\t\treturn {baseName}ExecuteAsync({args}{(args.Length > 0 ? ", " : "")}cancellationToken);");
			sb.AppendLine("\t}");
			sb.AppendLine();
		}
		else
		{
			sb.AppendLine("\t/// <summary>");
			sb.AppendLine($"\t/// Calls {schema}.{procName} using a request record (composite parameters).");
			sb.AppendLine("\t/// </summary>");
			sb.AppendLine($"\tpublic Task<int> {baseName}ExecuteAsync({requestRecordName} request, CancellationToken cancellationToken = default)");
			sb.AppendLine("\t{");
			sb.AppendLine("\t\tif (request is null) throw new ArgumentNullException(nameof(request));");
			var args = string.Join(", ", parameters.Select(p => $"request.{ToPascalIdentifier(p.Name)}"));
			sb.AppendLine($"\t\treturn {baseName}ExecuteAsync({args}{(args.Length > 0 ? ", " : "")}cancellationToken);");
			sb.AppendLine("\t}");
			sb.AppendLine();
		}

		// Query overload (only if query exists)
		if (kind == ProcResultKind.Rowset || kind == ProcResultKind.Unknown)
		{
			sb.AppendLine("\t/// <summary>");
			sb.AppendLine($"\t/// Calls {schema}.{procName} (rowset) using a request record (composite parameters).");
			sb.AppendLine("\t/// </summary>");
			sb.AppendLine($"\tpublic Task<IReadOnlyList<T>> {baseName}QueryAsync<T>({requestRecordName} request, CancellationToken cancellationToken = default)");
			sb.AppendLine("\t{");
			sb.AppendLine("\t\tif (request is null) throw new ArgumentNullException(nameof(request));");
			var args = string.Join(", ", parameters.Select(p => $"request.{ToPascalIdentifier(p.Name)}"));
			sb.AppendLine($"\t\treturn {baseName}QueryAsync<T>({args}{(args.Length > 0 ? ", " : "")}cancellationToken);");
			sb.AppendLine("\t}");
			sb.AppendLine();
		}
	}

	return sb.ToString();
}

// ────────────────────────────────────────────────────────────────────────────────
// PARAM / IDENT HELPERS
// ────────────────────────────────────────────────────────────────────────────────
static string BuildMethodBaseName(string schema, string procName)
{
	// Remove schema prefix and common prefixes; convert to PascalCase-ish.
	// Example: dbo.usp_Customers_Insert -> CustomersInsert
	var name = procName;

	// Strip leading "usp_" (common convention)
	if (name.StartsWith("usp_", StringComparison.OrdinalIgnoreCase))
		name = name.Substring(4);

	// Replace non-identifier chars with underscore
	name = Regex.Replace(name, @"[^\w]+", "_");

	// Split by underscore and Pascal-case
	var parts = name.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
	var pascal = string.Concat(parts.Select(ToPascalToken));

	return pascal.Length == 0 ? "Proc" : pascal;
}

static string BuildMethodParams(IReadOnlyList<ProcParam> parameters)
{
	if (parameters.Count == 0) return "";

	var parts = new List<string>(parameters.Count + 1);
	foreach (var p in parameters)
	{
		var csType = MapSqlToCSharpType(p.SqlTypeName, p.IsNullable);
		var varName = ToCamelIdentifier(p.Name);
		parts.Add($"{csType} {varName}");
	}
	return string.Join(", ", parts) + ", ";
}

static string BuildRecordCtorParams(IReadOnlyList<ProcParam> parameters)
{
	var parts = new List<string>(parameters.Count);
	foreach (var p in parameters)
	{
		var csType = MapSqlToCSharpType(p.SqlTypeName, p.IsNullable);
		var propName = ToPascalIdentifier(p.Name);
		parts.Add($"{csType} {propName}");
	}
	return string.Join(", ", parts);
}

static string BuildDpAdd(ProcParam p)
{
	var varName = ToCamelIdentifier(p.Name);
	var dbType = MapSqlToDbType(p.SqlTypeName);

	// Determine Direction
	var dir = p.IsOutput ? "ParameterDirection.InputOutput" : "ParameterDirection.Input";

	// Size (strings/binary)
	string sizePart = "";
	if (IsSizeRelevant(p.SqlTypeName) && p.MaxLength is not null)
	{
		// sys.parameters.max_length is bytes; for nvarchar/nchar divide by 2
		var maxLen = p.MaxLength.Value;
		if (maxLen == -1) sizePart = ", size: -1";
		else
		{
			if (IsUnicodeString(p.SqlTypeName)) maxLen = maxLen / 2;
			sizePart = $", size: {maxLen.ToString(CultureInfo.InvariantCulture)}";
		}
	}

	// Precision/Scale (decimal/numeric)
	string psPart = "";
	if (IsPrecisionScaleRelevant(p.SqlTypeName) && p.Precision is not null && p.Scale is not null)
	{
		// Dapper DynamicParameters supports precision/scale via DbType? Not directly.
		// We’ll omit explicit precision/scale (SQL Server enforces).
		// Keep hook if you later want to add IDbDataParameter customization.
		psPart = "";
	}

	return $"\t\t\tdp.Add(\"@{p.Name}\", {varName}, DbType.{dbType}, {dir}{sizePart});";
}

static bool IsUnicodeString(string sqlType) =>
	sqlType.Equals("nvarchar", StringComparison.OrdinalIgnoreCase)
	|| sqlType.Equals("nchar", StringComparison.OrdinalIgnoreCase)
	|| sqlType.Equals("ntext", StringComparison.OrdinalIgnoreCase);

static bool IsSizeRelevant(string sqlType) =>
	sqlType.Equals("varchar", StringComparison.OrdinalIgnoreCase)
	|| sqlType.Equals("nvarchar", StringComparison.OrdinalIgnoreCase)
	|| sqlType.Equals("char", StringComparison.OrdinalIgnoreCase)
	|| sqlType.Equals("nchar", StringComparison.OrdinalIgnoreCase)
	|| sqlType.Equals("varbinary", StringComparison.OrdinalIgnoreCase)
	|| sqlType.Equals("binary", StringComparison.OrdinalIgnoreCase);

static bool IsPrecisionScaleRelevant(string sqlType) =>
	sqlType.Equals("decimal", StringComparison.OrdinalIgnoreCase)
	|| sqlType.Equals("numeric", StringComparison.OrdinalIgnoreCase);

static string MapSqlToCSharpType(string sqlType, bool isNullable)
{
	// Basic mapping; tune as needed.
	// Reference: SQL Server types -> C# types typically used with Dapper
	var t = sqlType.ToLowerInvariant();
	string cs =
		t switch
		{
			"bit" => "bool",
			"tinyint" => "byte",
			"smallint" => "short",
			"int" => "int",
			"bigint" => "long",
			"real" => "float",
			"float" => "double",
			"money" => "decimal",
			"smallmoney" => "decimal",
			"decimal" => "decimal",
			"numeric" => "decimal",
			"date" => "DateTime",
			"datetime" => "DateTime",
			"datetime2" => "DateTime",
			"smalldatetime" => "DateTime",
			"datetimeoffset" => "DateTimeOffset",
			"time" => "TimeSpan",
			"uniqueidentifier" => "Guid",
			"varbinary" => "byte[]",
			"binary" => "byte[]",
			"image" => "byte[]",
			"rowversion" => "byte[]",
			"timestamp" => "byte[]",
			"xml" => "string",
			"sql_variant" => "object",
			// strings
			"varchar" => "string",
			"nvarchar" => "string",
			"char" => "string",
			"nchar" => "string",
			"text" => "string",
			"ntext" => "string",
			_ => "object"
		};

	// Nullable handling:
	// - reference types already nullable as string? etc. (we output string? when nullable)
	// - value types become nullable T?
	if (cs == "string" || cs == "object" || cs == "byte[]")
		return isNullable ? cs + "?" : cs;

	// value type
	return isNullable ? cs + "?" : cs;
}

static string MapSqlToDbType(string sqlType)
{
	var t = sqlType.ToLowerInvariant();
	return t switch
	{
		"bit" => "Boolean",
		"tinyint" => "Byte",
		"smallint" => "Int16",
		"int" => "Int32",
		"bigint" => "Int64",
		"real" => "Single",
		"float" => "Double",
		"money" => "Currency",
		"smallmoney" => "Currency",
		"decimal" => "Decimal",
		"numeric" => "Decimal",
		"date" => "Date",
		"datetime" => "DateTime",
		"datetime2" => "DateTime2",
		"smalldatetime" => "DateTime",
		"datetimeoffset" => "DateTimeOffset",
		"time" => "Time",
		"uniqueidentifier" => "Guid",
		"varbinary" => "Binary",
		"binary" => "Binary",
		"image" => "Binary",
		"xml" => "Xml",
		"sql_variant" => "Object",
		"varchar" => "String",
		"nvarchar" => "String",
		"char" => "StringFixedLength",
		"nchar" => "StringFixedLength",
		"text" => "String",
		"ntext" => "String",
		_ => "Object"
	};
}

static string ToPascalIdentifier(string name)
{
	// Remove leading @ if present; normalize
	name = name.Replace("@", "");
	name = Regex.Replace(name, @"[^\w]+", "_");
	var parts = name.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
	var pascal = string.Concat(parts.Select(ToPascalToken));
	return pascal.Length == 0 ? "Value" : pascal;
}

static string ToCamelIdentifier(string name)
{
	var p = ToPascalIdentifier(name);
	return p.Length switch
	{
		0 => p,
		1 => char.ToLowerInvariant(p[0]).ToString(),
		_ => char.ToLowerInvariant(p[0]) + p[1..]
	};
}

static string ToPascalToken(string s)
{
	if (string.IsNullOrWhiteSpace(s)) return "";
	s = s.Trim();
	if (s.Length == 1) return s.ToUpperInvariant();
	return char.ToUpperInvariant(s[0]) + s.Substring(1);
}

static string EscapeString(string s) =>
	s.Replace("\\", "\\\\").Replace("\"", "\\\"");

// ────────────────────────────────────────────────────────────────────────────────
// END
// ────────────────────────────────────────────────────────────────────────────────
