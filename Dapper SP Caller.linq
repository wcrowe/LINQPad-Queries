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

#nullable enable
// LINQPad 9 / .NET 10
// Generates Dapper stored-procedure CALL methods (like your sample) from existing SQL Server stored procedures.
//
// Output:
// - StoredProcedureCaller.g.cs (or one-per-proc if you prefer)
// - Includes:
//   - IDbConnectionFactory + SqlConnectionFactory
//   - StoredProcedureCaller methods:
//       * ExecuteAsync for non-query procs (returns int rows affected)
//       * QueryAsync<T> for procs with result sets (returns IReadOnlyList<T>)
//       * If proc has OUTPUT/INOUT params: returns (int RowsAffected, IReadOnlyDictionary<string, object?> Output)
//   - Request record overload when parameter count >= Threshold
//
// Notes:
// - Resultset detection uses sys.sp_describe_first_result_set_for_object (best effort).
// - Some procs (dynamic SQL / multiple result sets) may not be describable; those default to ExecuteAsync.
// - TVPs are emitted as object? placeholders with TODO comments.
//
// CONFIG defaults are safe; edit only if you want different output paths/names.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;

// ─────────────────────────────────────────────
// CONFIGURATION
// ─────────────────────────────────────────────
const bool SaveToDisk = true;
readonly string OutputDir = @"C:\dev\GeneratedDapper";
const bool OneFilePerProc = false;                 // false = one big file (recommended)
const string NamespaceName = "Generated.Dapper";
const int RequestRecordThreshold = 8;              // create request record + overload when params >= this
const bool StripUspPrefixInMethodNames = true;     // usp_Customers_Insert -> CustomersInsertAsync
const bool IncludeSchemaInMethodNames = false;     // dbo_CustomersInsertAsync
const bool UseNullableRefs = true;
const bool EmitConfigureAwaitFalse = true;

// Optional filters
const string? ProcSchemaFilter = null; // e.g. "dbo" or null
readonly string? ProcNameLike = null;  // e.g. "%usp_Articles_%" or null

void Main()
{
	using var conn = new SqlConnection(this.Connection.ConnectionString);
	conn.Open();

	var procs = LoadStoredProcedures(conn, ProcSchemaFilter, ProcNameLike)
		.OrderBy(p => p.Schema)
		.ThenBy(p => p.Name)
		.ToList();

	if (procs.Count == 0)
	{
		"No stored procedures matched your filters.".Dump();
		return;
	}

	if (SaveToDisk) Directory.CreateDirectory(OutputDir);

	var combined = new StringBuilder();
	AppendFilePreamble(combined);

	int wrote = 0;

	foreach (var p in procs)
	{
		var procTwoPart = $"[{p.Schema}].[{p.Name}]";

		var parameters = LoadProcParameters(conn, p.ObjectId)
			.Where(x => !x.IsReturnValue)
			.ToList();

		var describe = DescribeFirstResultSet(conn, procTwoPart);
		var hasRows = describe.HasDescribableRows;

		var code = GenerateCallerCodeForProc(p.Schema, p.Name, parameters, hasRows);

		if (OneFilePerProc)
		{
			var fileName = $"{SanitizeFileName(BuildProcBaseName(p.Schema, p.Name))}.Caller.g.cs";
			var fullPath = Path.Combine(OutputDir, fileName);

			var sb = new StringBuilder();
			AppendFilePreamble(sb);
			sb.AppendLine(code);

			if (SaveToDisk)
			{
				File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
				$"Wrote {fullPath}".Dump();
				wrote++;
			}
			else
			{
				sb.ToString().Dump(fileName);
			}
		}
		else
		{
			combined.AppendLine(code);
			combined.AppendLine();
		}
	}

	if (!OneFilePerProc)
	{
		var outPath = Path.Combine(OutputDir, "StoredProcedureCaller.g.cs");
		if (SaveToDisk)
		{
			File.WriteAllText(outPath, combined.ToString(), Encoding.UTF8);
			$"Wrote {outPath}".Dump();
		}
		else
		{
			combined.ToString().Dump("StoredProcedureCaller.g.cs");
		}
	}

	$"Processed {procs.Count} stored procedures.".Dump();
}

// ─────────────────────────────────────────────
// Models
// ─────────────────────────────────────────────
record ProcInfo(int ObjectId, string Schema, string Name);

record ProcParam(
	string Name,           // without @
	string SqlTypeName,     // sys.types.name
	int MaxLength,          // bytes
	byte Precision,
	byte Scale,
	bool IsOutput,
	bool HasDefault,
	bool IsNullable,
	bool IsTableType,
	string? TableTypeName,
	bool IsReturnValue
);

