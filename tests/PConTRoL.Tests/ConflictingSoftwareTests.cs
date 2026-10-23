using System.Diagnostics;
using PConTRoL.Core;

namespace PConTRoL.Tests;

public class ConflictingSoftwareTests
{
    [Fact]
    public void Production_control_is_the_only_conflict_never_the_job_manager()
    {
        Assert.Equal(["ProductionControl"], ConflictingSoftware.ProcessNames);
    }

    [Fact]
    public void Running_process_is_found_and_stopped()
    {
        // Ein Konsolenprogramm ohne Fenster: CloseMainWindow wirkt nicht, also muss hart beendet werden.
        using var p = Process.Start(new ProcessStartInfo("ping", "-n 120 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            Assert.Contains(ConflictingSoftware.FindRunning([p.ProcessName]), x => x.Id == p.Id);

            Assert.Null(ConflictingSoftware.Stop([p], TimeSpan.FromMilliseconds(200)));
            Assert.True(p.HasExited);
        }
        finally
        {
            if (!p.HasExited) p.Kill();
        }
    }

    [Fact]
    public void Already_exited_process_is_fine()
    {
        using var p = Process.Start(new ProcessStartInfo("cmd", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.Null(ConflictingSoftware.Stop([p], TimeSpan.FromMilliseconds(100)));
    }
}
