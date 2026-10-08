using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Runtime.CompilerServices;
using ChaosDotNet.Factories;
using HarmonyLib;

namespace ChaosDotNet.Cli;

/// <summary>
/// Patches every <see cref="SocketsHttpHandler"/> and every ADO.NET command and connection in the process, so calls go
/// through the timelines of <see cref="Http"/> and <see cref="Sql"/> without the app or its tests changing.
/// </summary>
internal static class Interceptors
{
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Harmony Harmony = new("ChaosDotNet.Cli");
    private static readonly HashSet<Type> Patched = [];
    private static readonly ConditionalWeakTable<HttpMessageHandler, HttpMessageInvoker> Veneers = [];

    // Set while a patched method calls the original, so the original (and any wrapped provider it calls) is not faulted twice.
    [ThreadStatic]
    private static bool t_bypass;

    private static HttpFactory? Http { get; set; }

    private static SqlFactory? Sql { get; set; }

    /// <summary>Points the patches at new factories. Patches the process on the first call.</summary>
    public static void Install(HttpFactory http, SqlFactory sql)
    {
        lock (Patched)
        {
            Http = http;
            Sql = sql;
            Veneers.Clear();
            if (Patched.Add(typeof(SocketsHttpHandler)))
            {
                Patch(typeof(SocketsHttpHandler), "SendAsync", nameof(SendAsync), typeof(HttpRequestMessage), typeof(CancellationToken));
                AppDomain.CurrentDomain.AssemblyLoad += (_, e) => PatchDatabaseTypes(e.LoadedAssembly);
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    PatchDatabaseTypes(assembly);
                }
            }
        }
    }

    private static void PatchDatabaseTypes(Assembly assembly)
    {
        if (assembly.IsDynamic
            || assembly.GetName().Name?.StartsWith("ChaosDotNet", StringComparison.Ordinal) == true
            || (assembly != typeof(DbCommand).Assembly && !assembly.GetReferencedAssemblies().Any(r => r.Name == typeof(DbCommand).Assembly.GetName().Name)))
        {
            return;
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.OfType<Type>().ToArray();
        }

        lock (Patched)
        {
            foreach (var type in types.Where(t => t is { IsAbstract: false, ContainsGenericParameters: false }))
            {
                if (type.IsSubclassOf(typeof(DbCommand)) && Patched.Add(type))
                {
                    Patch(type, nameof(DbCommand.ExecuteNonQuery), nameof(ExecuteNonQuery));
                    Patch(type, nameof(DbCommand.ExecuteNonQueryAsync), nameof(ExecuteNonQueryAsync), typeof(CancellationToken));
                    Patch(type, nameof(DbCommand.ExecuteScalar), nameof(ExecuteScalar));
                    Patch(type, nameof(DbCommand.ExecuteScalarAsync), nameof(ExecuteScalarAsync), typeof(CancellationToken));
                    Patch(type, "ExecuteDbDataReader", nameof(ExecuteDbDataReader), typeof(CommandBehavior));
                    Patch(type, "ExecuteDbDataReaderAsync", nameof(ExecuteDbDataReaderAsync), typeof(CommandBehavior), typeof(CancellationToken));
                }
                else if (type.IsSubclassOf(typeof(DbConnection)) && Patched.Add(type))
                {
                    Patch(type, nameof(DbConnection.Open), nameof(Open));
                    Patch(type, nameof(DbConnection.OpenAsync), nameof(OpenAsync), typeof(CancellationToken));
                }
            }
        }
    }

    /// <summary>Patches <paramref name="type"/>'s own override of a method. Inherited methods are left alone: they call overrides that are patched.</summary>
    private static void Patch(Type type, string method, string prefix, params Type[] parameters)
    {
        var target = type.GetMethod(method, Declared, null, parameters, null);
        if (target is null || target.IsAbstract)
        {
            return;
        }

        try
        {
            Harmony.Patch(target, prefix: new HarmonyMethod(typeof(Interceptors).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dotnet-chaos: could not patch {type.FullName}.{method}: {ex.Message}");
        }
    }

    private static T Bypass<T>(Func<T> original)
    {
        t_bypass = true;
        try
        {
            return original();
        }
        finally
        {
            t_bypass = false;
        }
    }

    private static bool SendAsync(SocketsHttpHandler __instance, HttpRequestMessage __0, CancellationToken __1, ref Task<HttpResponseMessage> __result)
    {
        if (t_bypass || Http is not { } http)
        {
            return true;
        }

        var veneer = Veneers.GetValue(__instance, real =>
        {
            var chaos = http.CreateHandler();
            chaos.InnerHandler = new Original(real);
            return new HttpMessageInvoker(chaos, disposeHandler: false);
        });
        __result = veneer.SendAsync(__0, __1);
        return false;
    }

    private static bool ExecuteNonQuery(DbCommand __instance, ref int __result)
    {
        if (t_bypass || Sql is not { } sql)
        {
            return true;
        }

        __result = sql.Engine.Run(Call("ExecuteNonQuery", __instance), () => Bypass(__instance.ExecuteNonQuery));
        return false;
    }

    private static bool ExecuteNonQueryAsync(DbCommand __instance, CancellationToken __0, ref Task<int> __result)
    {
        if (t_bypass || Sql is not { } sql)
        {
            return true;
        }

        __result = sql.Engine.RunAsync(Call("ExecuteNonQuery", __instance), () => Bypass(() => __instance.ExecuteNonQueryAsync(__0)), __0);
        return false;
    }

    private static bool ExecuteScalar(DbCommand __instance, ref object? __result)
    {
        if (t_bypass || Sql is not { } sql)
        {
            return true;
        }

        __result = sql.Engine.Run(Call("ExecuteScalar", __instance), () => Bypass(__instance.ExecuteScalar));
        return false;
    }

    private static bool ExecuteScalarAsync(DbCommand __instance, CancellationToken __0, ref Task<object?> __result)
    {
        if (t_bypass || Sql is not { } sql)
        {
            return true;
        }

        __result = sql.Engine.RunAsync(Call("ExecuteScalar", __instance), () => Bypass(() => __instance.ExecuteScalarAsync(__0)), __0);
        return false;
    }

    private static bool ExecuteDbDataReader(DbCommand __instance, CommandBehavior __0, ref DbDataReader __result)
    {
        if (t_bypass || Sql is not { } sql)
        {
            return true;
        }

        var call = Call("ExecuteReader", __instance);
        var fault = EnsureSupported(sql.Engine.BeforeCall(call));
        try
        {
            var reader = Bypass(() => __instance.ExecuteReader(__0));
            sql.Engine.CallSucceeded(call);
            __result = fault is null ? reader : fault.Wrap(reader);
        }
        catch (Exception ex)
        {
            sql.Engine.CallFailed(call, ex);
            throw;
        }

        return false;
    }

    private static bool ExecuteDbDataReaderAsync(DbCommand __instance, CommandBehavior __0, CancellationToken __1, ref Task<DbDataReader> __result)
    {
        if (t_bypass || Sql is not { } sql)
        {
            return true;
        }

        __result = ReadAsync(sql.Engine, __instance, __0, __1);
        return false;

        static async Task<DbDataReader> ReadAsync(ChaosEngine engine, DbCommand command, CommandBehavior behavior, CancellationToken cancellationToken)
        {
            var call = Call("ExecuteReader", command);
            var fault = EnsureSupported(await engine.BeforeCallAsync(call, cancellationToken).ConfigureAwait(false));
            try
            {
                var reader = await Bypass(() => command.ExecuteReaderAsync(behavior, cancellationToken)).ConfigureAwait(false);
                engine.CallSucceeded(call);
                return fault is null ? reader : fault.Wrap(reader);
            }
            catch (Exception ex)
            {
                engine.CallFailed(call, ex);
                throw;
            }
        }
    }

    private static bool Open(DbConnection __instance)
    {
        if (t_bypass || Sql is not { } sql)
        {
            return true;
        }

        sql.Engine.Run(new SqlChaosCall("Open"), () => Bypass(() =>
        {
            __instance.Open();
            return true;
        }));
        return false;
    }

    private static bool OpenAsync(DbConnection __instance, CancellationToken __0, ref Task __result)
    {
        if (t_bypass || Sql is not { } sql)
        {
            return true;
        }

        __result = sql.Engine.RunAsync(new SqlChaosCall("Open"), () => Bypass(() => __instance.OpenAsync(__0)), __0);
        return false;
    }

    private static SqlChaosCall Call(string operation, DbCommand command) => new(operation, command.CommandText ?? string.Empty);

    /// <summary>A <see cref="ReaderFault"/> is the only fault <see cref="ChaosEngine.BeforeCall"/> leaves for a database veneer to apply.</summary>
    private static ReaderFault? EnsureSupported(Fault? fault) => fault switch
    {
        null => null,
        ReaderFault reader => reader,
        _ => throw new NotSupportedException($"The fault '{fault.Name}' is not supported by dotnet-chaos."),
    };

    /// <summary>The handler that reaches the network: the patched handler itself, with the patch bypassed.</summary>
    private sealed class Original(HttpMessageHandler real) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(real, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Bypass(() => _invoker.SendAsync(request, cancellationToken));
    }
}