record DescribeInfo(bool HasDescribableRows, string? ErrorMessage);

// ─────────────────────────────────────────────
// Metadata: stored procedures
// ─────────────────────────────────────────────
static List<ProcInfo> LoadStoredProcedures(SqlConnection conn, string? schemaFilter, string? nameLike)
{
	var sql = @"
SELECT p.object_id, s.name AS schema_name, p.name AS proc_name
FROM sys.procedures p
JOIN sys.schemas s ON s.schema_id = p.schema_id
WHERE p.is_ms_shipped = 0
  AND (@schema IS NULL OR s.name = @schema)
  AND (@like IS NULL OR p.name LIKE @like)
ORDER BY s.name, p.name;";

	using var cmd = new SqlCommand(sql, conn);
	cmd.Parameters.AddWithValue("@schema", (object?)schemaFilter ?? DBNull.Value);
	cmd.Parameters.AddWithValue("@like", (object?)nameLike ?? DBNull.Value);

	using var r = cmd.ExecuteReader();
	var list = new List<ProcInfo>();
	while (r.Read())
		list.Add(new ProcInfo(r.GetInt32(0), r.GetString(1), r.GetString(2)));

	return list;
}

static List<ProcParam> LoadProcParameters(SqlConnection conn, int procObjectId)
{
	const string sql = @"
SELECT
	p.parameter_id,
	p.name,
	ty.name AS type_name,
	p.max_length,
	p.precision,
	p.scale,
	p.is_output,
	p.has_default_value,
	p.is_nullable,
	CASE WHEN ty.is_table_type = 1 THEN 1 ELSE 0 END AS is_table_type,
	tt.name AS table_type_name
FROM sys.parameters p
JOIN sys.types ty ON ty.user_type_id = p.user_type_id
LEFT JOIN sys.table_types tt ON tt.user_type_id = p.user_type_id
WHERE p.object_id = @objId
ORDER BY p.parameter_id;";

	using var cmd = new SqlCommand(sql, conn);
	cmd.Parameters.AddWithValue("@objId", procObjectId);

	using var r = cmd.ExecuteReader();
	var list = new List<ProcParam>();

	while (r.Read())
	{
		var paramId = r.GetInt32(0);
		var rawName = r.IsDBNull(1) ? "" : r.GetString(1);
		var name = rawName.StartsWith("@", StringComparison.Ordinal) ? rawName[1..] : rawName;

		list.Add(new ProcParam(
			Name: name,
			SqlTypeName: r.GetString(2),
			MaxLength: r.GetInt16(3),
			Precision: r.GetByte(4),
			Scale: r.GetByte(5),
			IsOutput: r.GetBoolean(6),
			HasDefault: r.GetBoolean(7),
			IsNullable: r.GetBoolean(8),
			IsTableType: r.GetInt32(9) == 1,
			TableTypeName: r.IsDBNull(10) ? null : r.GetString(10),
			IsReturnValue: paramId == 0
		));
	}

	return list;
}

// ─────────────────────────────────────────────
// Result-set detection (best-effort)
// ─────────────────────────────────────────────
static DescribeInfo DescribeFirstResultSet(SqlConnection conn, string procTwoPartName)
{
	// We only need to know "does it look like it returns rows?"
	// If describable and returns at least one column, we treat as Query.
	const string sql = @"
DECLARE @objId int = OBJECT_ID(@procName);
IF @objId IS NULL
BEGIN
	SELECT CAST(1 AS bit) AS [HasError], CAST(N'OBJECT_ID not found' AS nvarchar(4000)) AS [ErrorMessage];
	RETURN;
END

BEGIN TRY
	EXEC sys.sp_describe_first_result_set_for_object @object_id = @objId, @browse_information_mode = 1;
END TRY
BEGIN CATCH
	SELECT CAST(1 AS bit) AS [HasError], ERROR_MESSAGE() AS [ErrorMessage];
END CATCH;";

	using var cmd = new SqlCommand(sql, conn);
	cmd.Parameters.AddWithValue("@procName", procTwoPartName);

	using var r = cmd.ExecuteReader();

	// Detect error-shape
	bool hasHasError = false;
	for (int i = 0; i < r.FieldCount; i++)
	{
		if (string.Equals(r.GetName(i), "HasError", StringComparison.OrdinalIgnoreCase))
		{
			hasHasError = true;
			break;
		}
	}

	if (hasHasError)
	{
		if (r.Read())
		{
			var msg = r["ErrorMessage"]?.ToString();
			return new DescribeInfo(false, msg);
		}
		return new DescribeInfo(false, "Unknown error");
	}

	// If it has rows, it’s a resultset (even if we don't use schema here)
	// sp_describe_* can return a row per column. If there’s at least one row -> likely returns rows.
	bool any = r.Read();
	return new DescribeInfo(any, null);
}

