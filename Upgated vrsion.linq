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
  <NuGetReference>Microsoft.EntityFrameworkCore</NuGetReference>
  <NuGetReference>Microsoft.EntityFrameworkCore.Design</NuGetReference>
  <NuGetReference>Microsoft.EntityFrameworkCore.SqlServer</NuGetReference>
  <NuGetReference>Microsoft.Extensions.DependencyInjection</NuGetReference>
  <Namespace>Microsoft.EntityFrameworkCore</Namespace>
  <Namespace>Microsoft.EntityFrameworkCore.Design</Namespace>
  <Namespace>Microsoft.EntityFrameworkCore.Infrastructure</Namespace>
  <Namespace>Microsoft.EntityFrameworkCore.Scaffolding</Namespace>
  <Namespace>Microsoft.EntityFrameworkCore.Scaffolding.Metadata</Namespace>
  <Namespace>Microsoft.EntityFrameworkCore.Storage</Namespace>
  <Namespace>Microsoft.Extensions.DependencyInjection</Namespace>
</Query>

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;
using Microsoft.Extensions.DependencyInjection;

// ─────────────────────────────────────────────
//  MAIN
// ─────────────────────────────────────────────
void Main()
{
	// ──────────────────────────────── CONFIGURATION ────────────────────────────────
	//const string connectionString = 
		//"Server=.;Database=YourDatabase;Trusted_Connection=True;TrustServerCertificate=True";

	var connectionString = this.Connection.ConnectionString;

	const string outputRootBase = @"C:\EFScaffoldOutput";
	const bool useTimestampedFolder = true;  // false = always overwrite base folder

	const string contextName = "MyDbContext";
	const string rootNamespace = "MyScaffold";
	const string contextNamespace = $"{rootNamespace}.Data";
	const string modelNamespace = $"{rootNamespace}.Models";

	// Simple substring filters (case-insensitive contains)
	string[] includeTablePatterns = Array.Empty<string>();                 // e.g. { "Customer", "Order" }
	string[] excludeTablePatterns = { "__EFMigrationsHistory" };           // common exclusions
	string[] includeSchemaPatterns = Array.Empty<string>();                 // e.g. { "dbo", "sales" }
	string[] excludeSchemaPatterns = Array.Empty<string>();                 // e.g. { "sys" }

	// ──────────────────────────────── OUTPUT SETUP ────────────────────────────────
	string outputRoot = outputRootBase;
	if (useTimestampedFolder)
	{
		var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
		outputRoot = Path.Combine(outputRootBase, $"{contextName}_{timestamp}");
	}

	Directory.CreateDirectory(outputRoot);
	Console.WriteLine($"Output directory: {outputRoot}");
	Console.WriteLine("Starting EF Core 10 scaffolding...");

	// 1. Minimal DbContext for provider discovery
	using var tempContext = new DesignTimeContext(connectionString);

	if (!tempContext.Database.CanConnect())
	{
		"Connection failed! Check your connection string.".Dump();
		return;
	}
	"Connection successful.".Dump();

	// 2. Build design-time service provider
	var services = new ServiceCollection();
	services.AddDbContextDesignTimeServices(tempContext);

	// Register provider design-time services with fallback & good errors
	try
	{
		string providerAssemblyName = tempContext.GetService<IDatabaseProvider>().Name;

		// Fallback for LINQPad quirks or InMemory testing
		if (string.IsNullOrWhiteSpace(providerAssemblyName) || providerAssemblyName.Contains("InMemory"))
			providerAssemblyName = "Microsoft.EntityFrameworkCore.SqlServer";

		var providerAssembly = Assembly.Load(new AssemblyName(providerAssemblyName));

		var attr = providerAssembly.GetCustomAttribute<DesignTimeProviderServicesAttribute>()
			?? throw new InvalidOperationException(
				$"DesignTimeProviderServicesAttribute not found in '{providerAssemblyName}'. " +
				"Ensure Microsoft.EntityFrameworkCore.SqlServer (10.0.x) is referenced.");

		var designTimeServicesType = providerAssembly.GetType(attr.TypeName, throwOnError: true)
			?? throw new InvalidOperationException($"Type '{attr.TypeName}' not found.");

		var instance = Activator.CreateInstance(designTimeServicesType)
			?? throw new InvalidOperationException("Failed to create design-time services instance.");

		((IDesignTimeServices)instance).ConfigureDesignTimeServices(services);

		"Provider design-time services registered successfully.".Dump();
	}
	catch (Exception ex)
	{
		$"ERROR: Failed to register provider design-time services: {ex.Message}".Dump();
		ex.StackTrace?.Dump();
		"Make sure all EF Core 10 packages are the same version and Microsoft.EntityFrameworkCore.SqlServer is loaded.".Dump();
		return;
	}

	services.AddEntityFrameworkDesignTimeServices();

	using var scope = services.BuildServiceProvider().CreateScope();
	var serviceProvider = scope.ServiceProvider;

	// 3. Get scaffolder and factory
	var scaffolder = serviceProvider.GetRequiredService<IReverseEngineerScaffolder>();
	var dbModelFactory = serviceProvider.GetRequiredService<IDatabaseModelFactory>();

	// 4. Discover full schema
	var fullModel = dbModelFactory.Create(connectionString, new DatabaseModelFactoryOptions());
	var allTables = fullModel.Tables
		.Select(t => new
		{
			Schema = t.Schema ?? "dbo",
			t.Name,
			Qualified = $"{t.Schema ?? "dbo"}.{t.Name}",
			Cols = t.Columns.Count
		})
		.OrderBy(t => t.Schema)
		.ThenBy(t => t.Name)
		.ToList();

	allTables.Dump("Full Database Schema");
	Console.WriteLine($"Discovered {allTables.Count} tables");

	// 5. Filter tables
	var filteredTables = allTables
		.Where(t =>
			MatchesPatterns(t.Name, includeTablePatterns, true) &&
			!MatchesPatterns(t.Name, excludeTablePatterns, false) &&
			MatchesPatterns(t.Schema, includeSchemaPatterns, true) &&
			!MatchesPatterns(t.Schema, excludeSchemaPatterns, false))
		.ToList();

	var tablesToScaffold = filteredTables.Select(t => t.Qualified).ToArray();

	if (tablesToScaffold.Length == 0)
	{
		"No tables matched the filters — nothing to scaffold.".Dump();
		return;
	}

	tablesToScaffold.Dump("Tables to Scaffold");
	Console.WriteLine($"Scaffolding {tablesToScaffold.Length} table(s)");

	var dbOptions = new DatabaseModelFactoryOptions(tables: tablesToScaffold, schemas: Array.Empty<string>());

	// 6. Scaffold
	var scaffolded = scaffolder.ScaffoldModel(
		connectionString,
		dbOptions,
		new ModelReverseEngineerOptions { UseDatabaseNames = false },
		new ModelCodeGenerationOptions
		{
			ContextName = contextName,
			ContextNamespace = contextNamespace,
			ModelNamespace = modelNamespace,
			RootNamespace = rootNamespace,
			SuppressOnConfiguring = true,
			SuppressConnectionStringWarning = true,
			UseNullableReferenceTypes = true,
			UseDataAnnotations = false
		});

	// 7. Write files
	WriteFile(outputRoot, scaffolded.ContextFile, "DbContext", makePartial: false);
	foreach (var file in scaffolded.AdditionalFiles)
		WriteFile(outputRoot, file, "Entity", makePartial: true);

	GenerateExtensionsFile(outputRoot, modelNamespace, rootNamespace);

	"Scaffolding complete!".Dump();
	$"Generated {scaffolded.AdditionalFiles.Count + 1} files in: {outputRoot}".Dump();
}

