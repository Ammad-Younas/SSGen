using SSGen.Core.Data;
using SSGen.Core.Projects;
using SSGen.Emulator.Apk;
using SSGen.Emulator.Devices;
using SSGen.Emulator.Mirror;

namespace SSGen.Emulator.Tests;

public class EmulatorDiscoveryTests
{
    [Fact]
    public void Finds_grpc_endpoint_by_console_port()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("pid_111.ini"), "port.serial=5556\nport.adb=5557\ngrpc.port=8600\ngrpc.token=other\n");
        File.WriteAllText(dir.Combine("pid_222.ini"), "avd.name=X\nport.serial=5554\nport.adb=5555\ngrpc.token=abc123\ngrpc.port=8554\n");
        File.WriteAllText(dir.Combine("pid_333.ini"), "port.serial=5558\n"); // emulator without gRPC

        Assert.Equal(new EmulatorGrpcEndpoint(8554, "abc123"), EmulatorDiscovery.Find(5554, dir.Path));
        Assert.Null(EmulatorDiscovery.Find(5558, dir.Path));
        Assert.Null(EmulatorDiscovery.Find(5560, dir.Path));
        Assert.Null(EmulatorDiscovery.Find(5554, dir.Combine("missing")));
    }

    [Theory]
    [InlineData(AndroidKey.Back, "GoBack")]
    [InlineData(AndroidKey.Home, "GoHome")]
    [InlineData(AndroidKey.AppSwitch, "AppSwitch")]
    [InlineData(AndroidKey.Delete, "Backspace")]
    public void Maps_keys_to_dom_names(AndroidKey key, string expected) => Assert.Equal(expected, EmulatorGrpcMirror.DomKey(key));
}

public class ProjectPersistenceTests
{
    private static Project NewProject(TempDir dir)
    {
        var appDb = new SqliteDatabase(dir.Combine("app.db"), MigrationSet.App);
        appDb.Migrate();
        return new ProjectService(appDb).Create("P", dir.Path);
    }

    [Fact]
    public void Screens_are_saved_and_listed()
    {
        using var dir = new TempDir();
        var project = NewProject(dir);

        var a = ScreenRepository.Add(project, [1, 2, 3], "manual", "pkg/.Main", 1080, 2400);
        ScreenRepository.Add(project, [4], "agent", null, 720, 1280);

        var list = ScreenRepository.List(project);
        Assert.Equal(2, list.Count);
        Assert.Equal(a.Id, list[0].Id);
        Assert.Equal("pkg/.Main", list[0].Activity);
        Assert.Null(list[1].Activity);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(list[0].FullPath(project)));
    }

    [Fact]
    public void Apk_import_round_trips_and_replaces_previous()
    {
        using var dir = new TempDir();
        var project = NewProject(dir);
        var source = dir.Combine("in.apk");
        File.WriteAllBytes(source, [9, 9, 9]);
        var info = new ApkInfo { PackageName = "com.example.app", VersionCode = 7, Label = "Example", Permissions = ["android.permission.CAMERA"], ParsedBy = "aapt2" };

        var stored = ProjectApkStore.Import(project, source, new ApkInspection(info, [0x89, 0x50], ["warn"]));
        Assert.Equal(Path.Combine("apk", "com.example.app-7.apk"), stored.ApkFile);
        Assert.True(File.Exists(stored.ApkPath(project)));
        Assert.True(File.Exists(stored.IconPath(project)));

        var loaded = ProjectApkStore.Load(project)!;
        Assert.Equal("Example", loaded.Info.Label);
        Assert.Equal(["android.permission.CAMERA"], loaded.Info.Permissions);
        Assert.Equal(["warn"], loaded.Warnings);

        ProjectApkStore.Import(project, source, new ApkInspection(info with { VersionCode = 8 }, null, []));
        Assert.Equal(["com.example.app-8.apk"], Directory.GetFiles(Path.Combine(project.Folder, "apk")).Select(Path.GetFileName));
    }
}
