using Microsoft.Extensions.Logging;
using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests.App;

public sealed class ForecastLookupGuardTests
{
    private static readonly AutoFillRequest Request = new("P1078", "T35647");

    [Theory]
    [MemberData(nameof(Unexpected))]
    public async Task An_unexpected_exception_becomes_a_readable_failed_result(Exception failure)
    {
        var logger = new ListLogger();

        var result = await ForecastLookupGuard.AutoFillAsync(new FakeLookup(() => throw failure), Request, logger, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ForecastLookupGuard.UnexpectedAutoFillError, result.Error);
        Assert.Null(result.Project);
        Assert.Null(result.Task);
    }

    [Fact]
    public async Task The_log_has_the_exception_type_but_neither_the_message_nor_the_typed_numbers()
    {
        var logger = new ListLogger();
        var failure = new InvalidOperationException("secret-token P1078 T35647");

        await ForecastLookupGuard.AutoFillAsync(new FakeLookup(() => throw failure), Request, logger, CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Contains(nameof(InvalidOperationException), entry);
        Assert.DoesNotContain("secret-token", entry);
        Assert.DoesNotContain("P1078", entry);
        Assert.DoesNotContain("T35647", entry);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed_nor_logged()
    {
        var logger = new ListLogger();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ForecastLookupGuard.AutoFillAsync(new FakeLookup(() => throw new OperationCanceledException(cts.Token)), Request, logger, cts.Token));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task A_good_answer_is_returned_as_is_and_the_numbers_are_passed_on()
    {
        var expected = new ForecastAutoFillResult(null, null, "ok", []);
        var lookup = new FakeLookup(() => expected);

        var result = await ForecastLookupGuard.AutoFillAsync(lookup, Request, new ListLogger(), CancellationToken.None);

        Assert.Same(expected, result);
        Assert.Equal(("P1078", "T35647"), lookup.Asked);
    }

    public static TheoryData<Exception> Unexpected() => new()
    {
        new InvalidOperationException("boom"),
        new System.Text.Json.JsonException("bad json"),
        new HttpRequestException("refused"),
    };

    private sealed class FakeLookup(Func<ForecastAutoFillResult> autoFill) : IForecastLookup
    {
        public (string?, string?)? Asked { get; private set; }

        public Task<ForecastAutoFillResult> AutoFillAsync(string? projectNumber, string? taskNumber, CancellationToken cancellationToken = default)
        {
            Asked = (projectNumber, taskNumber);
            return Task.FromResult(autoFill());
        }

        public Task<ForecastLookupResult<ForecastProjectMatch>> SearchProjectsAsync(string? query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ForecastLookupResult<ForecastTaskMatch>> SearchTasksAsync(string? query, int? projectId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ForecastGetResult<ForecastProjectMatch>> GetProjectAsync(string? projectNumber, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ForecastGetResult<ForecastTaskMatch>> GetTaskAsync(string? taskNumber, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}
