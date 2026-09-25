namespace ChaosDotNet.DependencyInjection;

/// <summary>How <c>AddChaosMonkey</c> treats a client the app already registered.</summary>
public enum ChaosStrategy
{
    /// <summary>
    /// Keep the app's registration and put chaos on top of it. The app's own logic (poolers, handlers, wrappers) still runs,
    /// and every call goes through the chaos veneer first.
    /// </summary>
    Proxy,

    /// <summary>
    /// Remove the app's registrations and register ChaosDotNet's standard client instead, with chaos on top. The app's own
    /// logic is dropped; calls still reach the real infrastructure through the standard client.
    /// </summary>
    Replace,
}
