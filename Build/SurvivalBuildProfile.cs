namespace ASWDEBUG.Build
{
    internal static class SurvivalBuildProfile
    {
#if SURVIVAL_RELEASE_A
        internal const string Edition = "ReleaseA";
        internal const bool InternalTools = false;
#else
        internal const string Edition = "Private";
        internal const bool InternalTools = true;
#endif
    }
}