// ─────────────────────────────────────────────
// Code generation
// ─────────────────────────────────────────────
static string GenerateCallerCodeForProc(string schema, string procName, List<ProcParam> parameters, bool hasRows)
{
	var sb = new StringBuilder();

	// Container types emitted once per file in preamble if OneFilePerProc=false;
	// When OneFilePerProc=true, they still appear (fine).
	// We emit them only in preamble, not per-proc.

	// Request record (optional)
	var baseName = BuildProcBaseName(schema, procName);
	var methodBase = BuildMethodBaseName(schema, procName);
	var requestRecordName = $"{methodBase}Request";

	bool needsRequestRecord = parameters.Count >= RequestRecordThreshold;

	if (needsRequestRecord)
	{
		sb.AppendLine($"\t// Request record (generated when parameter count is high)");
		sb.AppendLine($"\tpublic sealed record {requestRecordName}({BuildRecordCtorParams(parameters)});");
		sb.AppendLine();
	}

	// Method signature
	bool hasOutput = parameters.Any(p => p.IsOutput);
	var returnType = GetReturnType(hasRows, hasOutput);

	sb.AppendLine($"\t/// <summary>");
	sb.AppendLine($"\t/// Calls {schema}.{procName}");
	sb.AppendLine($"\t/// </summary>");

	var methodName = $"{methodBase}Async";

	sb.AppendLine($"\tpublic async Task<{returnType}> {methodName}({BuildMethodParams(parameters)}CancellationToken cancellationToken = default)");
	sb.AppendLine("\t{");
	sb.AppendLine($"\t\tvar procName = QuoteProc(\"{EscapeForString(schema)}\", \"{EscapeForString(procName)}\");");
	sb.AppendLine("\t\tusing var conn = _connectionFactory.Create();");
	sb.AppendLine("\t\tif (conn.State != ConnectionState.Open) conn.Open();");
	sb.AppendLine();
	sb.AppendLine("\t\tvar dp = new DynamicParameters();");

	foreach (var p in parameters)
	{
		sb.AppendLine("\t\t" + BuildDynamicParamAddLine(p));
	}

	sb.AppendLine();
	sb.AppendLine("\t\tvar command = new CommandDefinition(procName, dp, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken);");

	if (hasRows)
	{
		sb.AppendLine($"\t\tvar rows = (await conn.QueryAsync<T>(command){CA()}).AsList();");
		sb.AppendLine("\t\treturn rows;");
	}
	else
	{
		sb.AppendLine($"\t\tvar rowsAffected = await conn.ExecuteAsync(command){CA()};");

		if (hasOutput)
		{
			sb.AppendLine();
			sb.AppendLine("\t\tvar output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);");
			foreach (var p in parameters.Where(x => x.IsOutput))
			{
				sb.AppendLine($"\t\toutput[\"{EscapeForString(ToPascalIdentifier(p.Name))}\"] = dp.Get<object?>(\"@{EscapeForString(p.Name)}\");");
			}
			sb.AppendLine("\t\treturn (rowsAffected, output);");
		}
		else
		{
			sb.AppendLine("\t\treturn rowsAffected;");
		}
	}

	sb.AppendLine("\t}");
	sb.AppendLine();

	// Request record overload
	if (needsRequestRecord)
	{
		sb.AppendLine($"\t/// <summary>");
		sb.AppendLine($"\t/// Calls {schema}.{procName} using a request record (composite parameters).");
		sb.AppendLine($"\t/// </summary>");
		sb.AppendLine($"\tpublic Task<{returnType}> {methodName}({requestRecordName} request, CancellationToken cancellationToken = default)");
		sb.AppendLine("\t{");
		sb.AppendLine("\t\tif (request is null) throw new ArgumentNullException(nameof(request));");

		var args = string.Join(", ", parameters.Select(p => $"request.{ToPascalIdentifier(p.Name)}"));
		sb.AppendLine($"\t\treturn {methodName}({args}{(args.Length > 0 ? ", " : "")}cancellationToken);");

		sb.AppendLine("\t}");
		sb.AppendLine();
	}

	return sb.ToString();

	static string CA() => EmitConfigureAwaitFalse ? ".ConfigureAwait(false)" : "";
}

static string GetReturnType(bool hasRows, bool hasOutput)
{
	if (hasRows)
		return "IReadOnlyList<T>";

	if (hasOutput)
		return "(int RowsAffected, IReadOnlyDictionary<string, object?> Output)";

	return "int";
}

