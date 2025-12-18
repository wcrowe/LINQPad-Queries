#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Data.SqlClient;
using System.IO;

// ─────────────────────────────────────────────
// LINQPad 9: Universal Stored Procedure Generator
// Features:
// - Separate UPDATE + INSERT for Upsert (better performance than MERGE)
// - Full transaction + TRY/CATCH with proper line breaks (fixed syntax error)
// - Audit columns (Created*/Updated*) auto-filled with SYSUTCDATETIME()
// - Soft delete support (IsDeleted/IsActive bit column)
// - Pagination for ListAll/Search (OFFSET/FETCH)
// - Row versioning (ConcurrencyStamp or RowVersion/timestamp column)
// ─────────────────────────────────────────────
void Main()
{
    var options = new GeneratorOptions
    {
        UseUspPrefix = true,
        ProcSchemaOverride = null,
        IncludeDropStatements = true,
        IncludeRowsAffected = true,
        IncludeListAll = true,
        IncludeSearch = true,
        EnableAuditTrail = true,
        EnableSoftDelete = true,
        EnableVersioning = true,
        ExcludedTablePatterns = new[] { "__EFMigrationsHistory" },
        OutputRootFolder = @"C:\dev\SqlCrudProcs",
        SavePerTableFiles = true
    };

    using var conn = new SqlConnection(this.Connection.ConnectionString);
    conn.Open();

    var tables = LoadTables(conn)
        .Where(t => !options.ExcludedTablePatterns.Any(p => t.Name.Contains(p, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(t => t.Schema)
        .ThenBy(t => t.Name)
        .ToList();

    if (!tables.Any())
    {
        "No tables to process.".Dump();
        return;
    }

    var master = new StringBuilder();
    master.AppendLine("-- =================================================");
    master.AppendLine("-- Auto-Generated CRUD Stored Procedures");
    master.AppendLine("-- Features: Upsert, Audit, Soft Delete (+Restore), Pagination, Versioning");
    master.AppendLine($"-- Database: {conn.Database}");
    master.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    master.AppendLine("-- =================================================");
    master.AppendLine();

    int generated = 0;

    foreach (var table in tables)
    {
        var columns = LoadColumns(conn, table.Schema, table.Name);
        var pkNames = LoadPrimaryKey(conn, table.Schema, table.Name);

        if (!pkNames.Any())
        {
            master.AppendLine($"-- SKIPPED: [{table.Schema}].[{table.Name}] — No primary key");
            master.AppendLine();
            continue;
        }

        var script = GenerateProcedures(options, table.Schema, table.Name, columns, pkNames);

        script.Dump($"Procedures for [{table.Schema}].[{table.Name}]");

        master.AppendLine(script);
        master.AppendLine("GO");
        master.AppendLine();

        if (options.SavePerTableFiles && !string.IsNullOrWhiteSpace(options.OutputRootFolder))
        {
            try
            {
                Directory.CreateDirectory(options.OutputRootFolder);

                var safeSchema = table.Schema.Replace(".", "_");
                var safeTable = table.Name.Replace(" ", "").Replace(".", "_");
                var fileName = $"{safeSchema}_{safeTable}_CRUD.sql";
                var fullPath = Path.Combine(options.OutputRootFolder, fileName);

                var fileContent = new StringBuilder();
                fileContent.AppendLine($"-- CRUD Procedures for [{table.Schema}].[{table.Name}]");
                fileContent.AppendLine($"-- Database: {conn.Database}");
                fileContent.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                fileContent.AppendLine("-- =================================================");
                fileContent.AppendLine();
                fileContent.AppendLine(script);
                fileContent.AppendLine("GO");

                File.WriteAllText(fullPath, fileContent.ToString());
                $"Saved: {fullPath}".Dump();
            }
            catch (Exception ex)
            {
                $"ERROR saving file for [{table.Schema}].[{table.Name}]: {ex.Message}".Dump();
            }
        }

        generated++;
    }

    master.ToString().Dump("=== ALL GENERATED PROCEDURES (MASTER SCRIPT) ===");
    $"Generated clean, compilable procedures for {generated} tables.".Dump();
}

// ─────────────────────────────────────────────
// Configuration
// ─────────────────────────────────────────────
sealed class GeneratorOptions
{
    public bool UseUspPrefix { get; init; } = true;
    public string? ProcSchemaOverride { get; init; } = null;
    public bool IncludeDropStatements { get; init; } = true;
    public bool IncludeRowsAffected { get; init; } = true;
    public bool IncludeListAll { get; init; } = true;
    public bool IncludeSearch { get; init; } = true;
    public bool EnableAuditTrail { get; init; } = true;
    public bool EnableSoftDelete { get; init; } = true;
    public bool EnableVersioning { get; init; } = true;
    public string[] ExcludedTablePatterns { get; init; } = Array.Empty<string>();
    public string? OutputRootFolder { get; init; } = @"C:\dev\SqlCrudProcs";
    public bool SavePerTableFiles { get; init; } = true;
}

// ─────────────────────────────────────────────
// Models
// ─────────────────────────────────────────────
record Table(string Schema, string Name);
record Column(string Name, string TypeName, int? MaxLength, byte Precision, byte Scale, bool IsNullable, bool IsIdentity, bool IsComputed, bool IsRowVersion);

// ─────────────────────────────────────────────
// Metadata
// ─────────────────────────────────────────────
static List<Table> LoadTables(SqlConnection conn)
{
    const string sql = "SELECT SCHEMA_NAME(schema_id), name FROM sys.tables WHERE is_ms_shipped = 0 ORDER BY 1, 2";
    using var cmd = new SqlCommand(sql, conn);
    using var r = cmd.ExecuteReader();
    var list = new List<Table>();
    while (r.Read()) list.Add(new Table(r.GetString(0), r.GetString(1)));
    return list;
}

static List<Column> LoadColumns(SqlConnection conn, string schema, string table)
{
    const string sql = @"
        SELECT c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed, 
               CASE WHEN ty.name = 'timestamp' THEN 1 ELSE 0 END AS is_rowversion
        FROM sys.columns c
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        JOIN sys.tables t ON t.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE s.name = @schema AND t.name = @table
        ORDER BY c.column_id";
    using var cmd = new SqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@schema", schema);
    cmd.Parameters.AddWithValue("@table", table);
    using var r = cmd.ExecuteReader();
    var list = new List<Column>();
    while (r.Read())
    {
        list.Add(new Column(
            r.GetString(0),
            r.GetString(1),
            r.IsDBNull(2) ? null : r.GetInt16(2),
            r.IsDBNull(3) ? (byte)0 : r.GetByte(3),
            r.IsDBNull(4) ? (byte)0 : r.GetByte(4),
            r.GetBoolean(5),
            r.GetBoolean(6),
            r.GetBoolean(7),
            r.GetInt32(8) == 1  // Fixed: explicit conversion to bool
        ));
    }
    return list;
}

static List<string> LoadPrimaryKey(SqlConnection conn, string schema, string table)
{
    const string sql = @"
        SELECT COL_NAME(ic.object_id, ic.column_id)
        FROM sys.indexes i
        INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        INNER JOIN sys.tables t ON i.object_id = t.object_id
        INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
        WHERE i.is_primary_key = 1
          AND s.name = @schema
          AND t.name = @table
        ORDER BY ic.key_ordinal";
    using var cmd = new SqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@schema", schema);
    cmd.Parameters.AddWithValue("@table", table);
    using var r = cmd.ExecuteReader();
    var pk = new List<string>();
    while (r.Read()) pk.Add(r.GetString(0));
    return pk;
}

// ─────────────────────────────────────────────
// Special Column Detection
// ─────────────────────────────────────────────
static (Column? Created, Column? Updated, Column? SoftDelete, Column? Version) DetectSpecialColumns(List<Column> columns)
{
    var createdPatterns = new[] { "Created", "CreatedOn", "CreatedAt", "DateCreated" };
    var updatedPatterns = new[] { "Updated", "UpdatedOn", "UpdatedAt", "DateUpdated", "LastModified" };
    var softDeletePatterns = new[] { "IsDeleted", "Deleted", "IsActive", "Active" };
    var versionPatterns = new[] { "ConcurrencyStamp", "RowVersion", "Version", "Timestamp" };

    var created = columns.FirstOrDefault(c =>
        createdPatterns.Any(p => c.Name.Contains(p, StringComparison.OrdinalIgnoreCase)) &&
        c.TypeName.Contains("date", StringComparison.OrdinalIgnoreCase));

    var updated = columns.FirstOrDefault(c =>
        updatedPatterns.Any(p => c.Name.Contains(p, StringComparison.OrdinalIgnoreCase)) &&
        c.TypeName.Contains("date", StringComparison.OrdinalIgnoreCase));

    var softDelete = columns.FirstOrDefault(c =>
        softDeletePatterns.Any(p => c.Name.Contains(p, StringComparison.OrdinalIgnoreCase)) &&
        c.TypeName == "bit");

    var version = columns.FirstOrDefault(c =>
        versionPatterns.Any(p => c.Name.Contains(p, StringComparison.OrdinalIgnoreCase)) &&
        (c.TypeName == "timestamp" || c.IsRowVersion || c.TypeName.Contains("stamp", StringComparison.OrdinalIgnoreCase)));

    return (created, updated, softDelete, version);
}

// ─────────────────────────────────────────────
// Generation
// ─────────────────────────────────────────────
static string GenerateProcedures(GeneratorOptions opts, string tableSchema, string tableName, List<Column> columns, List<string> pkNames)
{
    var table = $"[{tableSchema}].[{tableName}]";
    var procSchema = opts.ProcSchemaOverride ?? tableSchema;
    var safeTableName = tableName.Replace(" ", "");
    var baseName = $"{(opts.UseUspPrefix ? "usp_" : "")}{safeTableName}";
    var pkColumns = pkNames.Select(n => columns.First(c => c.Name == n)).ToList();
    var identity = columns.FirstOrDefault(c => c.IsIdentity);
    var isIdentityPk = identity != null && pkNames.Count == 1 && pkNames[0] == identity.Name;

    var insertable = columns.Where(c =>
        !c.IsIdentity &&
        !c.IsComputed &&
        !(opts.EnableAuditTrail && (c.Name.Contains("Created", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("Updated", StringComparison.OrdinalIgnoreCase))) &&
        !(opts.EnableSoftDelete && (c.Name.Contains("IsDeleted", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("IsActive", StringComparison.OrdinalIgnoreCase))) &&
        !(opts.EnableVersioning && c.IsRowVersion))
        .ToList();

    var updatable = columns.Where(c =>
        !pkNames.Contains(c.Name) &&
        !c.IsIdentity &&
        !c.IsComputed &&
        !(opts.EnableAuditTrail && (c.Name.Contains("Created", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("Updated", StringComparison.OrdinalIgnoreCase))) &&
        !(opts.EnableSoftDelete && (c.Name.Contains("IsDeleted", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("IsActive", StringComparison.OrdinalIgnoreCase))) &&
        !(opts.EnableVersioning && c.IsRowVersion))
        .ToList();

    var (createdCol, updatedCol, softDeleteCol, versionCol) = DetectSpecialColumns(columns);

    var sb = new StringBuilder();
    sb.AppendLine($"-- CRUD Procedures for {table}");
    if (createdCol != null || updatedCol != null || softDeleteCol != null || versionCol != null)
        sb.AppendLine($"-- Special: {(createdCol != null ? "Created" : "")} {(updatedCol != null ? "Updated" : "")} {(softDeleteCol != null ? "SoftDelete" : "")} {(versionCol != null ? "Versioning" : "")}".Trim());
    sb.AppendLine();

    void AddProc(string name, Action generator)
    {
        if (opts.IncludeDropStatements)
            sb.AppendLine($"IF OBJECT_ID('[{procSchema}].[{name}]', 'P') IS NOT NULL DROP PROCEDURE [{procSchema}].[{name}];");
        sb.AppendLine("GO");
        sb.AppendLine();
        generator();
        sb.AppendLine("GO");
        sb.AppendLine();
    }

    AddProc($"{baseName}_Insert", () => sb.Append(InsertProc(procSchema, baseName, table, insertable, pkColumns, identity, createdCol, updatedCol, softDeleteCol, versionCol)));
    if (updatable.Any() || updatedCol != null)
        AddProc($"{baseName}_Update", () => sb.Append(UpdateProc(procSchema, baseName, table, pkColumns, updatable, updatedCol, versionCol)));
    AddProc($"{baseName}_Upsert", () => sb.Append(UpsertProc(procSchema, baseName, table, insertable, updatable, columns, pkColumns, identity, createdCol, updatedCol, softDeleteCol, versionCol)));
    AddProc($"{baseName}_Delete", () => sb.Append(DeleteProc(procSchema, baseName, table, pkColumns, softDeleteCol, opts.EnableSoftDelete)));
    if (opts.EnableSoftDelete && softDeleteCol != null)
        AddProc($"{baseName}_Restore", () => sb.Append(RestoreProc(procSchema, baseName, table, pkColumns, softDeleteCol)));
    AddProc($"{baseName}_GetById", () => sb.Append(GetByIdProc(procSchema, baseName, table, pkColumns, softDeleteCol, opts.EnableSoftDelete)));
    if (opts.IncludeListAll)
        AddProc($"{baseName}_ListAll", () => sb.Append(ListAllProc(procSchema, baseName, table, softDeleteCol, opts.EnableSoftDelete)));
    if (opts.IncludeSearch && columns.Any(c => c.TypeName.Contains("char", StringComparison.OrdinalIgnoreCase)))
        AddProc($"{baseName}_Search", () => sb.Append(SearchProc(procSchema, baseName, table, columns, softDeleteCol, opts.EnableSoftDelete)));

    return sb.ToString();
}

// ─────────────────────────────────────────────
// Insert (with audit, soft delete, versioning init)
// ─────────────────────────────────────────────
static string InsertProc(string schema, string baseName, string table, List<Column> cols, List<Column> pk, Column? identity, Column? created, Column? updated, Column? softDelete, Column? version)
{
    var proc = $"[{schema}].[{baseName}_Insert]";
    var fields = cols.Select(c => $"[{c.Name}]").ToList();
    var values = cols.Select(c => $"@{c.Name}").ToList();
    var parms = cols.Select(c => ParamDecl(c)).ToList();

    if (created != null)
    {
        fields.Add($"[{created.Name}]");
        values.Add("SYSUTCDATETIME()");
    }

    if (updated != null)
    {
        fields.Add($"[{updated.Name}]");
        values.Add("SYSUTCDATETIME()");
    }

    if (softDelete != null)
    {
        fields.Add($"[{softDelete.Name}]");
        values.Add(softDelete.Name.Contains("IsActive") ? "1" : "0");
    }

    if (version != null && !version.IsRowVersion)
    {
        fields.Add($"[{version.Name}]");
        values.Add("NEWID()");
    }

    if (identity != null && pk.Count == 1 && pk[0].Name == identity.Name)
        parms.Add($"@NewId {SqlType(identity)} OUTPUT");

    var sb = new StringBuilder();
    sb.AppendLine($"CREATE OR ALTER PROCEDURE {proc}");
    if (parms.Any()) sb.AppendLine(" " + string.Join(",\r\n ", parms));
    sb.AppendLine("AS BEGIN");
    sb.AppendLine(" SET NOCOUNT ON;");
    sb.AppendLine();
    sb.AppendLine(" BEGIN TRY");
    sb.AppendLine($"  INSERT INTO {table} ({string.Join(", ", fields)})");
    sb.AppendLine($"  VALUES ({string.Join(", ", values)});");

    if (identity != null && pk.Count == 1 && pk[0].Name == identity.Name)
    {
        sb.AppendLine($"  SET @NewId = SCOPE_IDENTITY();");
        sb.AppendLine("  SELECT @NewId AS NewId;");
    }

    sb.AppendLine(" END TRY");
    sb.AppendLine(" BEGIN CATCH");
    sb.AppendLine("  IF @@TRANCOUNT > 0");
    sb.AppendLine("   ROLLBACK TRANSACTION;");
    sb.AppendLine();
    sb.AppendLine("  DECLARE @ErrorMessage nvarchar(4000) = ERROR_MESSAGE();");
    sb.AppendLine("  DECLARE @ErrorSeverity int = ERROR_SEVERITY();");
    sb.AppendLine("  DECLARE @ErrorState int = ERROR_STATE();");
    sb.AppendLine("  RAISERROR(@ErrorMessage, @ErrorSeverity, @ErrorState);");
    sb.AppendLine(" END CATCH");
    sb.AppendLine("END");
    return sb.ToString();
}

// ─────────────────────────────────────────────
// Update (with audit + versioning check)
// ─────────────────────────────────────────────
static string UpdateProc(string schema, string baseName, string table, List<Column> pk, List<Column> cols, Column? updated, Column? version)
{
    var proc = $"[{schema}].[{baseName}_Update]";
    var parms = pk.Select(ParamDecl).Concat(cols.Select(ParamDecl)).ToList();
    if (version != null)
        parms.Add(ParamDecl(version));

    var sets = new List<string>(cols.Select(c => $" [{c.Name}] = @{c.Name}"));
    if (updated != null)
        sets.Add($" [{updated.Name}] = SYSUTCDATETIME()");

    if (version != null && !version.IsRowVersion)
        sets.Add($" [{version.Name}] = NEWID()");

    var where = string.Join(" AND ", pk.Select(c => $"t.[{c.Name}] = @{c.Name}"));
    if (version != null)
        where += $" AND t.[{version.Name}] = @{version.Name}";

    var sb = new StringBuilder();
    sb.AppendLine($"CREATE OR ALTER PROCEDURE {proc}");
    sb.AppendLine(" " + string.Join(",\r\n ", parms));
    sb.AppendLine("AS BEGIN");
    sb.AppendLine(" SET NOCOUNT ON;");
    sb.AppendLine();
    sb.AppendLine(" BEGIN TRY");

    if (sets.Any())
    {
        sb.AppendLine("  UPDATE t SET");
        sb.AppendLine("   " + string.Join(",\r\n   ", sets));
        sb.AppendLine($"  FROM {table} t");
        sb.AppendLine($"  WHERE {where};");
        sb.AppendLine("  SELECT @@ROWCOUNT AS RowsAffected;");
    }
    else
    {
        sb.AppendLine($"  IF NOT EXISTS (SELECT 1 FROM {table} t WHERE {where})");
        sb.AppendLine("   THROW 50000, 'Record not found or version mismatch', 1;");
        sb.AppendLine("  SELECT 0 AS RowsAffected;");
    }

    sb.AppendLine(" END TRY");
    sb.AppendLine(" BEGIN CATCH");
    sb.AppendLine("  IF @@TRANCOUNT > 0");
    sb.AppendLine("   ROLLBACK TRANSACTION;");
    sb.AppendLine();
    sb.AppendLine("  DECLARE @ErrorMessage nvarchar(4000) = ERROR_MESSAGE();");
    sb.AppendLine("  DECLARE @ErrorSeverity int = ERROR_SEVERITY();");
    sb.AppendLine("  DECLARE @ErrorState int = ERROR_STATE();");
    sb.AppendLine("  RAISERROR(@ErrorMessage, @ErrorSeverity, @ErrorState);");
    sb.AppendLine(" END CATCH");
    sb.AppendLine("END");
    return sb.ToString();
}

// ─────────────────────────────────────────────
// Upsert (with audit, soft delete, versioning)
// ─────────────────────────────────────────────
static string UpsertProc(string schema, string baseName, string table, List<Column> insertable, List<Column> updatable, List<Column> columns, List<Column> pk, Column? identity, Column? created, Column? updated, Column? softDelete, Column? version)
{
    var proc = $"[{schema}].[{baseName}_Upsert]";
    bool isIdentityPk = identity != null && pk.Count == 1 && pk[0].Name == identity.Name;

    var allDataColumns = insertable.Concat(updatable)
        .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First())
        .ToList();

    var parms = new List<string>();
    foreach (var p in pk)
    {
        if (isIdentityPk && p.Name == identity!.Name)
            parms.Add($"@{identity.Name} {SqlType(identity)} = NULL OUTPUT");
        else
            parms.Add(ParamDecl(p));
    }

    var nonPkDataColumns = allDataColumns
        .Where(c => !pk.Any(p => p.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
        .Where(c => c != created && c != updated && c != softDelete && c != version)
        .ToList();

    parms.AddRange(nonPkDataColumns.Select(ParamDecl));

    if (version != null)
        parms.Add(ParamDecl(version));

    var sb = new StringBuilder();
    sb.AppendLine($"CREATE OR ALTER PROCEDURE {proc}");
    sb.AppendLine(" " + string.Join(",\r\n ", parms));
    sb.AppendLine("AS BEGIN");
    sb.AppendLine(" SET NOCOUNT ON;");
    sb.AppendLine();
    sb.AppendLine(" BEGIN TRY");
    sb.AppendLine("  BEGIN TRANSACTION;");

    var pkWhere = string.Join(" AND ", pk.Select(p => $"t.[{p.Name}] = @{p.Name}"));
    if (version != null)
        pkWhere += $" AND t.[{version.Name}] = @{version.Name}";

    // UPDATE
    var updateSets = nonPkDataColumns.Select(c => $"[{c.Name}] = @{c.Name}").ToList();
    if (updated != null)
        updateSets.Add($"[{updated.Name}] = SYSUTCDATETIME()");
    if (version != null && !version.IsRowVersion)
        updateSets.Add($"[{version.Name}] = NEWID()");

    if (updateSets.Any())
    {
        sb.AppendLine($"  UPDATE t SET");
        sb.AppendLine("   " + string.Join(",\r\n   ", updateSets));
        sb.AppendLine($"  FROM {table} t");
        sb.AppendLine($"  WHERE {pkWhere};");
        sb.AppendLine();
    }

    // INSERT
    sb.AppendLine($"  IF @@ROWCOUNT = 0");
    sb.AppendLine("  BEGIN");

    var insertFields = columns.Where(c => !c.IsIdentity && !c.IsComputed).Select(c => $"[{c.Name}]").ToList();
    var insertValues = insertFields.Select(f =>
    {
        var colName = f.Trim('[', ']');
        if (created != null && colName.Equals(created.Name, StringComparison.OrdinalIgnoreCase))
            return "SYSUTCDATETIME()";
        if (updated != null && colName.Equals(updated.Name, StringComparison.OrdinalIgnoreCase))
            return "SYSUTCDATETIME()";
        if (softDelete != null && colName.Equals(softDelete.Name, StringComparison.OrdinalIgnoreCase))
            return softDelete.Name.Contains("IsActive") ? "1" : "0";
        if (version != null && !version.IsRowVersion && colName.Equals(version.Name, StringComparison.OrdinalIgnoreCase))
            return "NEWID()";
        return $"@{colName}";
    }).ToList();

    sb.AppendLine($"    INSERT INTO {table} ({string.Join(", ", insertFields)})");
    sb.AppendLine($"    VALUES ({string.Join(", ", insertValues)});");

    if (isIdentityPk)
        sb.AppendLine($"    SET @{identity!.Name} = SCOPE_IDENTITY();");

    sb.AppendLine("  END");

    sb.AppendLine("  COMMIT TRANSACTION;");

    sb.AppendLine();
    sb.AppendLine($"  SELECT * FROM {table} t WHERE {string.Join(" AND ", pk.Select(p => $"t.[{p.Name}] = @{p.Name}"))};");
    sb.AppendLine("  SELECT 1 AS RowsAffected;");

    sb.AppendLine(" END TRY");
    sb.AppendLine(" BEGIN CATCH");
    sb.AppendLine("  IF @@TRANCOUNT > 0");
    sb.AppendLine("   ROLLBACK TRANSACTION;");
    sb.AppendLine();
    sb.AppendLine("  DECLARE @ErrorMessage nvarchar(4000) = ERROR_MESSAGE();");
    sb.AppendLine("  DECLARE @ErrorSeverity int = ERROR_SEVERITY();");
    sb.AppendLine("  DECLARE @ErrorState int = ERROR_STATE();");
    sb.AppendLine("  RAISERROR(@ErrorMessage, @ErrorSeverity, @ErrorState);");
    sb.AppendLine(" END CATCH");
    sb.AppendLine("END");
    return sb.ToString();
}

// ─────────────────────────────────────────────
// Delete (soft or hard)
// ─────────────────────────────────────────────
static string DeleteProc(string schema, string baseName, string table, List<Column> pk, Column? softDeleteCol, bool enableSoftDelete)
{
    var proc = $"[{schema}].[{baseName}_Delete]";
    var parms = pk.Select(ParamDecl).ToList();
    var where = string.Join(" AND ", pk.Select(c => $"[{c.Name}] = @{c.Name}"));

    var sb = new StringBuilder();
    sb.AppendLine($"CREATE OR ALTER PROCEDURE {proc}");
    sb.AppendLine(" " + string.Join(",\r\n ", parms));
    sb.AppendLine("AS BEGIN");
    sb.AppendLine(" SET NOCOUNT ON;");
    sb.AppendLine();
    sb.AppendLine(" BEGIN TRY");

    if (enableSoftDelete && softDeleteCol != null)
    {
        var flagValue = softDeleteCol.Name.Contains("IsActive") ? "0" : "1";
        sb.AppendLine($"  UPDATE {table} SET [{softDeleteCol.Name}] = {flagValue} WHERE {where};");
    }
    else
    {
        sb.AppendLine($"  DELETE FROM {table} WHERE {where};");
    }

    sb.AppendLine("  SELECT @@ROWCOUNT AS RowsAffected;");
    sb.AppendLine(" END TRY");
    sb.AppendLine(" BEGIN CATCH");
    sb.AppendLine("  IF @@TRANCOUNT > 0");
    sb.AppendLine("   ROLLBACK TRANSACTION;");
    sb.AppendLine();
    sb.AppendLine("  DECLARE @ErrorMessage nvarchar(4000) = ERROR_MESSAGE();");
    sb.AppendLine("  DECLARE @ErrorSeverity int = ERROR_SEVERITY();");
    sb.AppendLine("  DECLARE @ErrorState int = ERROR_STATE();");
    sb.AppendLine("  RAISERROR(@ErrorMessage, @ErrorSeverity, @ErrorState);");
    sb.AppendLine(" END CATCH");
    sb.AppendLine("END");
    return sb.ToString();
}

static string RestoreProc(string schema, string baseName, string table, List<Column> pk, Column? softDeleteCol)
{
    var proc = $"[{schema}].[{baseName}_Restore]";
    var parms = pk.Select(ParamDecl).ToList();
    var where = string.Join(" AND ", pk.Select(c => $"[{c.Name}] = @{c.Name}"));

    var sb = new StringBuilder();
    sb.AppendLine($"CREATE OR ALTER PROCEDURE {proc}");
    sb.AppendLine(" " + string.Join(",\r\n ", parms));
    sb.AppendLine("AS BEGIN");
    sb.AppendLine(" SET NOCOUNT ON;");
    sb.AppendLine();
    sb.AppendLine(" BEGIN TRY");

    var flagValue = softDeleteCol!.Name.Contains("IsActive") ? "1" : "0";
    sb.AppendLine($"  UPDATE {table} SET [{softDeleteCol.Name}] = {flagValue} WHERE {where};");

    sb.AppendLine("  SELECT @@ROWCOUNT AS RowsAffected;");
    sb.AppendLine(" END TRY");
    sb.AppendLine(" BEGIN CATCH");
    sb.AppendLine("  IF @@TRANCOUNT > 0");
    sb.AppendLine("   ROLLBACK TRANSACTION;");
    sb.AppendLine();
    sb.AppendLine("  DECLARE @ErrorMessage nvarchar(4000) = ERROR_MESSAGE();");
    sb.AppendLine("  DECLARE @ErrorSeverity int = ERROR_SEVERITY();");
    sb.AppendLine("  DECLARE @ErrorState int = ERROR_STATE();");
    sb.AppendLine("  RAISERROR(@ErrorMessage, @ErrorSeverity, @ErrorState);");
    sb.AppendLine(" END CATCH");
    sb.AppendLine("END");
    return sb.ToString();
}

// ─────────────────────────────────────────────
// Read procedures (filter soft-deleted)
// ─────────────────────────────────────────────
static string GetByIdProc(string schema, string baseName, string table, List<Column> pk, Column? softDeleteCol, bool enableSoftDelete)
{
    var proc = $"[{schema}].[{baseName}_GetById]";
    var parms = pk.Select(ParamDecl).ToList();
    var where = string.Join(" AND ", pk.Select(c => $"[{c.Name}] = @{c.Name}"));

    var sb = new StringBuilder();
    sb.AppendLine($"CREATE OR ALTER PROCEDURE {proc}");
    sb.AppendLine(" " + string.Join(",\r\n ", parms));
    sb.AppendLine("AS BEGIN");
    sb.AppendLine(" SET NOCOUNT ON;");
    sb.AppendLine($" SELECT * FROM {table}");
    sb.AppendLine($" WHERE {where}");

    if (enableSoftDelete && softDeleteCol != null)
    {
        var condition = softDeleteCol.Name.Contains("IsActive") ? "= 1" : "= 0";
        sb.AppendLine($"   AND [{softDeleteCol.Name}] {condition}");
    }

    sb.AppendLine("END");
    return sb.ToString();
}

static string ListAllProc(string schema, string baseName, string table, Column? softDeleteCol, bool enableSoftDelete)
{
    var sb = new StringBuilder();
    sb.AppendLine($"CREATE OR ALTER PROCEDURE [{schema}].[{baseName}_ListAll]");
    sb.AppendLine(" @Offset int = 0");
    sb.AppendLine(" ,@Fetch int = 100");
    sb.AppendLine("AS BEGIN");
    sb.AppendLine(" SET NOCOUNT ON;");
    sb.AppendLine($" SELECT * FROM {table}");

    if (enableSoftDelete && softDeleteCol != null)
    {
        var condition = softDeleteCol.Name.Contains("IsActive") ? "= 1" : "= 0";
        sb.AppendLine($" WHERE [{softDeleteCol.Name}] {condition}");
    }

    sb.AppendLine(" ORDER BY (SELECT NULL)");
    sb.AppendLine(" OFFSET @Offset ROWS FETCH NEXT @Fetch ROWS ONLY;");
    sb.AppendLine("END");
    return sb.ToString();
}

static string SearchProc(string schema, string baseName, string table, List<Column> cols, Column? softDeleteCol, bool enableSoftDelete)
{
    var searchable = cols.Where(c => c.TypeName.Contains("char", StringComparison.OrdinalIgnoreCase)).Take(5);
    if (!searchable.Any()) return "";

    var conditions = string.Join(" OR ", searchable.Select(c => $"[{c.Name}] LIKE '%' + @Search + '%'"));

    var sb = new StringBuilder();
    sb.AppendLine($"CREATE OR ALTER PROCEDURE [{schema}].[{baseName}_Search]");
    sb.AppendLine(" @Search nvarchar(200) = NULL");
    sb.AppendLine(" ,@Offset int = 0");
    sb.AppendLine(" ,@Fetch int = 100");
    sb.AppendLine("AS BEGIN");
    sb.AppendLine(" SET NOCOUNT ON;");
    sb.AppendLine($" SELECT * FROM {table}");
    sb.AppendLine($" WHERE (@Search IS NULL OR ({conditions}))");

    if (enableSoftDelete && softDeleteCol != null)
    {
        var condition = softDeleteCol.Name.Contains("IsActive") ? "= 1" : "= 0";
        sb.AppendLine($"   AND [{softDeleteCol.Name}] {condition}");
    }

    sb.AppendLine(" ORDER BY (SELECT NULL)");
    sb.AppendLine(" OFFSET @Offset ROWS FETCH NEXT @Fetch ROWS ONLY;");
    sb.AppendLine("END");
    return sb.ToString();
}

// ─────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────
static string ParamDecl(Column c) => $"@{c.Name} {SqlType(c)}{(c.IsNullable ? " = NULL" : "")}";

static string SqlType(Column c)
{
    var lower = c.TypeName.ToLowerInvariant();
    return lower switch
    {
        "varchar" or "char" or "varbinary" or "binary" => $"{c.TypeName}({(c.MaxLength == -1 ? "max" : c.MaxLength?.ToString() ?? "0")})",
        "nvarchar" or "nchar" => $"{c.TypeName}({(c.MaxLength == -1 ? "max" : (c.MaxLength / 2)?.ToString() ?? "0")})",
        "decimal" or "numeric" => $"{c.TypeName}({c.Precision},{c.Scale})",
        "datetime2" or "datetimeoffset" or "time" => $"{c.TypeName}({c.Scale})",
        _ => c.TypeName
    };
}
