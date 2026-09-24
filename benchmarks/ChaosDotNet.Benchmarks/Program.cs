using System.Net;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ChaosDotNet;
using ChaosDotNet.Factories;

BenchmarkSwitcher.FromAssembly(typeof(PassThroughBenchmarks).Assembly).Run(args);

/// <summary>What a veneer costs when no window is active.</summary>
[MemoryDiagnoser]
public class PassThroughBenchmarks
{
    private readonly Service _direct = new();
    private IService _proxy = null!;
    private HttpClient _plainClient = null!;
    private HttpClient _veneerClient = null!;

    [GlobalSetup]
    public void Setup()
    {
        _proxy = new ProxyFactory<IService>().Create(_direct);
        _plainClient = new HttpClient(new OkHandler()) { BaseAddress = new Uri("http://bench") };
        _veneerClient = new HttpFactory().After(1, TimeUnit.Hours).Forever().Freeze().CreateClient(new Uri("http://bench"), new OkHandler());
    }

    [Benchmark(Baseline = true)]
    public Task<int> Direct_interface_call() => _direct.GetAsync(1);

    [Benchmark]
    public Task<int> ProxyFactory_veneer() => _proxy.GetAsync(1);

    [Benchmark]
    public Task<HttpResponseMessage> Plain_HttpClient() => _plainClient.GetAsync("/");

    [Benchmark]
    public Task<HttpResponseMessage> HttpFactory_veneer() => _veneerClient.GetAsync("/");

    public interface IService
    {
        Task<int> GetAsync(int id);
    }

    public sealed class Service : IService
    {
        public Task<int> GetAsync(int id) => Task.FromResult(id);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
