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
  <NuGetReference>Microsoft.Data.SqlClient</NuGetReference>
  <NuGetReference>Dapper</NuGetReference>
  <Namespace>SqlCommand = Microsoft.Data.SqlClient.SqlCommand</Namespace>
  <Namespace>SqlConnection = Microsoft.Data.SqlClient.SqlConnection</Namespace>
</Query>

#nullable enable
// LINQPad 9 / .NET 10
// Generates C# (Dapper) stored procedure CALL methods from existing stored procedures.
// Outputs everything to Results and (optionally) writes to: C:\dve\GernerateeDapper


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
// CONFIGURATION (do not change defaults unless explicitly asked)
// ────────────────────────────────────────────────────────────────────────────────
const bool SaveToDisk = true; // still outputs to Results either way
string OutputDir = @"c:\dev\GernerateeDapper";

// Preserve existing scope expectation (same correct source as before).
// Default: include stored procedures whose name targets Articles or AspNet*.
Regex IncludeProcNameRegex = new Regex(@"(?i)\b(Articles|AspNet)\b|^(Articles|AspNet)", RegexOptions.Compiled);

// Optional: schema filter (empty = all)
HashSet<string> IncludeSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
	 "dbo"
};

// Naming (do not change unless explicitly asked)
const string DefaultProcPrefixToTrim = "usp_";

// Composite param object feature (new): if a procedure has >= this many parameters,
// generate a strongly-typed request record and an overload taking that record.
const int GenerateRequestRecordThreshold = 6;