static string BuildMethodParams(List<ProcParam> parameters)
{
	if (parameters.Count == 0) return "";

	return string.Join(", ",
		parameters.Select(p =>
		{
			var csType = MapSqlToCSharpForParam(p);
			var argName = ToCamelIdentifier(ToPascalIdentifier(p.Name));
			return $"{csType} {argName}, ";
		}));
}

static string BuildRecordCtorParams(List<ProcParam> parameters)
{
	// Records in your sample use PascalCase property names in ctor
	return string.Join(", ",
		parameters.Select(p =>
		{
			var csType = MapSqlToCSharpForParam(p);
			var propName = ToPascalIdentifier(p.Name);
			return $"{csType} {propName}";
		}));
}

static string BuildDynamicParamAddLine(ProcParam p)
{
	// dp.Add("@Name", name, DbType.X, ParameterDirection.Input, size: N);
	var paramName = "@" + p.Name;
	var argName = ToCamelIdentifier(ToPascalIdentifier(p.Name));
	var dbType = MapSqlToDbType(p.SqlTypeName);

	var dir = p.IsOutput ? (p.HasDefault ? "ParameterDirection.InputOutput" : "ParameterDirection.InputOutput") : "ParameterDirection.Input";

	// If output-only params ever appear (rare), treat as Output
	if (p.IsOutput && !p.HasDefault && string.IsNullOrWhiteSpace(p.Name) == false)
		dir = "ParameterDirection.InputOutput";

	if (p.IsTableType)
	{
		// TVP: Dapper supports AsTableValuedParameter, but that’s a different shape;
		// keep placeholder so generation never breaks compilation.
		var tvpName = p.TableTypeName ?? "YourTvpType";
		return $"// TODO TVP: dp.Add(\"{EscapeForString(paramName)}\", /* {tvpName} */ {argName}, dbType: {dbType}, direction: {dir});";
	}

	var sizeClause = BuildSizeClause(p);

	// If output param, we still pass the argument value (may be null)
	return $"dp.Add(\"{EscapeForString(paramName)}\", {argName}, {dbType}, {dir}{sizeClause});";
}

static string BuildSizeClause(ProcParam p)
{
	// Only for string/binary types where size matters. SQL max_length is bytes.
	var sql = p.SqlTypeName.ToLowerInvariant();
	bool isString =
		sql is "varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext";
	bool isBinary =
		sql is "varbinary" or "binary" or "image";

	if (!isString && !isBinary) return "";

	// nvarchar/nchar lengths are bytes; convert to chars for readability
	int size = p.MaxLength;

	if (size <= 0 || size == -1) return ""; // -1 = MAX

	if (sql is "nvarchar" or "nchar")
		size = size / 2;

	return $", size: {size.ToString(CultureInfo.InvariantCulture)}";
}

static string BuildProcBaseName(string schema, string procName)
{
	var name = procName;
	if (StripUspPrefixInMethodNames && name.StartsWith("usp_", StringComparison.OrdinalIgnoreCase))
		name = name[4..];

	if (IncludeSchemaInMethodNames)
		name = $"{schema}_{name}";

	return ToPascalIdentifier(name);
}

static string BuildMethodBaseName(string schema, string procName)
{
	// Sample: usp_Customers_Insert => CustomersInsert
	var name = procName;

	// Split by underscores & spaces for method readability
	if (StripUspPrefixInMethodNames && name.StartsWith("usp_", StringComparison.OrdinalIgnoreCase))
		name = name[4..];

	// Some procs might contain spaces (e.g. "usp_Order Details_Delete")
	name = name.Replace(" ", "_");

	if (IncludeSchemaInMethodNames)
		name = $"{schema}_{name}";

	// If it’s already like Customers_Insert -> CustomersInsert
	return ToPascalIdentifier(name);
}

// ─────────────────────────────────────────────
// Type mapping helpers
// ─────────────────────────────────────────────
static string MapSqlToCSharpForParam(ProcParam p)
{
	if (p.IsTableType)
		return UseNullableRefs ? "object?" : "object";

	var sql = p.SqlTypeName.ToLowerInvariant();

	string cs = sql switch
	{
		"int" => "int",
		"bigint" => "long",
		"smallint" => "short",
		"tinyint" => "byte",
		"bit" => "bool",
		"uniqueidentifier" => "Guid",
		"float" => "double",
		"real" => "float",
		"decimal" or "numeric" or "money" or "smallmoney" => "decimal",
		"date" or "datetime" or "datetime2" or "smalldatetime" => "DateTime",
		"datetimeoffset" => "DateTimeOffset",
		"time" => "TimeSpan",
		"char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" => "string",
		"binary" or "varbinary" or "image" => "byte[]",
		"xml" => "string",
		"sql_variant" => UseNullableRefs ? "object?" : "object",
		"rowversion" or "timestamp" => "byte[]",
		_ => UseNullableRefs ? "object?" : "object"
	};

	bool isValueType =
		cs is "int" or "long" or "short" or "byte" or "bool" or "Guid" or "double" or "float" or "decimal" or "DateTime" or "DateTimeOffset" or "TimeSpan";

	if (isValueType)
		return p.IsNullable ? cs + "?" : cs;

	if (UseNullableRefs)
		return p.IsNullable ? cs + "?" : cs;

	return cs;
}

