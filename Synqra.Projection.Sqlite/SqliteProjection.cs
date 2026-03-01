using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Synqra.AppendStorage;
using Synqra.BinarySerializer;
using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra.Projection.Sqlite;

using IAppendStorage = IAppendStorage<Event, Guid>;

internal static class SynqraStoreContextInternalExtensions
{
	/*
	internal static Guid GetId(this IObjectStore ctx, object model, ISynqraCollection? collection, GetMode mode)
	{
		return ctx.GetId(model, collection, mode);
	}

	internal static AttachedObjectData Attach(this IObjectStore ctx, object model, ISynqraCollection collection)
	{
		return ctx.Attach(model, collection);
	}

	internal static (bool IsJustCreated, Guid Id) GetOrCreateId(this IObjectStore ctx, object model, ISynqraCollection collection)
	{
		return ctx.GetOrCreateId(model, collection);
	}
	*/
}

public static class SqliteNativeAotMigrations
{
	public static void MigrateNative(this DatabaseFacade database)
	{
		var assembly = typeof(SqliteNativeAotMigrations).Assembly;
		var stream = assembly.GetManifestResourceStream("Synqra.Projection.Sqlite.MigrationScripts.Latest.sql_");
		if (stream == null)
		{
			throw new Exception($"Available resource names: {Environment.NewLine}{string.Join(Environment.NewLine, assembly.GetManifestResourceNames())}");
		}
		using (stream)
		{
			using var reader = new StreamReader(stream);
			var sqlString = reader.ReadToEnd();

			var stages = sqlString.Split("COMMIT;").Select(x => x + "COMMIT;");

			foreach (var item in stages)
			{
				EmergencyLog.Default.LogWarning("Executing migration stage: " + item.Substring(0, Math.Min(100, item.Length)).ReplaceLineEndings(" "));
				// EmergencyLog.Default.LogWarning("Executing migration stage: " + item);
			}

			database.ExecuteSqlRaw(sqlString);
		}
	}
}

public class SqliteDatabaseContext : DbContext
{
	internal string _connectionString;
	internal ILogger<SqliteDatabaseContext>? _logger;

