using System.Globalization;
using Microsoft.Extensions.Options;

namespace RunnerControlPlane.Images;

public sealed class RunnerImageScheduleWorker(
    IRunnerImageBuildQueue queue,
    IOptions<RunnerImageBuildOptions> options,
    ILogger<RunnerImageScheduleWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var schedule = options.Value;
        if (!schedule.RoutineBuildEnabled)
        {
            return;
        }

        if (schedule.RoutineBuildRequest is null
            || !Enum.TryParse<DayOfWeek>(schedule.RoutineBuildDayUtc, true, out var buildDay)
            || !TimeOnly.TryParse(
                schedule.RoutineBuildTimeUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var buildTime))
        {
            logger.LogError(
                "Routine runner image builds are enabled but the weekly schedule or pinned build request is invalid.");
            return;
        }

        var routineBuildRequest = schedule.RoutineBuildRequest.ToRequest();
        var requestError = RunnerImageRequestValidator.Validate(routineBuildRequest);
        if (requestError is not null)
        {
            logger.LogError("Routine runner image build configuration is invalid: {Reason}", requestError);
            return;
        }

        var lastScheduledWeek = (DateOnly?)null;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            var now = DateTimeOffset.UtcNow;
            if (now.DayOfWeek == buildDay
                && TimeOnly.FromDateTime(now.UtcDateTime) >= buildTime)
            {
                var today = DateOnly.FromDateTime(now.UtcDateTime);
                var weekStart = today.AddDays(-((7 + (int)now.DayOfWeek - (int)buildDay) % 7));
                if (lastScheduledWeek != weekStart)
                {
                    queue.Queue(routineBuildRequest);
                    lastScheduledWeek = weekStart;
                    logger.LogInformation("Queued the routine weekly runner image build for {Week}.", weekStart);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
