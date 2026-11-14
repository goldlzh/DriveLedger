using System;
using System.IO;
using System.Linq;

namespace DriveLedger.App;

// Separates platform service/startup operations from the real filesystem mutation lifetime.
public static class InstallationLifecycle
{
    public static void Apply(InstallPlan plan, string[] sourceFiles, Action stop,
        Action<InstallSession> configure, Action start, Action notify, string? shortcut = null)
    {
        using (var session = InstallSession.Open(plan, shortcut))
        {
            stop();
            foreach (var source in sourceFiles)
                session.ReplacePayload(source, Path.GetRelativePath(plan.Source, source));
            session.CommitReceipt(sourceFiles.Select(f => Path.GetRelativePath(plan.Source, f)).ToArray());
            configure(session);
        }
        // No installation resource is held while the restarted collector or modal notification runs.
        start();
        notify();
    }
}
