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
  <Output>DataGrids</Output>
  <NuGetReference>Microsoft.EntityFrameworkCore</NuGetReference>
  <NuGetReference>Microsoft.EntityFrameworkCore.Design</NuGetReference>
  <NuGetReference>Microsoft.EntityFrameworkCore.SqlServer</NuGetReference>
  <NuGetReference>Microsoft.Extensions.DependencyInjection</NuGetReference>
  <NuGetReference>Microsoft.EntityFrameworkCore.Tools</NuGetReference>
  <Namespace>Microsoft.EntityFrameworkCore</Namespace>
  <Namespace>Microsoft.EntityFrameworkCore.Design</Namespace>
  <Namespace>Microsoft.EntityFrameworkCore.Infrastructure</Namespace>
  <Namespace>Microsoft.EntityFrameworkCore.Scaffolding</Namespace>
  <Namespace>Microsoft.EntityFrameworkCore.Scaffolding;  // For IReverseEngineerScaffolder, etc.</Namespace>
  <Namespace>Microsoft.EntityFrameworkCore.Scaffolding.Metadata</Namespace>
  <Namespace>Microsoft.EntityFrameworkCore.Scaffolding.Metadata;  // For DatabaseTable, TableType</Namespace>
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
	//const string connectionString = 
	//"Server=.;Database=YourDatabase;Trusted_Connection=True;TrustServerCertificate=True";

	var connectionString = this.Connection.ConnectionString;

	const string outputRootBase = @"C:\EFScaffoldOutput";
	const bool useTimestampedFolder = true;

	const string baseContextName = "MyDbContext";
	const bool pluralizeContextName = true;                      // e.g., MyDbContexts

	const string rootNamespace = "MyScaffold";
	const string contextNamespace = $"{rootNamespace}.Data";
	const string modelNamespace = $"{rootNamespace}.Models";
	const string viewsNamespace = $"{modelNamespace}.Views";

	// Filters
	string[] includeTablePatterns = Array.Empty<string>();
	string[] excludeTablePatterns = { "__EFMigrationsHistory" };
	string[] includeSchemaPatterns = Array.Empty<string>();
	string[] excludeSchemaPatterns = Array.Empty<string>();

	// Feature toggles
	const bool scaffoldViewsAsKeyless = true;
	const bool generateFluentApiStub = true;
	const bool pluralizeEntityNames = false;                     // Simple pluralization of class/file names

	// ──────────────────────────────── SETUP ────────────────────────────────
	string contextName = pluralizeContextName ? SimplePluralize(baseContextName) : baseContextName;

	string outputRoot = outputRootBase;
	if (useTimestampedFolder)
	{
		var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
		outputRoot = Path.Combine(outputRootBase, $"{contextName}_{timestamp}");
	}

	Directory.CreateDirectory(outputRoot);
	Console.WriteLine($"Output: {outputRoot}");
	Console.WriteLine("Starting enhanced EF Core 10 scaffolding...");

	using var tempContext = new DesignTimeContext(connectionString);
	if (!tempContext.Database.CanConnect())
	{
		"Connection failed!".Dump();
		return;
	}

	// Service provider
	var services = new ServiceCollection();
	services.AddDbContextDesignTimeServices(tempContext);

	try
	{
		string providerName = tempContext.GetService<IDatabaseProvider>().Name;
		if (string.IsNullOrWhiteSpace(providerName) || providerName.Contains("InMemory"))
			providerName = "Microsoft.EntityFrameworkCore.SqlServer";

		var asm = Assembly.Load(new AssemblyName(providerName));
		var attr = asm.GetCustomAttribute<DesignTimeProviderServicesAttribute>()
			?? throw new InvalidOperationException("Provider attribute missing.");
		var type = asm.GetType(attr.TypeName, throwOnError: true)!;
		var instance = Activator.CreateInstance(type)!;
		((IDesignTimeServices)instance).ConfigureDesignTimeServices(services);
	}
	catch (Exception ex)
	{
		$"Provider registration error: {ex.Message}".Dump();
		return;
	}

	services.AddEntityFrameworkDesignTimeServices();
	using var scope = services.BuildServiceProvider().CreateScope();
	var sp = scope.ServiceProvider;

	var scaffolder = sp.GetRequiredService<IReverseEngineerScaffolder>();
	var factory = sp.GetRequiredService<IDatabaseModelFactory>();

	var fullModel = factory.Create(connectionString, new DatabaseModelFactoryOptions());

	var tables = fullModel.Tables.Where(t => t.TableType == "TABLE").ToList();
	var views = fullModel.Tables.Where(t => t.TableType == "VIEW").ToList();

	var selectedTables = tables.Where(t => MatchesFilter(t)).Select(QualifiedName).ToArray();
	var selectedViews = scaffoldViewsAsKeyless
		? views.Where(t => MatchesFilter(t)).Select(QualifiedName).ToArray()
		: Array.Empty<string>();

	if (selectedTables.Length == 0 && selectedViews.Length == 0)
	{
		"No tables/views matched filters.".Dump();
		return;
	}

	// Scaffold tables
	var tableOptions = new DatabaseModelFactoryOptions(tables: selectedTables, schemas: Array.Empty<string>());
	var scaffoldedTables = scaffolder.ScaffoldModel(connectionString, tableOptions,
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

	// Scaffold views (if enabled)
	ScaffoldedModel? scaffoldedViews = null;
	if (scaffoldViewsAsKeyless && selectedViews.Length > 0)
	{
		var viewOptions = new DatabaseModelFactoryOptions(tables: selectedViews, schemas: Array.Empty<string>());
		scaffoldedViews = scaffolder.ScaffoldModel(connectionString, viewOptions,
			new ModelReverseEngineerOptions { UseDatabaseNames = false },
			new ModelCodeGenerationOptions
			{
				ContextName = contextName,
				ContextNamespace = contextNamespace,
				ModelNamespace = viewsNamespace,
				RootNamespace = rootNamespace,
				SuppressOnConfiguring = true,
				SuppressConnectionStringWarning = true,
				UseNullableReferenceTypes = true,
				UseDataAnnotations = false
			});
	}

	// Write files
	WriteFile(outputRoot, scaffoldedTables.ContextFile, "DbContext", makePartial: true);

	foreach (var f in scaffoldedTables.AdditionalFiles)
		WriteFile(outputRoot, f, "Entity (Table)", makePartial: true, pluralizeName: pluralizeEntityNames);

	if (scaffoldedViews != null)
	{
		foreach (var f in scaffoldedViews.AdditionalFiles)
			WriteFile(outputRoot, f, "Entity (View)", makePartial: true, pluralizeName: pluralizeEntityNames, isView: true);
	}

	GenerateFluentApiStub(outputRoot, contextNamespace, contextName, generateFluentApiStub, selectedViews);
	GenerateExtensionsFile(outputRoot, modelNamespace, rootNamespace);

	$"Scaffolding complete! Generated context: {contextName}".Dump();
	$"Output: {outputRoot}".Dump();
}

