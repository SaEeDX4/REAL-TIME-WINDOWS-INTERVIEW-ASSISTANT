namespace InterviewAssistant.App.Services;

/// <summary>
/// Customer (Release) builds use the Interview Assistant service only. Developer builds (Debug, or -p:DeveloperBuild=true)
/// additionally allow the prototype "own OpenAI key" path and model/protocol overrides for local testing.
/// </summary>
public static class BuildFlavor
{
#if DEVELOPER_BUILD
    public const bool Developer = true;
#else
    public const bool Developer = false;
#endif
}
