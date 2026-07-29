using Microsoft.Extensions.Options;

namespace SportsVenueApi.Jobs;

/// <summary>Bound from the <c>Jobs:BookingExpiry</c> configuration section.</summary>
public sealed class BookingExpiryOptions
{
    public const string Section = "Jobs:BookingExpiry";

    /// <summary>
    /// Off unless explicitly switched on. This is the first background WRITER in the
    /// process, and the thing it writes is other people's bookings — it should not begin
    /// running on somebody's laptop, in CI, or in an integration test because they happened
    /// to pull main. Production opts in through an environment variable.
    /// </summary>
    public bool Enabled { get; set; }

    public int IntervalSeconds { get; set; } = 300;
    public int BatchSize { get; set; } = 200;

    /// <summary>Minutes an unpaid app booking may hold its slot. See <see cref="Helpers.ExpiryPolicy"/>.</summary>
    public int WindowMinutes { get; set; } = 120;
    public int SlotBufferMinutes { get; set; } = 30;
    public int MinimumMinutes { get; set; } = 10;
}

/// <summary>
/// Timer shell. Owns no rules — every decision about what may be cancelled lives in
/// <see cref="UnpaidBookingSweep"/>, which is Scoped and takes an explicit clock.
///
/// SINGLE INSTANCE ASSUMPTION: no distributed lock. What actually enforces that today is
/// the host-port binding <c>127.0.0.1:8000:8000</c> in playmakerjo-deploy/docker-compose.yml,
/// which structurally prevents `--scale api=2`, plus nginx proxying to one hardcoded
/// upstream. If that ever changes, two sweepers will race — though even then the damage is
/// bounded, because the UPDATE is a compare-and-set: the loser cancels nothing.
/// </summary>
public class BookingExpiryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly BookingExpiryOptions _options;
    private readonly ILogger<BookingExpiryService> _logger;

    public BookingExpiryService(
        IServiceScopeFactory scopes,
        IOptions<BookingExpiryOptions> options,
        ILogger<BookingExpiryService> logger)
    {
        // IServiceScopeFactory, not AppDbContext: a BackgroundService is a singleton, and
        // capturing a Scoped DbContext in one is a captive dependency that would hold a
        // single connection open for the lifetime of the process.
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(30, _options.IntervalSeconds));
        _logger.LogInformation(
            "Booking expiry job started: every {Interval}, batch {Batch}", interval, _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A scope per tick, disposed before the delay — a sleeping job holds no
                // database connection.
                using var scope = _scopes.CreateScope();
                var sweep = scope.ServiceProvider.GetRequiredService<UnpaidBookingSweep>();
                await sweep.SweepAsync(DateTime.UtcNow, _options.BatchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;  // shutting down; not an error
            }
            catch (Exception ex)
            {
                // Nothing may escape this loop. .NET's default BackgroundServiceExceptionBehavior
                // is StopHost, so one unhandled exception here would take down the whole API —
                // and with `restart: unless-stopped` in docker-compose that becomes an invisible
                // crash-restart loop rather than a clean, noticeable outage. Program.cs also sets
                // Ignore as a second layer; this catch is the first.
                _logger.LogError(ex, "Booking expiry sweep failed; will retry next tick");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Booking expiry job stopped");
    }
}
