namespace Setup_Agent.Services;

internal static class FinalServiceStage
{
    // Add future post-update service components here. Runs once Windows Update has confirmed
    // there are no remaining (non-preview) updates, before the agent removes its own scheduled
    // task and exits for the last time.
    public static void Run(Logger logger)
    {
        logger.Info("Final service stage: no components configured yet.");
    }
}
