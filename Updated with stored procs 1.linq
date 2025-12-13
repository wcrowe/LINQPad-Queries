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
  <Namespace>Microsoft.EntityFrameworkCore.Metadata</Namespace>
  <Namespace>Microsoft.EntityFrameworkCore.Scaffolding</Namespace>
  <Namespace>Microsoft.EntityFrameworkCore.Scaffolding;  // For IReverseEngineerScaffolder, etc.</Namespace>
  <Namespace>Microsoft.EntityFrameworkCore.Scaffolding.Metadata</Namespace>
  <Namespace>Microsoft.EntityFrameworkCore.Scaffolding.Metadata;  // For DatabaseTable, TableType</Namespace>
  <Namespace>Microsoft.EntityFrameworkCore.SqlServer</Namespace>
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
	// LINQPad connection string
	var connectionString = this.Connection.ConnectionString;

	const string outputRootBase = @"C:\EFScaffoldOutput";
	const bool useTimestampedFolder = true;

	const string baseContextName = "MyDbContext";
	const bool pluralizeContextName = true;

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
	const bool pluralizeEntityNames = false;

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

	using var tempContext = new DesignTimeContext(connectionString);
	if (!tempContext.Database.CanConnect())
	{
		"Connection failed!".Dump();
		return;
	}

	// ──────────────────────────────── DESIGN-TIME SERVICES ────────────────────────────────
	var services = new ServiceCollection();
	services.AddDbContextDesignTimeServices(tempContext);

	string providerName = tempContext.GetService<IDatabaseProvider>().Name;
	if (string.IsNullOrWhiteSpace(providerName) || providerName.Contains("InMemory"))
		providerName = "Microsoft.EntityFrameworkCore.SqlServer";

	var asm = Assembly.Load(new AssemblyName(providerName));
	var attr = asm.GetCustomAttribute<DesignTimeProviderServicesAttribute>()
		?? throw new InvalidOperationException("Provider attribute missing.");

	var designType = asm.GetType(attr.TypeName, throwOnError: true)!;
	((IDesignTimeServices)Activator.CreateInstance(designType)!)
		.ConfigureDesignTimeServices(services);

	services.AddEntityFrameworkDesignTimeServices();

	using var scope = services.BuildServiceProvider().CreateScope();
	var sp = scope.ServiceProvider;

	var scaffolder = sp.GetRequiredService<IReverseEngineerScaffolder>();
	var factory = sp.GetRequiredService<IDatabaseModelFactory>();

	var fullModel = factory.Create(connectionString, new DatabaseModelFactoryOptions());

	// EF Core 10: Tables includes views; DatabaseView derives from DatabaseTable
	var allTables = fullModel.Tables.ToList();
	var tables = allTables.Where(t => t is not DatabaseView).ToList();
	var views = allTables.OfType<DatabaseView>().ToList();

	var selectedTables = tables
		.Where(t => MatchesFilter(t,
			includeTablePatterns,
			excludeTablePatterns,
			includeSchemaPatterns,
			excludeSchemaPatterns))
		.Select(QualifiedName)
		.ToArray();

	var selectedViews = scaffoldViewsAsKeyless
		? views
			.Where(v => MatchesFilter(v,
				includeTablePatterns,
				excludeTablePatterns,
				includeSchemaPatterns,
				excludeSchemaPatterns))
			.Select(QualifiedName)
			.ToArray()
		: Array.Empty<string>();

	if (selectedTables.Length == 0 && selectedViews.Length == 0)
	{
		"No tables/views matched filters.".Dump();
		return;
	}

	// ──────────────────────────────── SCAFFOLD TABLES ────────────────────────────────
	var tableOptions = new DatabaseModelFactoryOptions(selectedTables, Array.Empty<string>());

	var scaffoldedTables = scaffolder.ScaffoldModel(
		connectionString,
		tableOptions,
		new ModelReverseEngineerOptions(),
		new ModelCodeGenerationOptions
		{
			ContextName = contextName,
			ContextNamespace = contextNamespace,
			ModelNamespace = modelNamespace,
			RootNamespace = rootNamespace,
			UseNullableReferenceTypes = true,
			UseDataAnnotations = false,
			SuppressOnConfiguring = true
		});

	// ──────────────────────────────── SCAFFOLD VIEWS ────────────────────────────────
	ScaffoldedModel? scaffoldedViews = null;

	if (scaffoldViewsAsKeyless && selectedViews.Length > 0)
	{
		var viewOptions = new DatabaseModelFactoryOptions(selectedViews, Array.Empty<string>());

		scaffoldedViews = scaffolder.ScaffoldModel(
			connectionString,
			viewOptions,
			new ModelReverseEngineerOptions(),
			new ModelCodeGenerationOptions
			{
				ContextName = contextName,
				ContextNamespace = contextNamespace,
				ModelNamespace = viewsNamespace,
				RootNamespace = rootNamespace,
				UseNullableReferenceTypes = true,
				UseDataAnnotations = false,
				SuppressOnConfiguring = true
			});
	}

	// ──────────────────────────────── WRITE FILES ────────────────────────────────
	WriteFile(outputRoot, scaffoldedTables.ContextFile, "DbContext", true);

	foreach (var f in scaffoldedTables.AdditionalFiles)
		WriteFile(outputRoot, f, "Entity (Table)", true, pluralizeEntityNames);

	if (scaffoldedViews != null)
		foreach (var f in scaffoldedViews.AdditionalFiles)
			WriteFile(outputRoot, f, "Entity (View)", true, pluralizeEntityNames, isView: true);

	GenerateFluentApiStub(outputRoot, contextNamespace, contextName, generateFluentApiStub, selectedViews);
	GenerateExtensionsFile(outputRoot, modelNamespace, rootNamespace);

	$"Scaffolding complete: {contextName}".Dump();
}