// ────────────────────────────────────────────────────────────────────────────────
// MAIN
// ────────────────────────────────────────────────────────────────────────────────
async Task Main()
{
	var connectionString = this.Connection.ConnectionString;

	Directory.CreateDirectory(OutputDir);

	var procs = await LoadStoredProceduresAsync(connectionString, IncludeSchemas, IncludeProcNameRegex);

	var generated = GenerateDapperRepositoryCode(
		namespaceName: "Generated.Dapper",
		className: "StoredProcedureCaller",
		connectionFactoryInterface: "IDbConnectionFactory",
		connectionFactoryClass: "SqlConnectionFactory",
		storedProcedures: procs);

	// Output everything to Results
	generated.Dump("Generated C# (Dapper) - Stored Procedure Calls");

	if (SaveToDisk)
	{
		var filePath = Path.Combine(OutputDir, "StoredProcedureCaller.g.cs");
		await File.WriteAllTextAsync(filePath, generated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		filePath.Dump("Saved to");
	}

	new
	{
		TotalProcedures = procs.Count,
		Procedures = procs.Select(p => $"{p.Schema}.{p.Name}").ToList()
	}.Dump("Included Stored Procedures");
}

// ────────────────────────────────────────────────────────────────────────────────
// DB MODEL
// ────────────────────────────────────────────────────────────────────────────────
sealed record StoredProcedureModel(
	string Schema,
	string Name,
	IReadOnlyList<StoredProcedureParam> Parameters
);

sealed record StoredProcedureParam(
	string Name,              // without @
	string SqlTypeName,        // e.g. "nvarchar", "int"
	int? MaxLength,            // bytes for (n)varchar/(n)char; -1 for MAX
	byte Precision,            // decimal/numeric
	byte Scale,                // decimal/numeric
	bool IsNullable,
	bool IsOutput,
	bool IsTableType
);

// ────────────────────────────────────────────────────────────────────────────────
// LOAD METADATA
// ────────────────────────────────────────────────────────────────────────────────
static async Task<List<StoredProcedureModel>> LoadStoredProceduresAsync(
	string connectionString,
	HashSet<string> includeSchemas,
	Regex includeProcNameRegex)
{
	const string sql = @"
SELECT
	s.name  AS SchemaName,
	p.name  AS ProcName,
	prm.parameter_id,
	prm.name AS ParamName,
	typ.name AS TypeName,
	prm.max_length,
	prm.precision,
	prm.scale,
	prm.is_nullable,
	prm.is_output,
	typ.is_table_type
FROM sys.procedures p
JOIN sys.schemas s
	ON s.schema_id = p.schema_id
LEFT JOIN sys.parameters prm
	ON prm.object_id = p.object_id
LEFT JOIN sys.types typ
	ON typ.user_type_id = prm.user_type_id
WHERE p.is_ms_shipped = 0
ORDER BY s.name, p.name, prm.parameter_id;";

	await using var conn = new SqlConnection(connectionString);
	await conn.OpenAsync().ConfigureAwait(false);

	var rows = (await conn.QueryAsync(sql).ConfigureAwait(false)).ToList();

	var grouped = rows
		.GroupBy(r => new
		{
			Schema = (string)r.SchemaName,
			Name = (string)r.ProcName
		})
		.Select(g =>
		{
			var schema = g.Key.Schema;
			var name = g.Key.Name;

			var parms = new List<StoredProcedureParam>();

			foreach (var r in g)
			{
				string? rawParamName = r.ParamName as string;
				if (string.IsNullOrWhiteSpace(rawParamName))
					continue;

				var paramName = rawParamName.Trim();
				if (paramName.StartsWith("@", StringComparison.Ordinal))
					paramName = paramName[1..];

				string typeName = (r.TypeName as string) ?? "sql_variant";

				int? maxLength = null;
				try { if (r.max_length is not null) maxLength = (int)r.max_length; } catch { }

				byte precision = 0;
				byte scale = 0;
				try { if (r.precision is not null) precision = (byte)r.precision; } catch { }
				try { if (r.scale is not null) scale = (byte)r.scale; } catch { }

				bool isNullable = false;
				bool isOutput = false;
				bool isTableType = false;
				try { if (r.is_nullable is not null) isNullable = (bool)r.is_nullable; } catch { }
				try { if (r.is_output is not null) isOutput = (bool)r.is_output; } catch { }
				try { if (r.is_table_type is not null) isTableType = (bool)r.is_table_type; } catch { }

				parms.Add(new StoredProcedureParam(
					Name: paramName,
					SqlTypeName: typeName,
					MaxLength: maxLength,
					Precision: precision,
					Scale: scale,
					IsNullable: isNullable,
					IsOutput: isOutput,
					IsTableType: isTableType
				));
			}

			return new StoredProcedureModel(schema, name, parms);
		})
		.ToList();

	var filtered = grouped
		.Where(p =>
		{
			if (includeSchemas.Count > 0 && !includeSchemas.Contains(p.Schema))
				return false;

			return includeProcNameRegex.IsMatch(p.Name);
		})
		.OrderBy(p => p.Schema, StringComparer.OrdinalIgnoreCase)
		.ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
		.ToList();

	return filtered;
}

// ────────────────────────────────────────────────────────────────────────────────
// GENERATION
// ────────────────────────────────────────────────────────────────────────────────
static string GenerateDapperRepositoryCode(
	string namespaceName,
	string className,
	string connectionFactoryInterface,
	string connectionFactoryClass,
	IReadOnlyList<StoredProcedureModel> storedProcedures)
{
	var sb = new StringBuilder(256 * 1024);

	sb.AppendLine("// <auto-generated />");
	sb.AppendLine("#nullable enable");
	sb.AppendLine("using System;");
	sb.AppendLine("using System.Collections.Generic;");
	sb.AppendLine("using System.Data;");
	sb.AppendLine("using System.Threading;");
	sb.AppendLine("using System.Threading.Tasks;");
	sb.AppendLine("using Dapper;");
	sb.AppendLine("using Microsoft.Data.SqlClient;");
	sb.AppendLine();

	sb.AppendLine($"namespace {namespaceName};");
	sb.AppendLine();

	// Connection factory
	sb.AppendLine($"public interface {connectionFactoryInterface}");
	sb.AppendLine("{");
	sb.AppendLine("\tIDbConnection Create();");
	sb.AppendLine("}");
	sb.AppendLine();

	sb.AppendLine($"public sealed class {connectionFactoryClass} : {connectionFactoryInterface}");
	sb.AppendLine("{");
	sb.AppendLine("\tprivate readonly string _connectionString;");
	sb.AppendLine($"\tpublic {connectionFactoryClass}(string connectionString) => _connectionString = connectionString;");
	sb.AppendLine("\tpublic IDbConnection Create() => new SqlConnection(_connectionString);");
	sb.AppendLine("}");
	sb.AppendLine();

	// Caller class
	sb.AppendLine($"public sealed class {className}");
	sb.AppendLine("{");
	sb.AppendLine($"\tprivate readonly {connectionFactoryInterface} _connectionFactory;");
	sb.AppendLine();
	sb.AppendLine($"\tpublic {className}({connectionFactoryInterface} connectionFactory)");
	sb.AppendLine("\t{");
	sb.AppendLine("\t\t_connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));");
	sb.AppendLine("\t}");
	sb.AppendLine();

	sb.AppendLine();
	sb.AppendLine("\tprivate static string QuoteProc(string schema, string name) => $\"[{schema}].[{name}]\";");
	sb.AppendLine();

	// Request records (new feature)
	var requestRecords = storedProcedures
		.Where(p => p.Parameters.Count >= GenerateRequestRecordThreshold)
		.Select(GenerateRequestRecord)
		.Where(s => !string.IsNullOrWhiteSpace(s))
		.ToList();

	if (requestRecords.Count > 0)
	{
		sb.AppendLine("\t// Request records (generated when parameter count is high)");
		foreach (var rr in requestRecords)
		{
			foreach (var line in rr.Split('\n'))
				sb.AppendLine("\t" + line.TrimEnd('\r'));
			sb.AppendLine();
		}
	}

	foreach (var proc in storedProcedures)
	{
		sb.AppendLine(GenerateMethodsForProc(proc));
	}

	sb.AppendLine("}");
	return sb.ToString();

	// ────────────────────────────────────────────────────────────────────────────
	// Local generators
	// ────────────────────────────────────────────────────────────────────────────
	static string GenerateRequestRecord(StoredProcedureModel proc)
	{
		if (proc.Parameters.Count == 0) return string.Empty;

		var recordName = ToSafeTypeName(ToSafeMethodName(TrimPrefix(proc.Name, DefaultProcPrefixToTrim)) + "Request");

		var props = new List<string>();
		foreach (var prm in proc.Parameters)
		{
			var csName = ToPascal(ToSafeIdentifier(prm.Name));
			var csType = prm.IsTableType ? "DataTable" : MapSqlTypeToCSharp(prm);

			// OUTPUT params are represented as nullable so callers can omit initial values
			if (prm.IsOutput)
			{
				csType = MakeNullableIfValueType(csType);
			}

			// Nullability for ref-types
			if (!prm.IsTableType)
			{
				csType = ApplyNullability(csType, prm.IsNullable);
			}
			else
			{
				// DataTable typically non-null; allow nullable if declared nullable in SQL
				if (prm.IsNullable) csType = "DataTable?";
			}

			props.Add($"{csType} {csName}");
		}

		return $"public sealed record {recordName}({string.Join(", ", props)});";
	}

	static string GenerateMethodsForProc(StoredProcedureModel proc)
	{
		var sb = new StringBuilder(16 * 1024);

		var methodBaseName = ToSafeMethodName(TrimPrefix(proc.Name, DefaultProcPrefixToTrim));
		var recordName = ToSafeTypeName(methodBaseName + "Request");
		var procQuoted = $"QuoteProc(\"{proc.Schema}\", \"{proc.Name}\")";

		// Heuristic: query-ish names => Query; else Execute
		var isQuery = Regex.IsMatch(proc.Name, @"(?i)\b(Get|Select|List|Search|Fetch|Read|Query)\b")
			|| proc.Name.Contains("Get", StringComparison.OrdinalIgnoreCase)
			|| proc.Name.Contains("List", StringComparison.OrdinalIgnoreCase)
			|| proc.Name.Contains("Search", StringComparison.OrdinalIgnoreCase);

		// If there are OUTPUT params, return outputs as dictionary (name->object?)
		var hasOutput = proc.Parameters.Any(p => p.IsOutput);

		// Build parameters
		var signatureParams = new List<string>();
		var dpAdds = new List<string>();

		foreach (var prm in proc.Parameters)
		{
			var csArgName = ToCamel(ToSafeIdentifier(prm.Name));
			var csType = prm.IsTableType ? "DataTable" : MapSqlTypeToCSharp(prm);

			if (prm.IsOutput)
			{
				// Dapper output values: we pass initial value (often null) and retrieve after execution
				csType = MakeNullableIfValueType(csType);
			}
			else
			{
				csType = ApplyNullability(csType, prm.IsNullable);
			}

			if (prm.IsTableType && prm.IsNullable)
				csType = "DataTable?";

			signatureParams.Add($"{csType} {csArgName}");

			// DynamicParameters
			var dbType = MapSqlTypeToDbType(prm.SqlTypeName);
			var dbTypeArg = dbType is null ? "null" : $"DbType.{dbType}";
			var direction = prm.IsOutput ? "ParameterDirection.InputOutput" : "ParameterDirection.Input";
			var sizeArg = BuildSizeArg(prm);

			dpAdds.Add(
				$"\t\t\tdp.Add(\"@{prm.Name}\", {csArgName}, {dbTypeArg}, {direction}{sizeArg});"
			);
		}

		var ctSig = "CancellationToken cancellationToken = default";
		var argsWithCt = signatureParams.Count == 0
			? ctSig
			: string.Join(", ", signatureParams) + ", " + ctSig;

		var argsNoCt = signatureParams.Count == 0
			? string.Empty
			: string.Join(", ", signatureParams);

		// New composite feature: request-record overload for high-param procs
		var generateRequestOverload = proc.Parameters.Count >= GenerateRequestRecordThreshold && proc.Parameters.Count > 0;

		// Decide method signatures
		if (isQuery)
		{
			// Query: generic result list (caller supplies T)
			var returnType = hasOutput
				? $"Task<(IReadOnlyList<T> Rows, IReadOnlyDictionary<string, object?> Output)>"
				: "Task<IReadOnlyList<T>>";

			sb.AppendLine("\t/// <summary>");
			sb.AppendLine($"\t/// Calls {proc.Schema}.{proc.Name}");
			sb.AppendLine("\t/// </summary>");
			sb.AppendLine($"\tpublic async {returnType} {methodBaseName}Async<T>({argsWithCt})");
			sb.AppendLine("\t{");
			sb.AppendLine($"\t\tvar procName = {procQuoted};");
			sb.AppendLine("\t\tusing var conn = _connectionFactory.Create();");
			sb.AppendLine("\t\tif (conn.State != ConnectionState.Open) conn.Open();");
			sb.AppendLine();
			sb.AppendLine("\t\tvar dp = new DynamicParameters();");
			foreach (var add in dpAdds) sb.AppendLine(add);
			sb.AppendLine();
			sb.AppendLine("\t\tvar command = new CommandDefinition(procName, dp, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken);");
			sb.AppendLine("\t\tvar rows = (await conn.QueryAsync<T>(command).ConfigureAwait(false)).AsList();");

			if (hasOutput)
			{
				sb.AppendLine();
				sb.AppendLine("\t\tvar output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);");
				foreach (var prm in proc.Parameters.Where(p => p.IsOutput))
				{
					sb.AppendLine($"\t\toutput[\"{prm.Name}\"] = dp.Get<object?>(\"@{prm.Name}\");");
				}
				sb.AppendLine("\t\treturn (rows, output);");
			}
			else
			{
				sb.AppendLine("\t\treturn rows;");
			}

			sb.AppendLine("\t}");
			sb.AppendLine();

			if (generateRequestOverload)
			{
				sb.AppendLine("\t/// <summary>");
				sb.AppendLine($"\t/// Calls {proc.Schema}.{proc.Name} using a request record (composite parameters).");
				sb.AppendLine("\t/// </summary>");
				sb.AppendLine($"\tpublic {returnType} {methodBaseName}Async<T>({recordName} request, {ctSig})");
				sb.AppendLine("\t{");
				sb.AppendLine("\t\tif (request is null) throw new ArgumentNullException(nameof(request));");

				var callArgs = string.Join(", ", proc.Parameters.Select(p => $"request.{ToPascal(ToSafeIdentifier(p.Name))}"));
				if (string.IsNullOrWhiteSpace(callArgs))
				{
					sb.AppendLine($"\t\treturn {methodBaseName}Async<T>({ctSig.Split('=')[0].Trim()});");
				}
				else
				{
					sb.AppendLine($"\t\treturn {methodBaseName}Async<T>({callArgs}, cancellationToken);");
				}

				sb.AppendLine("\t}");
				sb.AppendLine();
			}
		}
		else
		{
			// Execute: returns rows affected (and outputs if any)
			var returnType = hasOutput
				? "Task<(int RowsAffected, IReadOnlyDictionary<string, object?> Output)>"
				: "Task<int>";

			sb.AppendLine("\t/// <summary>");
			sb.AppendLine($"\t/// Calls {proc.Schema}.{proc.Name}");
			sb.AppendLine("\t/// </summary>");
			sb.AppendLine($"\tpublic async {returnType} {methodBaseName}Async({argsWithCt})");
			sb.AppendLine("\t{");
			sb.AppendLine($"\t\tvar procName = {procQuoted};");
			sb.AppendLine("\t\tusing var conn = _connectionFactory.Create();");
			sb.AppendLine("\t\tif (conn.State != ConnectionState.Open) conn.Open();");
			sb.AppendLine();
			sb.AppendLine("\t\tvar dp = new DynamicParameters();");
			foreach (var add in dpAdds) sb.AppendLine(add);
			sb.AppendLine();
			sb.AppendLine("\t\tvar command = new CommandDefinition(procName, dp, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken);");
			sb.AppendLine("\t\tvar rowsAffected = await conn.ExecuteAsync(command).ConfigureAwait(false);");

			if (hasOutput)
			{
				sb.AppendLine();
				sb.AppendLine("\t\tvar output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);");
				foreach (var prm in proc.Parameters.Where(p => p.IsOutput))
				{
					sb.AppendLine($"\t\toutput[\"{prm.Name}\"] = dp.Get<object?>(\"@{prm.Name}\");");
				}
				sb.AppendLine("\t\treturn (rowsAffected, output);");
			}
			else
			{
				sb.AppendLine("\t\treturn rowsAffected;");
			}

			sb.AppendLine("\t}");
			sb.AppendLine();

			if (generateRequestOverload)
			{
				sb.AppendLine("\t/// <summary>");
				sb.AppendLine($"\t/// Calls {proc.Schema}.{proc.Name} using a request record (composite parameters).");
				sb.AppendLine("\t/// </summary>");
				sb.AppendLine($"\tpublic {returnType} {methodBaseName}Async({recordName} request, {ctSig})");
				sb.AppendLine("\t{");
				sb.AppendLine("\t\tif (request is null) throw new ArgumentNullException(nameof(request));");

				var callArgs = string.Join(", ", proc.Parameters.Select(p => $"request.{ToPascal(ToSafeIdentifier(p.Name))}"));
				if (string.IsNullOrWhiteSpace(callArgs))
				{
					sb.AppendLine($"\t\treturn {methodBaseName}Async({ctSig.Split('=')[0].Trim()});");
				}
				else
				{
					sb.AppendLine($"\t\treturn {methodBaseName}Async({callArgs}, cancellationToken);");
				}

				sb.AppendLine("\t}");
				sb.AppendLine();
			}
		}

		return sb.ToString();
	}
}

// ────────────────────────────────────────────────────────────────────────────────
// TYPE MAPPING
// ────────────────────────────────────────────────────────────────────────────────
static string MapSqlTypeToCSharp(StoredProcedureParam prm)
{
	// Important: Keep simple, stable mappings; unknown => object?
	// Nullability handled separately.
	var t = prm.SqlTypeName.ToLowerInvariant();

	return t switch
	{
		"bigint" => "long",
		"int" => "int",
		"smallint" => "short",
		"tinyint" => "byte",
		"bit" => "bool",

		"decimal" => "decimal",
		"numeric" => "decimal",
		"money" => "decimal",
		"smallmoney" => "decimal",
		"float" => "double",
		"real" => "float",

		"date" => "DateTime",
		"datetime" => "DateTime",
		"datetime2" => "DateTime",
		"smalldatetime" => "DateTime",
		"time" => "TimeSpan",
		"datetimeoffset" => "DateTimeOffset",

		"uniqueidentifier" => "Guid",

		"char" => "string",
		"nchar" => "string",
		"varchar" => "string",
		"nvarchar" => "string",
		"text" => "string",
		"ntext" => "string",
		"xml" => "string",

		"binary" => "byte[]",
		"varbinary" => "byte[]",
		"image" => "byte[]",

		"sql_variant" => "object",
		"timestamp" => "byte[]",
		"rowversion" => "byte[]",

		_ => "object"
	};
}

static string? MapSqlTypeToDbType(string sqlTypeName)
{
	var t = sqlTypeName.ToLowerInvariant();

	return t switch
	{
		"bigint" => "Int64",
		"int" => "Int32",
		"smallint" => "Int16",
		"tinyint" => "Byte",
		"bit" => "Boolean",

		"decimal" => "Decimal",
		"numeric" => "Decimal",
		"money" => "Currency",
		"smallmoney" => "Currency",
		"float" => "Double",
		"real" => "Single",

		"date" => "Date",
		"datetime" => "DateTime",
		"datetime2" => "DateTime2",
		"smalldatetime" => "DateTime",
		"time" => "Time",
		"datetimeoffset" => "DateTimeOffset",

		"uniqueidentifier" => "Guid",

		"char" => "AnsiStringFixedLength",
		"varchar" => "AnsiString",
		"text" => "AnsiString",

		"nchar" => "StringFixedLength",
		"nvarchar" => "String",
		"ntext" => "String",

		"xml" => "Xml",

		"binary" => "Binary",
		"varbinary" => "Binary",
		"image" => "Binary",

		"sql_variant" => "Object",

		_ => null
	};
}

static string BuildSizeArg(StoredProcedureParam prm)
{
	// Dapper supports "size" for strings/binary. For NVARCHAR etc, SQL max_length is bytes.
	// For nvarchar/nchar: bytes / 2
	if (prm.MaxLength is null) return string.Empty;

	var t = prm.SqlTypeName.ToLowerInvariant();
	var maxLength = prm.MaxLength.Value;

	if (maxLength <= 0) return string.Empty; // includes -1 (MAX) or 0

	if (t is "nvarchar" or "nchar")
	{
		var chars = maxLength / 2;
		return $", size: {chars.ToString(CultureInfo.InvariantCulture)}";
	}

	if (t is "varchar" or "char" or "varbinary" or "binary")
	{
		return $", size: {maxLength.ToString(CultureInfo.InvariantCulture)}";
	}

	return string.Empty;
}

static string ApplyNullability(string csType, bool isNullable)
{
	// ref-types: string, byte[], object, DataTable already nullable by nature (but allow explicit ? for string?).
	// value-types: add ?
	if (!isNullable) return csType;

	if (csType is "string" or "byte[]" or "object")
		return csType + "?";

	if (csType.EndsWith("[]", StringComparison.Ordinal))
		return csType + "?";

	if (csType.EndsWith("?", StringComparison.Ordinal))
		return csType;

	// likely value type
	return csType + "?";
}

static string MakeNullableIfValueType(string csType)
{
	if (csType.EndsWith("?", StringComparison.Ordinal)) return csType;
	if (csType is "string" or "object") return csType + "?";
	if (csType.EndsWith("[]", StringComparison.Ordinal)) return csType + "?";
	return csType + "?";
}

// ────────────────────────────────────────────────────────────────────────────────
// IDENTIFIERS
// ────────────────────────────────────────────────────────────────────────────────
static string TrimPrefix(string name, string prefix)
{
	if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		return name.Substring(prefix.Length);
	return name;
}

static string ToSafeMethodName(string name)
{
	// Replace invalid chars, then PascalCase tokens
	var cleaned = Regex.Replace(name, @"[^\w]+", "_");
	cleaned = Regex.Replace(cleaned, @"_+", "_").Trim('_');
	if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Proc";

	// Keep underscores as token boundaries
	var parts = cleaned.Split('_', StringSplitOptions.RemoveEmptyEntries);
	var pascal = string.Concat(parts.Select(ToPascal));

	// C# keywords
	if (IsCSharpKeyword(pascal))
		pascal = "@" + pascal;

	return pascal;
}

static string ToSafeTypeName(string name)
{
	var n = ToSafeMethodName(name);
	// Type name should not start with '@'
	return n.StartsWith("@", StringComparison.Ordinal) ? n.Substring(1) : n;
}

static string ToSafeIdentifier(string name)
{
	var cleaned = Regex.Replace(name, @"[^\w]+", "_");
	cleaned = Regex.Replace(cleaned, @"_+", "_").Trim('_');
	if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "param";

	if (char.IsDigit(cleaned[0]))
		cleaned = "_" + cleaned;

	return cleaned;
}

static string ToCamel(string s)
{
	var p = ToPascal(s);
	if (string.IsNullOrEmpty(p)) return p;
	if (p.Length == 1) return p.ToLowerInvariant();
	return char.ToLowerInvariant(p[0]) + p.Substring(1);
}

static string ToPascal(string s)
{
	if (string.IsNullOrWhiteSpace(s)) return string.Empty;

	// Preserve existing PascalCase segments while normalizing separators
	var tokens = Regex.Split(s, @"[_\s\-]+").Where(t => t.Length > 0);
	var sb = new StringBuilder();
	foreach (var t in tokens)
	{
		if (t.Length == 1)
		{
			sb.Append(char.ToUpperInvariant(t[0]));
			continue;
		}

		// If token is already mixed-case, keep first as upper but keep rest as-is
		sb.Append(char.ToUpperInvariant(t[0]));
		sb.Append(t.Substring(1));
	}
	var res = sb.ToString();

	// If starts with digit, prefix underscore
	if (res.Length > 0 && char.IsDigit(res[0]))
		res = "_" + res;

	// Handle keyword
	if (IsCSharpKeyword(res))
		res = "@" + res;

	return res;
}

static bool IsCSharpKeyword(string s)
{
	// minimal set; enough to avoid obvious collisions
	return s is
		"class" or "namespace" or "public" or "private" or "protected" or "internal" or
		"void" or "string" or "object" or "int" or "long" or "short" or "byte" or "bool" or
		"decimal" or "double" or "float" or "return" or "new" or "record" or "params" or
		"ref" or "out" or "in" or "base" or "this" or "event" or "operator" or "default";
}