	// public DbSet<Command> Command { get; set; }
	// public DbSet<CreateObjectCommand> CreateObjectCommand { get; set; }

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		// todo Use JsonIgnore attribute, and use it in SBX as well
		/*
		modelBuilder.Entity<CreateObjectCommand>(b =>
		{
			b.Ignore("Data");
			b.Ignore("Target");
		});
		*/
		EmergencyLog.Default.LogWarning("SqliteDatabaseContext OnModelCreating Done");
	}

	public SqliteDatabaseContext()
	{
		/*
		foreach (var item in Environment.GetCommandLineArgs())
		{
			EmergencyLog.Default.LogWarning(item);
		}

		// Migrations
		var tempMigrationsFile = Path.Combine(Path.GetTempPath(), "migrations.db");

		// This trick allows to use normal migrations and also generate non-idempotent script for EF. Each run will be based on previous schema, so script needs to be reliable migrated
		if (Environment.GetCommandLineArgs().Any(x=>x == "script"))
		{
			tempMigrationsFile = Path.Combine(Path.GetTempPath(), "migrations_script.db");
		}

		if (File.Exists(tempMigrationsFile))
		{
			try
			{
				File.Delete(tempMigrationsFile);
				EmergencyLog.Default.LogWarning("SqliteDatabaseContext Temp deleted successfully: " + tempMigrationsFile);
			}
			catch (Exception ex)
			{
				tempMigrationsFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N)}_migrations.db");
				EmergencyLog.Default.LogWarning(ex, "SqliteDatabaseContext Temp delete failed. Changing to: " + tempMigrationsFile);
			}
		}
		else
		{
			EmergencyLog.Default.LogWarning("Default temp migrations database file does not exist: " + tempMigrationsFile);
		}
		*/
		_connectionString = "Data Source=:memory:" /*+ tempMigrationsFile*/;
		EmergencyLog.Default.LogWarning("SqliteDatabaseContext DefaultCtor + " + _connectionString);
	}


	public SqliteDatabaseContext(
	  DbContextOptions<SqliteDatabaseContext> options
	, IConfiguration configuration
	, ILogger<SqliteDatabaseContext> logger
	)
	: this(
	  true
	, options
	, configuration
	, logger
	)
	{

	}
	protected SqliteDatabaseContext(
		  bool _
		, DbContextOptions options
		, IConfiguration configuration
		, ILogger<SqliteDatabaseContext> logger
		)
	: base(options)
	{
		_logger = logger;
		var csb = new SqliteConnectionStringBuilder(configuration.GetConnectionString("SynqraProjectionSqlite"));
		var file = Environment.ExpandEnvironmentVariables(csb.DataSource);
		csb.DataSource = file;
		Directory.CreateDirectory(Path.GetDirectoryName(file));
		_connectionString = csb.ToString();
	}

	protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
	{
		base.OnConfiguring(optionsBuilder);
		optionsBuilder.EnableDetailedErrors();
		optionsBuilder.UseSqlite(_connectionString, x =>
		{
			x.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
		});
		optionsBuilder.EnableSensitiveDataLogging();
		optionsBuilder.ConfigureWarnings(w => w.Throw(RelationalEventId.MultipleCollectionIncludeWarning));
		EmergencyLog.Default.LogWarning("SqliteDatabaseContext OnConfiguring Done");
	}

	public async Task EnsureSchemaAsync()
	{
		try
		{
			// Attempt to open a transaction to check if the database is writable
			_logger?.LogInformation("Opening database...");

			await using var transaction = await Database.BeginTransactionAsync();
			await transaction.RollbackAsync();

			// Proceed with migration if the database is writable
			// await Database.EnsureCreatedAsync();
			Database.MigrateNative();
			// await Database.MigrateAsync();
		}
		catch (SqliteException ex) when (ex.SqliteErrorCode == 8) // SQLite Error 8: 'attempt to write a readonly database'
		{
			_logger?.LogWarning(ex, "The database is in read-only mode and cannot be migrated.");
		}
	}

	public void Vacuum()
	{
		Database.ExecuteSqlRaw("VACUUM");
	}
}

public class SqliteStore : IObjectStore
{
	static SqliteStore()
	{
		AppContext.SetSwitch("Synqra.GuidExtensions.ValidateNamespaceIdHashChain", false); // I use deterministic hash guids for named collections per type ids, and type id is also hash based by type name, so namespace id for collection is v5
	}

	private readonly SqliteDatabaseContext _databaseContext;
	private readonly ISBXSerializerFactory _serializerFactory;
	public ITypeMetadataProvider TypeMetadataProvider { get; }
	internal readonly JsonSerializerOptions? _jsonSerializerOptions;

	private readonly Dictionary<Guid, StoreCollection> _collections = new();
	private readonly ConcurrentDictionary<Guid, StrongReference> _attachedObjectsById = new();
	private readonly ConditionalWeakTable<object, AttachedObjectData> _attachedObjects = new();

	public SqliteStore(
	  SqliteDatabaseContext databaseContext
	, ISBXSerializerFactory serializerFactory
	, ITypeMetadataProvider typeMetadataProvider
	, IAppendStorage? eventStorage = null
	, IEventReplicationService? eventReplicationService = null
	, JsonSerializerOptions? jsonSerializerOptions = null
	, JsonSerializerContext? jsonSerializerContext = null
	)
	{
		_databaseContext = databaseContext;
		_serializerFactory = serializerFactory;
		TypeMetadataProvider = typeMetadataProvider;
		_jsonSerializerOptions = jsonSerializerOptions;

		databaseContext.EnsureSchemaAsync().GetAwaiter().GetResult();
	}

