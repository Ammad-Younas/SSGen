using SSGen.Core.Projects;

namespace SSGen.Core.Tests;

public class ProjectServiceTests
{
    [Fact]
    public void Create_makes_folder_db_and_recent_entry()
    {
        using var dir = new TempDir();
        var service = new ProjectService(TestDb.CreateApp(dir));
        Project? changed = null;
        service.CurrentChanged += (_, p) => changed = p;

        var project = service.Create("Fitness App", dir.Combine("projects"));

        Assert.Same(project, service.Current);
        Assert.Same(project, changed);
        Assert.True(File.Exists(Path.Combine(project.Folder, ProjectService.ProjectDbFileName)));
        Assert.True(Directory.Exists(project.ScreensDir));
        var recent = Assert.Single(service.GetRecent());
        Assert.Equal("Fitness App", recent.Name);
        Assert.True(recent.Exists);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad/name")]
    [InlineData("what?")]
    [InlineData("trailing.")]
    public void Create_rejects_invalid_names(string name)
    {
        using var dir = new TempDir();
        var service = new ProjectService(TestDb.CreateApp(dir));

        Assert.Throws<UserFacingException>(() => service.Create(name, dir.Path));
    }

    [Fact]
    public void Create_refuses_non_empty_existing_folder()
    {
        using var dir = new TempDir();
        dir.Touch("projects", "Taken", "file.txt");
        var service = new ProjectService(TestDb.CreateApp(dir));

        Assert.Throws<UserFacingException>(() => service.Create("Taken", dir.Combine("projects")));
    }

    [Fact]
    public void Open_reads_saved_name_and_rejects_non_projects()
    {
        using var dir = new TempDir();
        var appDb = TestDb.CreateApp(dir);
        var created = new ProjectService(appDb).Create("Recipes", dir.Path);

        var reopened = new ProjectService(appDb).Open(created.Folder);

        Assert.Equal("Recipes", reopened.Name);
        Assert.Throws<UserFacingException>(() => new ProjectService(appDb).Open(dir.Combine("nope")));
    }

    [Fact]
    public void Recent_is_most_recent_first_and_removable()
    {
        using var dir = new TempDir();
        var service = new ProjectService(TestDb.CreateApp(dir));
        var a = service.Create("A", dir.Path);
        Thread.Sleep(5);
        service.Create("B", dir.Path);
        Thread.Sleep(5);
        service.Open(a.Folder);

        Assert.Equal(["A", "B"], service.GetRecent().Select(r => r.Name));

        service.RemoveRecent(a.Folder);
        Assert.Equal(["B"], service.GetRecent().Select(r => r.Name));
    }
}