// ─────────────────────────────────────────────
//  HELPERS
// ─────────────────────────────────────────────
static string QualifiedName(DatabaseTable t) => $"{t.Schema ?? "dbo"}.{t.Name}";

static bool MatchesFilter(DatabaseTable t) =>
	MatchesPatterns(t.Name, includeTablePatterns, true) &&
	!MatchesPatterns(t.Name, excludeTablePatterns, false) &&
	MatchesPatterns(t.Schema ?? "dbo", includeSchemaPatterns, true) &&
	!MatchesPatterns(t.Schema ?? "dbo", excludeSchemaPatterns, false);

static void WriteFile(string root, ScaffoldedFile file, string kind, bool makePartial, bool pluralizeName = false, bool isView = false)
{
	var relative = file.Path ?? "Generated.cs";

	if (pluralizeName && (kind.Contains("Table") || kind.Contains("View")))
		relative = Regex.Replace(relative, @"([^\/\\]+)\.cs$", m => SimplePluralize(m.Groups[1].Value) + ".cs");

	var full = Path.Combine(root, relative);
	Directory.CreateDirectory(Path.GetDirectoryName(full)!);

	var code = file.Code;

	code = $@"// <auto-generated>
//   EF Core {GetEfCoreVersion()} scaffolding from LINQPad
//   Kind: {kind}
//   Generated: {DateTimeOffset.Now:u}
// </auto-generated>

" + code;

	if (makePartial)
		code = MakeClassPartial(code);

	if (isView)
		code = AddKeylessAttribute(code);

	code = CleanupGeneratedCode(code);

	File.WriteAllText(full, code);
	Console.WriteLine($"Wrote {kind}: {full}");
}