// ─────────────────────────────────────────────
//  DESIGN-TIME CONTEXT
// ─────────────────────────────────────────────
public class DesignTimeContext : DbContext
{
	private readonly string _cs;
	public DesignTimeContext(string cs) => _cs = cs;
	protected override void OnConfiguring(DbContextOptionsBuilder b) => b.UseSqlServer(_cs);
}

// ─────────────────────────────────────────────
//  FILE WRITING & CLEANUP
// ─────────────────────────────────────────────
static void WriteFile(string root, ScaffoldedFile file, string kind, bool makePartial)
{
	var relative = string.IsNullOrWhiteSpace(file.Path) ? "Generated.cs" : file.Path;
	var full = Path.Combine(root, relative);
	Directory.CreateDirectory(Path.GetDirectoryName(full)!);

	var code = file.Code;

	// Banner
	code = $@"// <auto-generated>
//   EF Core {GetEfCoreVersion()} scaffolding from LINQPad
//   Kind: {kind}
//   Generated: {DateTimeOffset.Now:u}
// </auto-generated>

" + code;

	// Make partial (robust)
	if (makePartial)
		code = MakeClassPartial(code);

	// Clean formatting
	code = CleanupGeneratedCode(code);

	File.WriteAllText(full, code);
	Console.WriteLine($"Wrote ({kind}): {full}");
}