static string MapSqlToDbType(string sqlTypeName)
{
	// Emit "DbType.X" string so call sites match your sample style
	var sql = sqlTypeName.ToLowerInvariant();

	// Note: Dapper ignores DbType for some types; still useful for clarity.
	return sql switch
	{
		"int" => "DbType.Int32",
		"bigint" => "DbType.Int64",
		"smallint" => "DbType.Int16",
		"tinyint" => "DbType.Byte",
		"bit" => "DbType.Boolean",
		"uniqueidentifier" => "DbType.Guid",
		"float" => "DbType.Double",
		"real" => "DbType.Single",
		"decimal" or "numeric" => "DbType.Decimal",
		"money" or "smallmoney" => "DbType.Currency",
		"date" => "DbType.Date",
		"datetime" or "smalldatetime" or "datetime2" => "DbType.DateTime",
		"datetimeoffset" => "DbType.DateTimeOffset",
		"time" => "DbType.Time",
		"char" or "varchar" or "text" => "DbType.String",
		"nchar" or "nvarchar" or "ntext" => "DbType.String",
		"binary" or "varbinary" or "image" => "DbType.Binary",
		"xml" => "DbType.String",
		"rowversion" or "timestamp" => "DbType.Binary",
		_ => "DbType.Object"
	};
}

// ─────────────────────────────────────────────
// File preamble + container class
// ─────────────────────────────────────────────
static void AppendFilePreamble(StringBuilder sb)
{
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
	sb.AppendLine("public sealed class StoredProcedureCaller");
	sb.AppendLine("{");
	sb.AppendLine("\tprivate readonly IDbConnectionFactory _connectionFactory;");
	sb.AppendLine();
	sb.AppendLine("\tpublic StoredProcedureCaller(IDbConnectionFactory connectionFactory)");
	sb.AppendLine("\t{");
	sb.AppendLine("\t\t_connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));");
	sb.AppendLine("\t}");
	sb.AppendLine();
	sb.AppendLine("\tprivate static string QuoteProc(string schema, string name) => $\"[{schema}].[{name}]\";");
	sb.AppendLine();

	// IMPORTANT: leave the class open; per-proc code appends methods/records; we close at the end.
	// When OneFilePerProc=false, we close at final write (in Main).
	// But this script writes the whole file at once, so we need to close it here AFTER generation.
	// We'll close by appending at the very end in Main by ensuring code generator includes it.
	// To keep it simple, we close it at the very end of Main output:
	// - When OneFilePerProc=false, we append "}" after loop.
	// - When OneFilePerProc=true, each file needs closing too; handled by GenerateTrailer().
}

// After all procs, we need to close StoredProcedureCaller class + namespace
static string GenerateTrailer() => "}\n";

// ─────────────────────────────────────────────
// Identifiers + utilities
// ─────────────────────────────────────────────
static string EscapeForString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

static string SanitizeFileName(string s)
{
	foreach (var c in Path.GetInvalidFileNameChars())
		s = s.Replace(c, '_');
	return s;
}

static string ToPascalIdentifier(string input)
{
	if (string.IsNullOrWhiteSpace(input)) return "Unnamed";

	// Replace non-letter/digit with underscores, then PascalCase segments
	var cleaned = new string(input.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
	var parts = cleaned.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);

	var sb = new StringBuilder();
	foreach (var part in parts)
	{
		if (part.Length == 0) continue;
		sb.Append(char.ToUpperInvariant(part[0]));
		if (part.Length > 1) sb.Append(part.Substring(1));
	}

	var id = sb.Length == 0 ? "Unnamed" : sb.ToString();

	if (char.IsDigit(id[0])) id = "P" + id;
	return id;
}

static string ToCamelIdentifier(string pascal)
{
	if (string.IsNullOrWhiteSpace(pascal)) return "value";
	if (pascal.Length == 1) return pascal.ToLowerInvariant();
	return char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
}
