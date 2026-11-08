namespace RunnerControlPlane.Access;

public static class AccessPolicies
{
    public const string CanView = "CanViewRunnerAccess";
    public const string CanManage = "CanManageRunnerAccess";
}

public sealed record UpdateRunnerAccessRequest(IReadOnlyList<string>? Repositories);

public sealed record RunnerAccessResponse(
    string Organization,
    string RunnerGroup,
    string RunnerLabel,
    bool IsRestricted,
    IReadOnlyList<string> Repositories,
    bool CanManage);