	ISynqraCollection IObjectStore.GetCollection(Type type, string? collectionName)
	{
		return GetCollection(type, collectionName ?? "");
	}

	ISynqraCollection<T> IObjectStore.GetCollection<T>(string? collectionName)
		where T : class
	{
		return GetCollection<T>(collectionName ?? "");
	}

	internal StoreCollection GetCollection(Type type, string collectionName)
	{
		var collectionId = GetCollectionId(type, collectionName ?? throw new ArgumentNullException(nameof(collectionName)));
#if NET7_0_OR_GREATER
		ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_collections, collectionId, out var exists);
		if (!exists || slot == null)
		{
			var gtype = typeof(SqliteStoreCollection<>).MakeGenericType(type);
			slot = (StoreCollection)Activator.CreateInstance(gtype, [
				  /* databaseContext */ _databaseContext
				// , /* store */ _databaseContext.Set<Command>() // TODO this is a problem
				, /* set */ null
				, /* store */ this
				, /* containerId */ ContainerId
				, /* collectionId */ collectionId
				, /* serializerFactory */ _serializerFactory
				])!;
		}
		return slot;
#else
		throw new Exception("Not implemented for older frameworks");
#endif
	}

	internal SqliteStoreCollection<T> GetCollection<T>(string collectionName)
		where T : class
	{
		var collectionId = GetCollectionId(typeof(T), collectionName ?? throw new ArgumentNullException(nameof(collectionName)));
#if NET7_0_OR_GREATER
		ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_collections, collectionId, out var exists);
		if (!exists || slot == null)
		{
			var col = new SqliteStoreCollection<T>(
				  /* databaseContext */ _databaseContext
				// , /* store */ _databaseContext.Set<T>()
				, null
				, /* store */ this
				, /* containerId */ ContainerId
				, /* collectionId */ collectionId
				, /* serializerFactory */ _serializerFactory
				);
			slot = col;
			return col;

		}
		return (SqliteStoreCollection<T>)slot;
#else
		throw new Exception("Not implemented for older frameworks");
#endif
	}


	Guid GetCollectionId(Type rootType, string collectionName)
	{
		return TypeMetadataProvider.GetTypeMetadata(rootType).GetCollectionId(collectionName ?? throw new ArgumentNullException(nameof(collectionName)));
	}

	public Guid GetId(object model)
	{
		throw new NotImplementedException();
	}

	public async Task SubmitCommandAsync(ISynqraCommand newCommand)
	{
	}

	public Guid ContainerId { get; }
}

public class SqliteProjection : IProjection
{
	#region Command Visitor

	public async Task AfterVisitAsync(Command cmd, CommandHandlerContext ctx)
	{
	}

	public async Task BeforeVisitAsync(Command cmd, CommandHandlerContext ctx)
	{
	}

	public async Task VisitAsync(CreateObjectCommand cmd, CommandHandlerContext ctx)
	{
	}

	public async Task VisitAsync(DeleteObjectCommand cmd, CommandHandlerContext ctx)
	{
	}

	public async Task VisitAsync(ChangeObjectPropertyCommand cmd, CommandHandlerContext ctx)
	{
	}

	#endregion

	#region Event Visitor

	public async Task BeforeVisitAsync(Event ev, EventVisitorContext ctx)
	{
	}

	public async Task AfterVisitAsync(Event ev, EventVisitorContext ctx)
	{
	}

	public async Task VisitAsync(ObjectCreatedEvent ev, EventVisitorContext ctx)
	{
	}

	public async Task VisitAsync(ObjectPropertyChangedEvent ev, EventVisitorContext ctx)
	{
	}

	public async Task VisitAsync(ObjectDeletedEvent ev, EventVisitorContext ctx)
	{
	}

	public async Task VisitAsync(CommandCreatedEvent ev, EventVisitorContext ctx)
	{
	}

	#endregion
}