// ─────────────────────────────────────────────
//  HELPERS
// ─────────────────────────────────────────────

static string QualifiedName(DatabaseTable t) => $"{t.Schema ?? "dbo"}.{t.Name}";

static bool MatchesFilter(
	DatabaseTable t,
	string[] includeTablePatterns,
	string[] excludeTablePatterns,
	string[] includeSchemaPatterns,
	string[] excludeSchemaPatterns)
{
	return
		MatchesPatterns(t.Name, includeTablePatterns, true) &&
		!MatchesPatterns(t.Name, excludeTablePatterns, false) &&
		MatchesPatterns(t.Schema ?? "dbo", includeSchemaPatterns, true) &&
		!MatchesPatterns(t.Schema ?? "dbo", excludeSchemaPatterns, false);
}

static void WriteFile(string root, ScaffoldedFile file, string kind, bool makePartial, bool pluralizeName = false, bool isView = false)
{
	var relative = file.Path ?? "Generated.cs";
	var full = Path.Combine(root, relative);
	Directory.CreateDirectory(Path.GetDirectoryName(full)!);

	var code = file.Code;

	if (makePartial)
	{
		var rx = new Regex(@"\bclass\s+(\w+)", RegexOptions.Multiline);
		code = rx.Replace(code, "partial class $1", 1); // replace first class only
	}

	//if (isView && !code.Contains("[Keyless]"))
	//	code = "using Microsoft.EntityFrameworkCore;\r\n\r\n[Keyless]\r\n" + code;

	if (isView)
	{
		if (!code.Contains("using Microsoft.EntityFrameworkCore;"))
			code = "using Microsoft.EntityFrameworkCore;\r\n" + code;

		if (!code.Contains("[Keyless]"))
		{
			var rx = new Regex(@"(public partial class \w+)");
			code = rx.Replace(code, "[Keyless]\r\n    $1", 1);
		}
	}

	File.WriteAllText(full, code);
}


static string SimplePluralize(string s) =>
	s.EndsWith("y") ? s[..^1] + "ies" :
	s.EndsWith("s") ? s + "es" : s + "s";

static bool MatchesPatterns(string value, string[] patterns, bool defaultWhenEmpty)
{
	if (patterns.Length == 0) return defaultWhenEmpty;
	return patterns.Any(p => value.Contains(p, StringComparison.OrdinalIgnoreCase));
}

static void GenerateFluentApiStub(string root, string ns, string ctx, bool generate, string[] views)
{
	if (!generate) return;

	var path = Path.Combine(root, "Data", $"{ctx}.Customizations.cs");
	if (File.Exists(path)) return;

	Directory.CreateDirectory(Path.GetDirectoryName(path)!);

	File.WriteAllText(path, $@"
using Microsoft.EntityFrameworkCore;

namespace {ns}
{{
    public partial class {ctx}
    {{
        partial void CustomizeModel(ModelBuilder modelBuilder)
        {{
            // View configuration hooks
        }}
    }}
}}
");
}

static void GenerateExtensionsFile(string root, string ns, string rootNs)
{
	var path = Path.Combine(root, "Models", "EfScaffoldExtensions.cs");
	if (File.Exists(path)) return;

	Directory.CreateDirectory(Path.GetDirectoryName(path)!);

	File.WriteAllText(path, $@"
namespace {rootNs}.Extensions
{{
    public static class EfScaffoldExtensions {{ }}
}}
");
}

public class DesignTimeContext : DbContext
{
	private readonly string _cs;
	public DesignTimeContext(string cs) => _cs = cs;
	protected override void OnConfiguring(DbContextOptionsBuilder b) => b.UseSqlServer(_cs);
}