static string SimplePluralize(string s) =>
	s.EndsWith("y") ? s[..^1] + "ies" :
	s.EndsWith("s") || s.EndsWith("x") || s.EndsWith("ch") || s.EndsWith("sh") ? s + "es" :
	s + "s";

static string AddKeylessAttribute(string code)
{
	const string keyless = "using Microsoft.EntityFrameworkCore.Infrastructure;\r\n\r\n    [Keyless]\r\n    ";
	if (code.Contains("[Keyless]")) return code;
	return Regex.Replace(code, @"(public partial class \w+)", keyless + "$1", RegexOptions.Multiline);
}

static string MakeClassPartial(string code)
{
	var regex = new Regex(@"(\b(public|internal|private|protected)\s+)?\bclass\s+(\w+)", RegexOptions.Multiline);
	return regex.Replace(code, m =>
	{
		var access = m.Groups[2].Success ? m.Groups[2].Value + " " : "public ";
		return $"{access}partial class {m.Groups[3].Value}";
	}, 1);
}

static string CleanupGeneratedCode(string code)
{
	code = Regex.Replace(code, @"(\r?\n\s*){3,}", "\r\n\r\n");
	code = Regex.Replace(code, @"[ \t]+(\r?\n)", "$1");
	code = Regex.Replace(code, @"\sthis\.(\w+)\s*=\s*\1\s*;", " $1 = $1;");
	return code;
}

static void GenerateFluentApiStub(string root, string ns, string ctxName, bool generate, string[] viewNames)
{
	if (!generate) return;

	var dir = Path.Combine(root, "Data");
	Directory.CreateDirectory(dir);
	var path = Path.Combine(dir, $"{ctxName}.Customizations.cs");
	if (File.Exists(path)) return;

	var viewConfigs = viewNames.Length > 0
		? string.Join("\r\n            ", viewNames.Select(v => $"// modelBuilder.Entity<{Path.GetFileNameWithoutExtension(v)}>()\r\n            //     .HasNoKey()\r\n            //     .ToView(\"{v.Split('.').Last()}\");"))
		: "            // Add view configurations here";

	var code = $@"// Custom Fluent API configurations
// Safe to modify — not overwritten on re-scaffold

using Microsoft.EntityFrameworkCore;

namespace {ns}
{{
    public partial class {ctxName}
    {{
        partial void CustomizeModel(ModelBuilder modelBuilder)
        {{
            {viewConfigs}

            // Add indexes, relationships, etc.
            // modelBuilder.Entity<Customer>()
            //     .HasIndex(c => c.Email)
            //     .IsUnique();
        }}

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {{
            base.OnModelCreating(modelBuilder);
            CustomizeModel(modelBuilder);
        }}
    }}
}}
";

	File.WriteAllText(path, code);
	Console.WriteLine($"Wrote Fluent API stub: {path}");
}

static void GenerateExtensionsFile(string root, string ns, string rootNs)
{
	var dir = Path.Combine(root, ns.Split('.').LastOrDefault() ?? "Models");
	Directory.CreateDirectory(dir);
	var path = Path.Combine(dir, "EfScaffoldExtensions.cs");
	if (File.Exists(path)) return;

	var code = $@"// Custom extension methods for scaffolded entities
namespace {rootNs}.Extensions
{{
    public static class EfScaffoldExtensions
    {{
        // Add your extensions here
    }}
}}
";

	File.WriteAllText(path, code);
	Console.WriteLine($"Wrote extensions stub: {path}");
}

static string GetEfCoreVersion() => typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "Unknown";

static bool MatchesPatterns(string value, string[] patterns, bool defaultWhenEmpty)
{
	if (patterns is null || patterns.Length == 0) return defaultWhenEmpty;
	return patterns.Any(p => !string.IsNullOrWhiteSpace(p) && value.Contains(p, StringComparison.OrdinalIgnoreCase));
}

public class DesignTimeContext : DbContext
{
	private readonly string _cs;
	public DesignTimeContext(string cs) => _cs = cs;
	protected override void OnConfiguring(DbContextOptionsBuilder b) => b.UseSqlServer(_cs);
}