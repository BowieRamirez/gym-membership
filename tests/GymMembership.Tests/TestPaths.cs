/// <summary>Finds the app project from the test's output folder (the solution file marks the root).</summary>
static class TestPaths
{
    public static string AppFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "gym-membership.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("solution folder not found"), "src", "GymMembership.App");
    }
}
