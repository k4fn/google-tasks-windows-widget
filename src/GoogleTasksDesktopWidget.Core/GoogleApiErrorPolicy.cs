namespace GoogleTasksDesktopWidget.Core;

public enum GoogleApiErrorKind
{
    PermissionDenied,
    TasksApiNotEnabled,
    InsufficientPermissions,
    DomainPolicy
}

public static class GoogleApiErrorPolicy
{
    public static GoogleApiErrorKind ClassifyForbiddenReason(string? reasonCode) => reasonCode switch
    {
        "accessNotConfigured" or "serviceDisabled" => GoogleApiErrorKind.TasksApiNotEnabled,
        "insufficientPermissions" => GoogleApiErrorKind.InsufficientPermissions,
        "domainPolicy" => GoogleApiErrorKind.DomainPolicy,
        _ => GoogleApiErrorKind.PermissionDenied
    };
}