static string GetEfCoreVersion() =>
	typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "Unknown";

static string MakeClassPartial(string code)
{
	var regex = new Regex(@"(\b(public|internal|private|protected)\s+)?\bclass\s+(\w+)", RegexOptions.Multiline);
	return regex.Replace(code, m =>
	{
		var access = m.Groups[2].Success ? m.Groups[2].Value + " " : "public ";
		var name = m.Groups[3].Value;
		return $"{access}partial class {name}";
	}, 1);
}

static string CleanupGeneratedCode(string code)
{
	// Collapse multiple blank lines
	code = Regex.Replace(code, @"(\r?\n\s*){3,}", "\r\n\r\n");

	// Trim trailing whitespace
	code = Regex.Replace(code, @"[ \t]+(\r?\n)", "$1");

	// Remove redundant "this." in property setters
	code = Regex.Replace(code, @"\sthis\.(\w+)\s*=\s*\1\s*;", " $1 = $1;");

	return code;
}

// ─────────────────────────────────────────────
//  EXTENSIONS STUB
// ─────────────────────────────────────────────
static void GenerateExtensionsFile(string outputRoot, string modelNamespace, string rootNamespace)
{
	var folder = modelNamespace.Split('.').LastOrDefault() ?? "Models";
	var dir = Path.Combine(outputRoot, folder);
	Directory.CreateDirectory(dir);

	var path = Path.Combine(dir, "EfScaffoldExtensions.cs");
	if (File.Exists(path)) return;

	var code = $@"// <auto-generated>
//   Custom extension methods for scaffolded entities
//   Generated: {DateTimeOffset.Now:u}
//   Feel free to edit or delete this file
// </auto-generated>

namespace {rootNamespace}.Extensions
{{
    public static class EfScaffoldExtensions
    {{
        // Example:
        // public static IQueryable<T> Active<T>(this IQueryable<T> query) where T : IHasIsActive
        //     => query.Where(e => e.IsActive);

        // Add your extensions here!
    }}
}}
";

	File.WriteAllText(path, code);
	Console.WriteLine($"Wrote extension stub: {path}");
}

// ─────────────────────────────────────────────
//  FILTER HELPERS
// ─────────────────────────────────────────────
static bool MatchesPatterns(string value, string[] patterns, bool defaultWhenEmpty)
{
	if (patterns is null || patterns.Length == 0) return defaultWhenEmpty;
	return patterns.Any(p => !string.IsNullOrWhiteSpace(p) && value.Contains(p, StringComparison.OrdinalIgnoreCase));
}