<Query Kind="Program">
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



// ─────────────────────────────────────────────
//  MAIN
// ─────────────────────────────────────────────
void Main()
{
	// ──────────────────────────────── CONFIGURATION ────────────────────────────────
	//const string connectionString = @"Data Source=localhost\sqlexpress;Integrated Security=SSPI;Encrypt=false;app=LINQPad";
	//	"Server=.;Database=YourDatabase;Trusted_Connection=True;TrustServerCertificate=True";

	const string connectionString =
		"Server=localhost\\sqlexpress;Database=BlazorBlogDb;Trusted_Connection=True;TrustServerCertificate=True";

	const string outputRootBase = @"C:\EFScaffoldOutput";
	const bool useTimestampedFolder = true;  // False to always overwrite the base folder

	const string contextName = "MyDbContext";
	const string rootNamespace = "MyScaffold";
	const string contextNamespace = $"{rootNamespace}.Data";
	const string modelNamespace = $"{rootNamespace}.Models";

	// Filters (substring contains, case-insensitive)
	string[] includeTablePatterns = Array.Empty<string>();
	string[] excludeTablePatterns = { "__EFMigrationsHistory" };
	string[] includeSchemaPatterns = Array.Empty<string>();
	string[] excludeSchemaPatterns = Array.Empty<string>();

	// ──────────────────────────────── SETUP OUTPUT EARLY ────────────────────────────────
	string outputRoot = outputRootBase;
	if (useTimestampedFolder)
	{
		var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
		outputRoot = Path.Combine(outputRootBase, $"{contextName}_{timestamp}");
	}

	Directory.CreateDirectory(outputRoot);  // Always create upfront
	Console.WriteLine($"Output directory: {outputRoot}");

	Console.WriteLine("Starting EF Core 10 scaffolding...");

	// 1. Minimal DbContext
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

	// Dynamically register provider design-time services (with fallback + better errors)
	try
	{
		string providerAssemblyName = tempContext.GetService<IDatabaseProvider>().Name;  // e.g. Microsoft.EntityFrameworkCore.SqlServer

		// Fallback if dynamic name causes issues (common in some LINQPad/EF10 setups)
		if (string.IsNullOrWhiteSpace(providerAssemblyName) || providerAssemblyName.Contains("InMemory"))
			providerAssemblyName = "Microsoft.EntityFrameworkCore.SqlServer";

		var providerAssembly = Assembly.Load(new AssemblyName(providerAssemblyName));

		var attr = providerAssembly.GetCustomAttribute<DesignTimeProviderServicesAttribute>()
			?? throw new InvalidOperationException($"DesignTimeProviderServicesAttribute not found in '{providerAssemblyName}'. Ensure Microsoft.EntityFrameworkCore.SqlServer (10.0.x) is referenced.");

		var designTimeServicesType = providerAssembly.GetType(attr.TypeName, throwOnError: true)
			?? throw new InvalidOperationException($"Design-time type '{attr.TypeName}' not found.");

		var instance = Activator.CreateInstance(designTimeServicesType)
			?? throw new InvalidOperationException("Failed to instantiate design-time services.");

		((IDesignTimeServices)instance).ConfigureDesignTimeServices(services);

		"Provider design-time services registered successfully.".Dump();
	}
	catch (Exception ex)
	{
		$"ERROR registering provider services: {ex.Message}\n{ex.StackTrace}".Dump();
		"Try hardcoding providerAssemblyName = \"Microsoft.EntityFrameworkCore.SqlServer\" or check NuGet versions.".Dump();
		return;
	}

	services.AddEntityFrameworkDesignTimeServices();

	using var scope = services.BuildServiceProvider().CreateScope();
	var serviceProvider = scope.ServiceProvider;

	// 3-9. Rest of scaffolding (unchanged from previous robust version)
	var scaffolder = serviceProvider.GetRequiredService<IReverseEngineerScaffolder>();
	var dbModelFactory = serviceProvider.GetRequiredService<IDatabaseModelFactory>();

	var fullModel = dbModelFactory.Create(connectionString, new DatabaseModelFactoryOptions());
	var allTables = fullModel.Tables
		.Select(t => new
		{
			Schema = t.Schema ?? "dbo",
			t.Name,
			Qualified = $"{(t.Schema ?? "dbo")}.{t.Name}",
			Cols = t.Columns.Count
		})
		.OrderBy(t => t.Schema)
		.ThenBy(t => t.Name)
		.ToList();

	allTables.Dump("Full Database Schema");
	Console.WriteLine($"Discovered {allTables.Count} tables");

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
		"No tables matched filters.".Dump();
		return;
	}

	tablesToScaffold.Dump("Tables to Scaffold");
	Console.WriteLine($"Scaffolding {tablesToScaffold.Length} tables");

	var dbOptions = new DatabaseModelFactoryOptions(tables: tablesToScaffold, schemas: Array.Empty<string>());

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

	WriteFile(outputRoot, scaffolded.ContextFile, "DbContext", makePartial: false);
	foreach (var file in scaffolded.AdditionalFiles)
		WriteFile(outputRoot, file, "Entity", makePartial: true);

	GenerateExtensionsFile(outputRoot, modelNamespace, rootNamespace);

	"Scaffolding complete!".Dump();
	$"Generated {scaffolded.AdditionalFiles.Count + 1} files in: {outputRoot}".Dump();
}

