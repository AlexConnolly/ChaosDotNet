namespace ChaosDotNet.Tests;

public sealed class OddValuesTests
{
    public enum Status
    {
        Pending,
        Paid,
    }

    public static TheoryData<Type> Types =>
    [
        typeof(string), typeof(int), typeof(long), typeof(short), typeof(byte), typeof(uint), typeof(double), typeof(float),
        typeof(decimal), typeof(bool), typeof(char), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(Guid),
        typeof(int?), typeof(Status), typeof(Receipt), typeof(int[]), typeof(List<string>), typeof(IEnumerable<int>),
        typeof(IReadOnlyList<Receipt>), typeof(IDictionary<string, int>), typeof(ISet<string>), typeof(Task<int>),
        typeof(ValueTask<string>),
    ];

    [Theory]
    [MemberData(nameof(Types))]
    public void Values_fit_the_type(Type type)
    {
        var random = new Random(1);
        for (var i = 0; i < 50; i++)
        {
            var value = OddValues.For(type, random);
            if (value is null)
            {
                Assert.True(!type.IsValueType || Nullable.GetUnderlyingType(type) is not null, $"null for {type}");
            }
            else
            {
                Assert.True(type.IsInstanceOfType(value), $"{value.GetType()} for {type}");
            }
        }
    }

    [Fact]
    public void Values_are_odd()
    {
        var random = new Random(2);
        var ints = Enumerable.Range(0, 100).Select(_ => (int)OddValues.For(typeof(int), random)!).ToHashSet();
        var doubles = Enumerable.Range(0, 100).Select(_ => (double)OddValues.For(typeof(double), random)!).ToList();
        var strings = Enumerable.Range(0, 100).Select(_ => (string?)OddValues.For(typeof(string), random)).ToList();
        var status = (Status)OddValues.For(typeof(Status), random)!;

        Assert.Equal([int.MinValue, -1, 0, int.MaxValue], ints.Order());
        Assert.Contains(doubles, double.IsNaN);
        Assert.Contains(null, strings);
        Assert.Contains(string.Empty, strings);
        Assert.Contains(strings, s => s?.Length == 10_000);
        Assert.False(Enum.IsDefined(status));
        Assert.Equal(Guid.Empty, OddValues.For(typeof(Guid), random));
    }

    [Fact]
    public void The_same_seed_gives_the_same_values()
    {
        static string Run(int seed)
        {
            var random = new Random(seed);
            return string.Join('|', Enumerable.Range(0, 30).Select(_ => OddValues.For(typeof(decimal), random)));
        }

        Assert.Equal(Run(5), Run(5));
        Assert.NotEqual(Run(5), Run(6));
    }

    [Fact]
    public async Task Task_types_wrap_an_odd_value()
    {
        var task = (Task<int>)OddValues.For(typeof(Task<int>), new Random(1))!;

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Contains(await task, new[] { 0, -1, int.MinValue, int.MaxValue });
    }
}
