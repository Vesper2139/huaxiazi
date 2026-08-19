namespace PromptFloat.Services;

public enum UnhandledExceptionOrigin
{
    UiDispatcher,
    UnobservedTask,
    AppDomain
}

public static class ExceptionPresentationPolicy
{
    public static bool ShouldShowFatalDialog(UnhandledExceptionOrigin origin) =>
        origin == UnhandledExceptionOrigin.AppDomain;
}
