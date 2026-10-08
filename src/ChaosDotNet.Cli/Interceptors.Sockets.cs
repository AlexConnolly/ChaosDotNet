using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ChaosDotNet.Cli;

internal static partial class Interceptors
{
    private static readonly string[] SocketOperations = ["Connect", "Send", "Receive"];

    // Test platforms talk to their runner over TCP; breaking that would crash the run, not test the app.
    private static readonly string[] TestPlatforms = ["Microsoft.TestPlatform", "Microsoft.VisualStudio.TestPlatform", "Microsoft.Testing"];

    private static readonly ConditionalWeakTable<Socket, StrongBox<bool>> AppSockets = [];

    // Set while inside a patched socket method, so a public overload that calls another counts as one call.
    [ThreadStatic]
    private static bool t_inSocket;

    /// <summary>Patches every public connect, send and receive method on <see cref="Socket"/>, sync and async.</summary>
    private static void PatchSockets()
    {
        var prefix = new HarmonyMethod(typeof(Interceptors).GetMethod(nameof(SocketCall), BindingFlags.Static | BindingFlags.NonPublic));
        var finalizer = new HarmonyMethod(typeof(Interceptors).GetMethod(nameof(SocketCallEnded), BindingFlags.Static | BindingFlags.NonPublic));
        foreach (var method in typeof(Socket).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => SocketOperations.Any(op => m.Name == op || m.Name == op + "Async")))
        {
            try
            {
                Harmony.Patch(method, prefix: prefix, finalizer: finalizer);
            }
            catch (Exception) when (method.GetParameters()[0].ParameterType == typeof(System.Net.IPAddress[]))
            {
                // Harmony cannot rewrite Connect(IPAddress[], int); it calls Connect(IPAddress, int) per address, which is patched.
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"dotnet-chaos: could not patch Socket.{method.Name}: {ex.Message}");
            }
        }
    }

    private static void SocketCall(Socket __instance, MethodBase __originalMethod, out bool __state)
    {
        __state = false;
        if (t_inSocket || t_bypass || Tcp is not { } tcp || __instance.ProtocolType != ProtocolType.Tcp || !IsAppSocket(__instance))
        {
            return;
        }

        t_inSocket = __state = true;
        var operation = SocketOperations.First(op => __originalMethod.Name.StartsWith(op, StringComparison.Ordinal));
        try
        {
            tcp.Engine.Run(new SocketChaosCall(operation, __instance.RemoteEndPoint?.ToString()), () => true);
        }
        catch
        {
            t_inSocket = __state = false;
            throw;
        }
    }

    private static Exception? SocketCallEnded(Exception? __exception, bool __state)
    {
        if (__state)
        {
            t_inSocket = false;
        }

        return __exception;
    }

    /// <summary>
    /// Whether the app opened this socket rather than the test platform, decided once per socket from the first frame
    /// outside the runtime. Sockets with no such frame (pure runtime code) are left alone.
    /// </summary>
    private static bool IsAppSocket(Socket socket) => AppSockets.GetValue(socket, _ =>
    {
        var caller = new StackTrace().GetFrames()
            .Select(f => f.GetMethod()?.DeclaringType?.Namespace)
            .FirstOrDefault(ns => ns is not null
                && !ns.StartsWith("System", StringComparison.Ordinal)
                && !ns.StartsWith("ChaosDotNet.Cli", StringComparison.Ordinal)
                && !ns.StartsWith("HarmonyLib", StringComparison.Ordinal));
        return new StrongBox<bool>(caller is not null && !TestPlatforms.Any(p => caller.StartsWith(p, StringComparison.Ordinal)));
    }).Value;
}