// ─────────────────────────────────────────────
//  MINIMAL DESIGN-TIME CONTEXT
// ─────────────────────────────────────────────
public class DesignTimeContext : DbContext
{
	private readonly string _cs;
	public DesignTimeContext(string cs) => _cs = cs;

	protected override void OnConfiguring(DbContextOptionsBuilder b)
		=> b.UseSqlServer(_cs);
}

// ─────────────────────────────────────────────
//  FILE WRITER + CODE CLEANUP
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

	// Make partial (robust: inserts after attributes and comments)
	if (makePartial)
		code = MakeClassPartial(code);

	// Clean up formatting
	code = CleanupGeneratedCode(code);

	File.WriteAllText(full, code);
	Console.WriteLine($"Wrote ({kind}): {full}");
}

static string GetEfCoreVersion()
{
	return typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "Unknown";
}

static string MakeClassPartial(string code)
{
	// Match: [attributes] [accessibility] class Name
	var regex = new Regex(@"(\bpublic\s+|\binternal\s+)?\bclass\s+(\w+)", RegexOptions.Multiline);
	return regex.Replace(code, match =>
	{
		var accessibility = match.Groups[1].Success ? match.Groups[1].Value : "public ";
		var className = match.Groups[2].Value;
		return $"{accessibility}partial class {className}";
	}, 1); // Only replace first occurrence
}

static string CleanupGeneratedCode(string code)
{
	// Collapse 3+ blank lines → 2
	code = Regex.Replace(code, @"(\r?\n\s*){3,}", "\r\n\r\n");

	// Trim trailing whitespace
	code = Regex.Replace(code, @"[ \t]+(\r?\n)", "$1");

	// Optional: remove redundant "this." on property setters (EF sometimes adds)
	code = Regex.Replace(code, @"\sthis\.(\w+)\s*=\s*\1\s*;", " $1 = $1;");

	return code;
}

// ─────────────────────────────────────────────
//  EXTENSIONS STUB
// ─────────────────────────────────────────────
static void GenerateExtensionsFile(string outputRoot, string modelNamespace, string rootNamespace)
{
	var folderSegment = modelNamespace.Split('.').LastOrDefault() ?? "Models";
	var dir = Path.Combine(outputRoot, folderSegment);
	Directory.CreateDirectory(dir);

	var path = Path.Combine(dir, "EfScaffoldExtensions.cs");

	if (File.Exists(path)) return; // Preserve user edits

	var code = $@"// <auto-generated>
//   Custom extension methods for scaffolded entities
//   Generated: {DateTimeOffset.Now:u}
//   Feel free to modify or delete this file
// </auto-generated>

namespace {rootNamespace}.Extensions
{{
    public static class EfScaffoldExtensions
    {{
        // Example: Active records
        // public static IQueryable<T> Active<T>(this IQueryable<T> query) where T : IHasIsActive
        //     => query.Where(e => e.IsActive);

        // Add your own extensions here!
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
	if (patterns is null || patterns.Length == 0)
		return defaultWhenEmpty;

	return patterns.Any(p =>
		!string.IsNullOrWhiteSpace(p) &&
		value.Contains(p, StringComparison.OrdinalIgnoreCase));
}